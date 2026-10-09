/// Opening balances, CPA adjustments, year-end closing and cash-basis
/// reporting (WI-0026, v0.2 §21-24).
module Summa.Tests.PeriodsTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Reports
open Summa.Ledger.Periods
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private extra =
    [ { Id = "retained"; Code = "3100"; Name = "Retained Earnings"; Type = Equity; Active = true }
      { Id = "salestax"; Code = "2300"; Name = "Sales Tax Payable"; Type = Liability; Active = true }
      { Id = "workshops"; Code = "4100"; Name = "Workshop Revenue"; Type = Revenue; Active = true }
      { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Liability; Active = true }
      { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Liability; Active = true }
      { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Expense; Active = true } ]

let private withExtra (ledger: Ledger) = extra |> List.fold (fun l a -> addAccount context a l |> ok) ledger
let private jan1 = DateOnly(2026, 1, 1)
let private yearEnd = DateOnly(2026, 12, 31)

let private opening =
    { EntryId = "JE-OPEN"
      MigrationDate = jan1
      Lines = [ "cash", Debit(usd 1000000L); "ar", Debit(usd 250000L); "equity", Credit(usd 500000L); "retained", Credit(usd 750000L) ]
      Reference = Some "QuickBooks export 2025-12-31" }

[<Fact>]
let ``opening balances are one identifiable entry on the migration date, balanced before posting`` () =
    let ledger = withExtra chart
    Assert.Equal(openingTotals "USD" opening |> fst, openingTotals "USD" opening |> snd)
    let opened = postOpeningBalances context opening ledger |> ok
    let entry = opened.Entries["JE-OPEN"]
    Assert.Equal(OpeningBalance jan1, kindOf entry)
    Assert.Equal(jan1, entry.Date)
    Assert.True(balances (balanceSheet "USD" jan1 opened))
    Assert.Equal(usd 1000000L, balance opened "USD" jan1 "cash")
    // Retrying is a no-op; a second migration, an unbalanced one, or income accounts are refused.
    Assert.Equal(opened, postOpeningBalances context opening opened |> ok)
    Assert.Equal<PeriodProblem list>([ AlreadyMigrated "JE-OPEN" ], postOpeningBalances context { opening with EntryId = "JE-OPEN-2" } opened |> refused)
    let unbalanced = { opening with Lines = [ "cash", Debit(usd 100L); "equity", Credit(usd 99L) ] }
    Assert.Equal<PeriodProblem list>([ PeriodPosting [ Unbalanced(usd 100L, usd 99L) ] ], postOpeningBalances context unbalanced ledger |> refused)
    let income = { opening with Lines = [ "cash", Debit(usd 100L); "revenue", Credit(usd 100L) ] }
    Assert.Equal<PeriodProblem list>([ NotBalanceSheetAccount "revenue" ], postOpeningBalances context income ledger |> refused)

[<Fact>]
let ``opening balances come before any other activity`` () =
    let earlier = { Date = DateOnly(2025, 12, 15); Description = "Old"; Lines = [ line "cash" (Debit(usd 100L)); line "equity" (Credit(usd 100L)) ]; Source = "manual" }
    let ledger = withExtra chart |> post context "old" "JE-OLD" earlier |> ok |> fst
    Assert.Equal<PeriodProblem list>([ ActivityBeforeMigration "JE-OLD" ], postOpeningBalances context opening ledger |> refused)

[<Fact>]
let ``adjusting entries are marked, attributed and carry the CPA's reference`` () =
    let cpa = { context with Who = "github:cpa-1" }

    let depreciation =
        { EntryId = "JE-ADJ-1"
          Date = yearEnd
          Description = "Depreciation of equipment, 2026"
          Kind = Depreciation
          Lines = [ line "software" (Debit(usd 120000L)); line "cash" (Credit(usd 120000L)) ]
          Reference = Some "WP-14.2" }

    let ledger = withExtra chart |> postAdjusting cpa depreciation |> ok
    Assert.Equal(Adjusting(Depreciation, Some "WP-14.2"), kindOf ledger.Entries["JE-ADJ-1"])
    Assert.Equal(Some "github:cpa-1", createdBy ledger "JE-ADJ-1")
    let noReference = ledger |> postAdjusting cpa { depreciation with EntryId = "JE-ADJ-2"; Kind = Reclassification; Reference = None } |> ok
    Assert.Equal(Adjusting(Reclassification, None), kindOf noReference.Entries["JE-ADJ-2"])
    // Operational entries stay operational.
    let books, invoice = issued ()
    Assert.Equal(Operational, kindOf books.Ledger.Entries[invoice.JournalEntryId])
    Assert.Equal<PeriodProblem list>([ DescriptionRequired ], postAdjusting cpa { depreciation with Description = " " } ledger |> refused)

[<Fact>]
let ``a proposed year-end close moves revenue and expenses into retained earnings, once`` () =
    let books, _ = issued ()
    let expense = { Date = DateOnly(2026, 11, 1); Description = "Subscription"; Lines = [ line "software" (Debit(usd 9900L)); line "cash" (Credit(usd 9900L)) ]; Source = "manual" }
    let ledger = withExtra books.Ledger |> post context "sub" "JE-SUB" expense |> ok |> fst
    let before = incomeStatement "USD" jan1 yearEnd ledger
    // Proposed, not posted: nothing changes until a person posts it.
    let proposal = closingEntry "USD" yearEnd "retained" ledger |> ok
    Assert.False(ledger.Entries |> Map.exists (fun _ e -> isClosingEntry e))
    Assert.Equal(3, proposal.Lines.Length)
    let closed = postClosing context "USD" yearEnd "retained" "JE-CLOSE-2026" ledger |> ok
    Assert.Equal(YearEndClose 2026, kindOf closed.Entries["JE-CLOSE-2026"])
    Assert.Equal(usd 0L, balance closed "USD" yearEnd "revenue")
    Assert.Equal(usd 0L, balance closed "USD" yearEnd "software")
    Assert.Equal(before.NetIncome, balance closed "USD" yearEnd "retained")
    // The year still reports what it earned; the balance sheet still balances.
    Assert.Equal(before, incomeStatement "USD" jan1 yearEnd closed)
    Assert.True(balances (balanceSheet "USD" yearEnd closed))
    Assert.Equal(usd 0L, (balanceSheet "USD" yearEnd closed).CurrentEarnings)
    Assert.Equal<PeriodProblem list>([ AlreadyClosed 2026 ], postClosing context "USD" yearEnd "retained" "JE-CLOSE-2" closed |> refused)
    Assert.Equal<PeriodProblem list>([ NothingToClose ], closingEntry "USD" (DateOnly(2027, 12, 31)) "retained" closed |> refused)
    Assert.Equal<PeriodProblem list>([ ClosingAccountNotEquity "cash" ], closingEntry "USD" yearEnd "cash" ledger |> refused)

[<Fact>]
let ``cash basis is a projection: revenue when cash arrives, never a second ledger`` () =
    let r = receivables ()
    let r = { r with Books = { r.Books with Ledger = withExtra r.Books.Ledger } }
    let october = DateOnly(2026, 10, 1), DateOnly(2026, 10, 31)
    // Issued in October for 6,050.00: accrual revenue is all of it, cash revenue nothing yet.
    Assert.Equal(usd 605000L, (incomeStatement "USD" (fst october) (snd october) r.Books.Ledger).Revenue)
    Assert.Equal(usd 0L, (cashBasis "USD" (fst october) (snd october) [ "cash" ] r).Revenue)
    let paid = r |> recordPayment context (payment "PAY-1" 200000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 200000L) |> ok
    let cash = cashBasis "USD" (fst october) (snd october) [ "cash" ] paid
    Assert.Equal(usd 200000L, cash.Revenue)
    Assert.Equal<(string * Money) list>([ "revenue", usd 200000L ], cash.RevenueByAccount)
    // The projection changed nothing in the books.
    Assert.True((r.Books.Ledger.Accounts = paid.Books.Ledger.Accounts))
    Assert.Equal(usd 605000L, (incomeStatement "USD" (fst october) (snd october) paid.Books.Ledger).Revenue)

[<Fact>]
let ``cash revenue splits exactly across revenue accounts and leaves tax and non-cash credits out`` () =
    let draft =
        { draftFor [ { consulting 1000L 10000L with Tax = Taxable "services" }; { consulting 1000L 5000L with RevenueAccountId = "workshops"; Description = "Workshop"; Tax = Taxable "services" } ] with
            Adjustments = [ { Kind = Tax(taxCharge "NY" "salestax"); Label = "Sales tax"; Amount = usd 1500L } ] }

    let issuedBooks, _ = issue context request (saveDraft context draft { books with Ledger = withExtra books.Ledger } |> ok) |> ok
    // 150.00 of revenue and 15.00 of tax; 100.00 received.
    let r = start issuedBooks |> recordPayment context (payment "PAY-1" 10000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 10000L) |> ok
    let cash = cashBasis "USD" (DateOnly(2026, 10, 1)) (DateOnly(2026, 10, 31)) [ "cash" ] r
    // 100 x 150/165 = 90.909...: 60.61 and 30.30 to the two accounts, the rest paid tax.
    Assert.Equal<(string * Money) list>([ "revenue", usd 6061L; "workshops", usd 3030L ], cash.RevenueByAccount)
    Assert.Equal(usd 9091L, cash.Revenue)
    // A credit memo applied to the invoice moves no cash.
    let accounts: Credits.ReceivableAccounts = { Cash = "cash"; Receivable = "ar"; CustomerCredits = "credits"; CustomerDeposits = "deposits"; BadDebt = "baddebt" }
    let memo: CreditMemo = { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 1000L; RevenueAccountId = "revenue"; Reason = "Goodwill"; IssueDate = DateOnly(2026, 10, 25); JournalEntryId = "JE-CM-1"; Lines = [] }
    let applied =
        r
        |> Credits.issueCreditMemo context accounts memo
        |> ok
        |> Credits.apply context accounts { ApplicationId = "AP-1"; Source = FromCreditMemo "CM-1"; InvoiceId = "INV-001"; Amount = usd 1000L; Date = DateOnly(2026, 10, 25); JournalEntryId = "JE-AP-1" }
        |> ok
    Assert.Equal(cash.Revenue, (cashBasis "USD" (DateOnly(2026, 10, 1)) (DateOnly(2026, 10, 31)) [ "cash" ] applied).Revenue)

[<Fact>]
let ``cash expenses are what left a cash account`` () =
    let paidCash = { Date = DateOnly(2026, 10, 5); Description = "Software"; Lines = [ line "software" (Debit(usd 9900L)); line "cash" (Credit(usd 9900L)) ]; Source = "manual" }
    let accrued = { Date = DateOnly(2026, 10, 6); Description = "Accrued software"; Lines = [ line "software" (Debit(usd 5000L)); line "equity" (Credit(usd 5000L)) ]; Source = "manual" }
    let ledger = withExtra chart |> post context "a" "JE-A" paidCash |> ok |> fst |> post context "b" "JE-B" accrued |> ok |> fst
    let r = start (openBooks ledger)
    let cash = cashBasis "USD" (DateOnly(2026, 10, 1)) (DateOnly(2026, 10, 31)) [ "cash" ] r
    Assert.Equal(usd 9900L, cash.Expenses)
    Assert.Equal(usd -9900L, cash.NetIncome)
