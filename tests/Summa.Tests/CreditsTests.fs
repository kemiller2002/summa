/// v0.2 receivables edge cases (WI-0024): one payment across several
/// invoices, overpayments, deposits, credit memos, refunds, bounced
/// payments, write-offs and duplicate detection (§3-5, §9-12, §25).
module Summa.Tests.CreditsTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Credits
open Summa.Ledger.Reports
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let accountsFor =
    { Cash = "cash"
      Receivable = "ar"
      CustomerCredits = "credits"
      CustomerDeposits = "deposits"
      BadDebt = "baddebt" }

let private extra =
    [ { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Liability; Active = true }
      { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Liability; Active = true }
      { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Expense; Active = true } ]

/// INV-001 for 6,050.00 issued, with the extra accounts.
let private books () =
    let r = receivables ()
    let ledger = extra |> List.fold (fun l a -> addAccount context a l |> ok) r.Books.Ledger
    { r with Books = { r.Books with Ledger = ledger } }

let private secondInvoice (r: Receivables) =
    let second = { request with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002" }
    let withDraft = saveDraft context { draftFor [ consulting 2000L 20000L ] with DraftId = "D-2" } r.Books |> ok
    let b, _ = issue context second withDraft |> ok
    { r with Books = b }

let private day = DateOnly(2026, 10, 25)
let private balancesOk (r: Receivables) =
    let tb = trialBalance "USD" (DateOnly(2026, 12, 31)) r.Books.Ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)

[<Fact>]
let ``one payment across several invoices, all or none, with the remainder visible`` () =
    let r = books () |> secondInvoice |> recordPayment context (payment "PAY-1" 700000L) |> ok
    let across = [ allocation "AL-1" "PAY-1" "INV-001" 605000L; { allocation "AL-2" "PAY-1" "INV-002" 40000L with JournalEntryId = "JE-AL-2b" } ]
    let done' = allocateAcross context across r |> ok
    Assert.Equal(Paid, status done' done'.Books.Invoices["INV-001"])
    Assert.Equal(Paid, status done' done'.Books.Invoices["INV-002"])
    Assert.Equal(usd 55000L, unallocated done' done'.Payments["PAY-1"])
    // One allocation too many fails the whole command; nothing changes.
    let tooMuch = across @ [ { allocation "AL-3" "PAY-1" "INV-001" 1L with JournalEntryId = "JE-AL-3" } ]
    Assert.True(Result.isError (allocateAcross context tooMuch r))
    balancesOk done'

[<Fact>]
let ``an overpayment becomes the customer's credit, never revenue, and can pay a later invoice`` () =
    let r = books () |> recordPayment context (payment "PAY-1" 700000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) |> ok
    let credited = creditUnapplied context accountsFor { CreditId = "CR-1"; PaymentId = "PAY-1"; Date = day; JournalEntryId = "JE-CR-1" } r |> ok
    Assert.Equal(usd 95000L, credited.Credits["CR-1"].Amount)
    Assert.Equal(usd 0L, unallocated credited credited.Payments["PAY-1"])
    // Revenue is unchanged; the credit is a liability.
    Assert.Equal(usd 605000L, balance credited.Books.Ledger "USD" day "revenue")
    Assert.Equal(usd 95000L, balance credited.Books.Ledger "USD" day "credits")
    Assert.Equal<(CreditSource * Money) list>([ FromCredit "CR-1", usd 95000L ], available credited "CUST-ABC")
    // Applying it to the next invoice.
    let later = secondInvoice credited
    let applied = apply context accountsFor { ApplicationId = "AP-1"; Source = FromCredit "CR-1"; InvoiceId = "INV-002"; Amount = usd 40000L; Date = day; JournalEntryId = "JE-AP-1" } later |> ok
    Assert.Equal(Paid, status applied applied.Books.Invoices["INV-002"])
    Assert.Equal(Some(usd 55000L), remaining applied (FromCredit "CR-1"))
    Assert.Equal<CreditProblem list>([ NothingUnallocated "PAY-1" ], creditUnapplied context accountsFor { CreditId = "CR-2"; PaymentId = "PAY-1"; Date = day; JournalEntryId = "JE-CR-2" } applied |> refused)
    balancesOk applied

[<Fact>]
let ``a deposit is a liability until applied, traceable by customer`` () =
    let deposit = { Id = "DEP-1"; CustomerId = "CUST-ABC"; DateReceived = day; Amount = usd 1000000L; Method = Wire; Reference = "W-1"; JournalEntryId = "JE-DEP-1" }
    let r = books () |> recordDeposit context accountsFor deposit |> ok
    Assert.Equal(usd 605000L, balance r.Books.Ledger "USD" day "revenue")
    Assert.Equal(usd 1000000L, balance r.Books.Ledger "USD" day "deposits")
    Assert.Equal(Issued, status r r.Books.Invoices["INV-001"])
    let applied = apply context accountsFor { ApplicationId = "AP-1"; Source = FromDeposit "DEP-1"; InvoiceId = "INV-001"; Amount = usd 605000L; Date = day; JournalEntryId = "JE-AP-1" } r |> ok
    Assert.Equal(Paid, status applied applied.Books.Invoices["INV-001"])
    Assert.Equal(Some(usd 395000L), remaining applied (FromDeposit "DEP-1"))
    Assert.Equal<CreditProblem list>([ ExceedsRemaining(usd 395000L) ], apply context accountsFor { ApplicationId = "AP-2"; Source = FromDeposit "DEP-1"; InvoiceId = "INV-001"; Amount = usd 400000L; Date = day; JournalEntryId = "JE-AP-2" } applied |> refused)
    // Retrying is idempotent; reusing the id differently is refused.
    Assert.True(Result.isOk (recordDeposit context accountsFor deposit applied))
    Assert.Equal<CreditProblem list>([ IdReused "DEP-1" ], recordDeposit context accountsFor { deposit with Amount = usd 1L } applied |> refused)
    balancesOk applied

[<Fact>]
let ``a credit memo has its own id, reduces revenue and the customer's balance`` () =
    let memo = { Id = "CM-1"; CustomerId = "CUST-ABC"; InvoiceId = Some "INV-001"; Amount = usd 50000L; RevenueAccountId = "revenue"; Reason = "Agreed discount on assessment"; IssueDate = day; JournalEntryId = "JE-CM-1"; Lines = [] }
    let r = books () |> issueCreditMemo context accountsFor memo |> ok
    Assert.Equal(usd 555000L, balance r.Books.Ledger "USD" day "revenue")
    let applied = apply context accountsFor { ApplicationId = "AP-1"; Source = FromCreditMemo "CM-1"; InvoiceId = "INV-001"; Amount = usd 50000L; Date = day; JournalEntryId = "JE-AP-1" } r |> ok
    Assert.Equal(usd 555000L, outstanding applied applied.Books.Invoices["INV-001"])
    Assert.Equal(PartiallyPaid, status applied applied.Books.Invoices["INV-001"])
    Assert.Equal<CreditProblem list>([ ReasonRequired ], issueCreditMemo context accountsFor { memo with Id = "CM-2"; Reason = " " } r |> refused)
    Assert.Equal<CreditProblem list>([ NotARevenueAccount "cash" ], issueCreditMemo context accountsFor { memo with Id = "CM-3"; RevenueAccountId = "cash" } r |> refused)
    balancesOk applied

[<Fact>]
let ``a refund pays money out of a credit or deposit and never touches the payment`` () =
    let r = books () |> recordPayment context (payment "PAY-1" 700000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) |> ok
    let credited = creditUnapplied context accountsFor { CreditId = "CR-1"; PaymentId = "PAY-1"; Date = day; JournalEntryId = "JE-CR-1" } r |> ok
    let back = { Id = "RF-1"; CustomerId = "CUST-ABC"; Source = FromCredit "CR-1"; Amount = usd 95000L; Date = day; Method = Ach; Reference = "ACH-OUT-1"; JournalEntryId = "JE-RF-1" }
    let refunded = refund context accountsFor back credited |> ok
    Assert.Equal(Some(usd 0L), remaining refunded (FromCredit "CR-1"))
    Assert.Equal(usd 605000L, balance refunded.Books.Ledger "USD" day "cash")
    Assert.True(refunded.Payments.ContainsKey "PAY-1")
    Assert.Equal<CreditProblem list>([ ExceedsRemaining(usd 0L) ], refund context accountsFor { back with Id = "RF-2"; Amount = usd 1L } refunded |> refused)
    Assert.Equal<CreditProblem list>([ WrongCustomer ], refund context accountsFor { back with Id = "RF-3"; CustomerId = "CUST-XYZ" } credited |> refused)
    balancesOk refunded

[<Fact>]
let ``a bounced payment reverses its effects, keeps its history and reopens the invoice`` () =
    let r = books () |> recordPayment context (payment "PAY-1" 605000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) |> ok
    Assert.Equal(Paid, status r r.Books.Invoices["INV-001"])
    let reversed = reversePayment context accountsFor { PaymentId = "PAY-1"; Reason = "Returned ACH R01"; Date = day; JournalEntryPrefix = "JE-REV-PAY-1" } r |> ok
    Assert.True(reversed.Payments.ContainsKey "PAY-1")
    Assert.Equal(1, reversed.Allocations.Length)
    Assert.Equal(Issued, status reversed reversed.Books.Invoices["INV-001"])
    Assert.Equal(Open, obligationStatus reversed reversed.Books.Obligations["OBL-001"])
    Assert.Equal(usd 0L, balance reversed.Books.Ledger "USD" day "cash")
    Assert.Equal(usd 605000L, balance reversed.Books.Ledger "USD" day "ar")
    Assert.Equal<string list>([ "JE-REV-PAY-1-1" ], reversed.Reversals["PAY-1"].JournalEntryIds)
    Assert.Equal<CreditProblem list>([ AlreadyReversed "PAY-1" ], reversePayment context accountsFor { PaymentId = "PAY-1"; Reason = "again"; Date = day; JournalEntryPrefix = "X" } reversed |> refused)
    // The money it carried cannot be allocated again.
    Assert.True(Result.isError (allocate context (allocation "AL-2" "PAY-1" "INV-001" 1L) reversed))
    balancesOk reversed

[<Fact>]
let ``a payment whose credit was used cannot be reversed until that is undone`` () =
    let r = books () |> recordPayment context (payment "PAY-1" 700000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) |> ok
    let credited = creditUnapplied context accountsFor { CreditId = "CR-1"; PaymentId = "PAY-1"; Date = day; JournalEntryId = "JE-CR-1" } r |> ok
    let used = refund context accountsFor { Id = "RF-1"; CustomerId = "CUST-ABC"; Source = FromCredit "CR-1"; Amount = usd 1000L; Date = day; Method = Ach; Reference = "R"; JournalEntryId = "JE-RF-1" } credited |> ok
    Assert.Equal<CreditProblem list>([ CreditInUse "CR-1" ], reversePayment context accountsFor { PaymentId = "PAY-1"; Reason = "NSF"; Date = day; JournalEntryPrefix = "JE-REV" } used |> refused)
    // An unused credit is reversed with the payment.
    let reversed = reversePayment context accountsFor { PaymentId = "PAY-1"; Reason = "NSF"; Date = day; JournalEntryPrefix = "JE-REV" } credited |> ok
    Assert.Equal(2, reversed.Reversals["PAY-1"].JournalEntryIds.Length)
    Assert.Equal(usd 0L, balance reversed.Books.Ledger "USD" day "credits")
    balancesOk reversed

[<Fact>]
let ``a write-off is explicit, needs a reason and is reported apart from revenue`` () =
    let r = books () |> recordPayment context (payment "PAY-1" 600000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 600000L) |> ok
    let w = { Id = "WO-1"; InvoiceId = "INV-001"; Amount = usd 5000L; Reason = "Small balance; customer unresponsive"; Date = day; JournalEntryId = "JE-WO-1" }
    let written = writeOff context accountsFor w r |> ok
    Assert.Equal(WrittenOff, status written written.Books.Invoices["INV-001"])
    Assert.Equal(usd 5000L, badDebt "USD" written)
    Assert.Equal(usd 605000L, balance written.Books.Ledger "USD" day "revenue")
    Assert.Equal(usd 5000L, balance written.Books.Ledger "USD" day "baddebt")
    Assert.Equal<CreditProblem list>([ ReasonRequired ], writeOff context accountsFor { w with Id = "WO-2"; Reason = "" } r |> refused)
    Assert.Equal<CreditProblem list>([ ExceedsOutstandingBalance(usd 5000L) ], writeOff context accountsFor { w with Id = "WO-3"; Amount = usd 5001L } r |> refused)
    balancesOk written

[<Fact>]
let ``a duplicate payment returns the existing one instead of recording another`` () =
    let r = books ()
    let first, recorded = receive context (payment "PAY-1" 605000L) r |> ok
    let again, existing = receive context { payment "PAY-1" 605000L with Id = "PAY-2" } first |> ok
    Assert.Equal(recorded, existing)
    Assert.Equal(1, again.Payments.Count)
    // A different reference is a different payment.
    let other, _ = receive context { payment "PAY-1" 605000L with Id = "PAY-3"; Reference = "ACH-OTHER" } first |> ok
    Assert.Equal(2, other.Payments.Count)
