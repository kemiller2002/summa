/// The general ledger, journal entries, reports and accounting periods in
/// the accounting application (WI-0030 slice 3), each with its account,
/// dates and basis in the link (WI-0043), computed by Summa's own reports.
module Summa.Web.Tests.BooksTests

open System
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private go hash =
    LocationChanged
        { Origin = "https://summa.example"
          Path = "/app/"
          Query = ""
          Hash = hash }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | Some(_, Value(Number n)) -> string n
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows
    | _ -> failwith $"no list view value {name}"

let private field name (row: (string * Scalar) list) =
    match row |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Text t) -> t
    | Some(_, Flag f) -> string f
    | other -> failwith $"%A{other}"

/// One 1,000.00 invoice issued on 2026-10-07, 400.00 of it paid.
let private books () =
    let opened =
        run
            [ Started(match go "" with LocationChanged page -> page | _ -> failwith "page")
              ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}""")
              Loaded None
              go "#/customers"
              CustomerNameChanged "Acme"
              CustomerAddressChanged "1 Main Street"
              CustomerAdded
              go "#/invoices/new?customer=CUST-0001" ]
            initial
        |> fst

    let key = opened.Draft.Lines.Head.Key

    run
        [ LineDescriptionChanged(key, "Assessment")
          LineRateChanged(key, "1000")
          DraftSubmitted
          DraftIssued
          InvoiceTabChosen "payments"
          PaymentAmountChanged "400"
          PaymentRecorded ]
        opened
    |> fst

[<Fact>]
let ``the journal lists every entry, and an account's ledger runs its balance`` () =
    let model = books ()
    let journal = run [ go "#/ledger" ] model |> fst
    Assert.Equal<string list>([ "#/ledger/entries/JE-INV-0001"; "#/ledger/entries/JE-PAY-0001" ], items "journalRows" journal |> List.map (field "href"))

    let receivable, effects = run [ LedgerAccountChosen "1100" ] journal
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/ledger?account=1100") ], effects)
    Assert.Equal("1100 Accounts Receivable", value "ledgerAccountName" receivable)
    Assert.Equal<string list>([ "1,000.00"; "600.00" ], items "ledgerRows" receivable |> List.map (field "balance"))

    // A later start brings the balance forward.
    let later = run [ go "#/ledger?account=1100&from=2026-10-08" ] model |> fst
    Assert.Empty(items "ledgerRows" later)
    Assert.Equal("600.00", value "ledgerOpening" later)
    Assert.Equal("True", value "isNotFound" (run [ go "#/ledger?account=9999" ] model |> fst))

[<Fact>]
let ``a journal entry shows its balanced lines, each linking to its account`` () =
    let entry = run [ go "#/ledger/entries/JE-PAY-0001" ] (books ()) |> fst
    Assert.Equal<string list>([ "1000 Operating Cash"; "1100 Accounts Receivable" ], items "entryLines" entry |> List.map (field "account"))
    Assert.Equal("#/ledger?account=1000", items "entryLines" entry |> List.head |> field "href")
    Assert.Equal(value "entryDebits" entry, value "entryCredits" entry)
    Assert.Equal("True", value "isNotFound" (run [ go "#/ledger/entries/JE-NONE" ] entry |> fst))

[<Fact>]
let ``the trial balance and balance sheet are as of the date in the link, and balance`` () =
    let model = books ()
    let trial = run [ go "#/reports/trial-balance" ] model |> fst
    Assert.Equal("True", value "trialBalances" trial)
    Assert.Equal("1,000.00 USD", value "trialDebits" trial)
    Assert.Equal("#/ledger?account=1100&to=2026-10-07", items "trialRows" trial |> List.find (fun r -> field "code" r = "1100") |> field "href")
    let before, effects = run [ ReportAsOfChanged "2026-10-06" ] trial
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/reports/trial-balance?asOf=2026-10-06") ], effects)
    Assert.Empty(items "trialRows" before)

    let sheet = run [ go "#/reports/balance-sheet?asOf=2026-10-07" ] model |> fst
    Assert.Equal("True", value "sheetBalances" sheet)
    Assert.Equal("1,000.00 USD", value "sheetAssets" sheet)
    Assert.Equal("1,000.00 USD", value "sheetEarnings" sheet)

[<Fact>]
let ``the income statement follows the basis in the link: accrual books the invoice, cash only what was received`` () =
    let accrual = run [ go "#/reports/income-statement?from=2026-10-01&to=2026-10-31" ] (books ()) |> fst
    Assert.Equal("1,000.00 USD", value "incomeRevenue" accrual)
    Assert.Equal("Accrual basis", value "incomeBasisLabel" accrual)
    let cash, effects = run [ IncomeBasisChosen "cash" ] accrual
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/reports/income-statement?from=2026-10-01&to=2026-10-31&basis=cash") ], effects)
    Assert.Equal("400.00 USD", value "incomeRevenue" cash)
    Assert.Equal("400.00 USD", value "incomeNet" cash)

[<Fact>]
let ``a closed period refuses new invoices until it is reopened`` () =
    let model = books ()
    let periods = run [ go "#/periods?year=2026" ] model |> fst
    Assert.Equal("Open", items "months" periods |> List.find (fun m -> field "key" m = "2026-10") |> field "state")
    Assert.Equal("2", items "months" periods |> List.find (fun m -> field "key" m = "2026-10") |> field "entries")

    let october = run [ go "#/periods/2026-10" ] model |> fst
    Assert.Equal<string list>([ "ok"; "ok"; "ok" ], items "periodChecks" october |> List.map (field "tone"))
    let closed = run [ PeriodClosed ] october |> fst
    Assert.Equal("Closed", value "periodState" closed)
    Assert.Equal("True", value "canReopenPeriod" closed)

    // Issuing into a closed period is refused by the issue command (INV-ISS-006).
    let opened = run [ go "#/invoices/new?customer=CUST-0001" ] closed |> fst
    let key = opened.Draft.Lines.Head.Key
    let refused = run [ LineDescriptionChanged(key, "More"); LineRateChanged(key, "10"); DraftSubmitted; DraftIssued ] opened |> fst
    Assert.Equal(1, refused.Books.Value.Books.Invoices.Count)

    let reopened = run [ go "#/periods/2026-10"; PeriodReopened ] refused |> fst
    Assert.Equal("Open", value "periodState" reopened)
    Assert.Equal("October 2026 is open again.", value "notice" reopened)

[<Fact>]
let ``an entry links back to what posted it, and the invoice links to its entry`` () =
    let model = books ()
    let invoiceEntry = run [ go "#/ledger/entries/JE-INV-0001" ] model |> fst
    Assert.Equal("#/invoices/INV-0001", value "entryOriginHref" invoiceEntry)
    Assert.Equal("#/payments/PAY-0001", value "entryOriginHref" (run [ go "#/ledger/entries/JE-PAY-0001" ] model |> fst))
    Assert.Equal("#/ledger/entries/JE-INV-0001", value "detailEntryHref" (run [ go "#/invoices/INV-0001?tab=history" ] model |> fst))

[<Fact>]
let ``the income statement drills into each account's ledger and offers the common date ranges`` () =
    let model = run [ go "#/reports/income-statement?from=2026-10-01&to=2026-10-31" ] (books ()) |> fst
    let revenue = items "incomeAccounts" model |> List.exactlyOne
    Assert.Equal("4000 Consulting Revenue", field "account" revenue)
    Assert.Equal("#/ledger?account=4000&from=2026-10-01&to=2026-10-31", field "href" revenue)

    let lastMonth, effects = run [ IncomeRangeChosen "last-month" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/reports/income-statement?from=2026-09-01&to=2026-09-30") ], effects)
    Assert.Equal("0.00 USD", value "incomeRevenue" lastMonth)
    Assert.Equal("true", items "incomeRanges" lastMonth |> List.find (fun r -> field "value" r = "last-month") |> field "selected")
    Assert.Equal(Some(DateOnly(2026, 7, 1), DateOnly(2026, 9, 30)), dateRange (DateOnly(2026, 10, 7)) "last-quarter")
    Assert.Equal(Some(DateOnly(2025, 1, 1), DateOnly(2025, 12, 31)), dateRange (DateOnly(2026, 10, 7)) "last-year")
    Assert.Equal(None, dateRange (DateOnly(2026, 10, 7)) "someday")

[<Fact>]
let ``a period with a payment not yet applied cannot be closed, and says why`` () =
    let model = books ()

    let payment: Summa.Ledger.Payments.Payment =
        { Id = "PAY-0002"
          CustomerId = "CUST-0001"
          DateReceived = DateOnly(2026, 10, 7)
          Amount = Summa.Ledger.Money.usd 5000L
          Method = Summa.Ledger.Payments.Ach
          Reference = "unapplied"
          Memo = None }

    let context: Summa.Ledger.Ledger.Context = { Who = "test"; When = ctx.Now; Source = "test"; CorrelationId = None }

    let withUnapplied =
        match Summa.Ledger.Payments.recordPayment context payment model.Books.Value with
        | Ok r -> { model with Books = Some r }
        | Error e -> failwith $"%A{e}"

    let october = run [ go "#/periods/2026-10" ] withUnapplied |> fst
    Assert.Equal("True", value "isPeriodBlocked" october)
    Assert.Equal("False", value "canClosePeriod" october)
    Assert.Equal<string list>([ "One payment received this month is not fully applied." ], items "periodBlockers" october |> List.map (field "blocker"))
    let refused = run [ PeriodClosed ] october |> fst
    Assert.Equal("Open", value "periodState" refused)
    Assert.Equal("This period is not ready to close. Resolve what is listed first.", value "error" refused)
