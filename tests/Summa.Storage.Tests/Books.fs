/// A small set of books to store: a chart of accounts, a customer, an
/// issued invoice, a payment allocated to it and a reversed manual entry.
module Summa.Storage.Tests.Books

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

let ledgerContext = { Who = "github:583231"; When = DateTimeOffset(2026, 10, 7, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567L); Source = "summa-test"; CorrelationId = Some "c-1" }

let ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"%A{problems}"

let accounts =
    [ { Id = "cash"; Code = "1000"; Name = "Operating Cash"; Type = Asset; Active = true }
      { Id = "ar"; Code = "1100"; Name = "Accounts Receivable"; Type = Asset; Active = true }
      { Id = "equity"; Code = "3000"; Name = "Owner Equity"; Type = Equity; Active = true }
      { Id = "revenue"; Code = "4000"; Name = "Consulting Revenue"; Type = Revenue; Active = true }
      { Id = "software"; Code = "6100"; Name = "Software"; Type = Expense; Active = true }
      { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Liability; Active = true }
      { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Liability; Active = true }
      { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Expense; Active = true } ]

let chart = accounts |> List.fold (fun ledger a -> addAccount ledgerContext a ledger |> ok) empty

let abc =
    { Id = "CUST-ABC"
      Name = "ABC Corp"
      BillingName = "ABC Corp Accounts Payable"
      BillingAddress = "1 Main St"
      Email = "ap@abc.example"
      DefaultTerms = Net 30
      Active = true }

let draft =
    { DraftId = "D-1"
      CustomerId = abc.Id
      Currency = "USD"
      Lines =
        [ { Description = "Architecture assessment"
            QuantityThousandths = 34500L
            UnitPrice = usd 17500L
            RevenueAccountId = "revenue"
            Project = Some "PRJ-ARCH"
            WorkItem = None } ]
      Adjustments = [ usd 1250L ]
      Terms = None
      DueDate = None }

let issueRequest =
    { DraftId = "D-1"
      IssueDate = DateOnly(2026, 10, 7)
      NumberOverride = None
      Prefix = "INV"
      ReceivableAccountId = "ar"
      InvoiceId = "INV-001"
      JournalEntryId = "JE-000001"
      ObligationId = "OBL-001" }

let payment =
    { Id = "PAY-1"
      CustomerId = abc.Id
      DateReceived = DateOnly(2026, 10, 20)
      Amount = usd 605000L
      Method = Ach
      Reference = "ACH-1"
      Memo = Some "October"; }

let allocation =
    { AllocationId = "AL-1"
      PaymentId = "PAY-1"
      InvoiceId = "INV-001"
      Amount = usd 605000L
      JournalEntryId = "JE-000002"
      CashAccountId = "cash"
      ReceivableAccountId = "ar" }

let manual =
    { Date = DateOnly(2026, 10, 8)
      Description = "Software subscription"
      Lines =
        [ { AccountId = "software"; Side = Debit(usd 9900L); Memo = None; Dimensions = noDimensions }
          { AccountId = "cash"; Side = Credit(usd 9900L); Memo = None; Dimensions = { noDimensions with Project = Some "OPS" } } ]
      Source = "manual" }

/// The whole slice in memory.
let full () =
    let books = openBooks chart |> saveCustomer ledgerContext abc |> saveDraft ledgerContext draft |> ok
    let books, _ = issue ledgerContext issueRequest books |> ok
    let books = markSent ledgerContext "INV-001" "ap@abc.example" books |> ok
    let ledger, _ = post ledgerContext "manual-1" "JE-000003" manual books.Ledger |> ok
    let ledger, _ = reverse ledgerContext "JE-000003" "JE-000004" (DateOnly(2026, 10, 9)) ledger |> ok
    let ledger = closePeriod ledgerContext (2026, 9) ledger

    start { books with Ledger = ledger }
    |> recordPayment ledgerContext payment
    |> ok
    |> allocate ledgerContext allocation
    |> ok

let receivableAccounts: Summa.Ledger.Credits.ReceivableAccounts =
    { Cash = "cash"
      Receivable = "ar"
      CustomerCredits = "credits"
      CustomerDeposits = "deposits"
      BadDebt = "baddebt" }

/// The slice plus every v0.2 receivables record: an overpayment credit, a
/// deposit, a credit memo, applications, a refund, a reversed payment and a
/// write-off.
let withCredits () =
    let open' = Summa.Ledger.Credits.creditUnapplied
    let day = DateOnly(2026, 10, 25)
    let r = full ()
    let r = recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 10000L; Reference = "ACH-2" } r |> ok
    let r = open' ledgerContext receivableAccounts { CreditId = "CR-1"; PaymentId = "PAY-2"; Date = day; JournalEntryId = "JE-CR-1" } r |> ok
    let r = Summa.Ledger.Credits.recordDeposit ledgerContext receivableAccounts { Id = "DEP-1"; CustomerId = abc.Id; DateReceived = day; Amount = usd 50000L; Method = Wire; Reference = "W-1"; JournalEntryId = "JE-DEP-1" } r |> ok
    let r = Summa.Ledger.Credits.issueCreditMemo ledgerContext receivableAccounts { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 2000L; RevenueAccountId = "revenue"; Reason = "Goodwill"; IssueDate = day; JournalEntryId = "JE-CM-1" } r |> ok
    let r = Summa.Ledger.Credits.refund ledgerContext receivableAccounts { Id = "RF-1"; CustomerId = abc.Id; Source = FromCredit "CR-1"; Amount = usd 4000L; Date = day; Method = Ach; Reference = "OUT-1"; JournalEntryId = "JE-RF-1" } r |> ok
    let r = recordPayment ledgerContext { payment with Id = "PAY-3"; Amount = usd 7000L; Reference = "CHK-3"; Method = Check } r |> ok
    Summa.Ledger.Credits.reversePayment ledgerContext receivableAccounts { PaymentId = "PAY-3"; Reason = "Bounced check"; Date = day; JournalEntryPrefix = "JE-REV-3" } r |> ok
