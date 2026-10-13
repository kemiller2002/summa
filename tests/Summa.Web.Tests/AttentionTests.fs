/// The work queue, follow-up, payments inbox and CPA workspace (WI-0030
/// slice 5), each at its own address and acting through Summa's domain
/// commands (Lifecycle follow-up, Credits.allocateAcross, creditUnapplied,
/// the CSV exports).
module Summa.Web.Tests.AttentionTests

open System
open System.Text
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private at (day: DateTimeOffset) = { Now = day; Actor = "local-person" }
let private october = at (DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero))
let private december = at (DateTimeOffset(2026, 12, 15, 9, 0, 0, TimeSpan.Zero))

let private go hash =
    LocationChanged
        { Origin = "https://summa.example"
          Path = "/app/"
          Query = ""
          Hash = hash }

let private runAt ctx (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private run = runAt december

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
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

/// Two invoices for Acme issued on 2026-10-07 (100.00 and 250.00, Net 30),
/// opened in December when both are overdue.
let private books () =
    let issue rate (model: Model) =
        let opened = runAt october [ go "#/invoices/new?customer=CUST-0001" ] model |> fst
        let key = opened.Draft.Lines.Head.Key
        runAt october [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, rate); DraftSubmitted; DraftIssued ] opened |> fst

    runAt
        october
        [ Started(match go "" with LocationChanged p -> p | _ -> failwith "page")
          ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}""")
          Loaded None
          go "#/customers"
          CustomerNameChanged "Acme"
          CustomerAddressChanged "1 Main"
          CustomerTermsChanged "30"
          CustomerAdded ]
        initial
    |> fst
    |> issue "100"
    |> issue "250"

[<Fact>]
let ``the work queue lists what needs attention, each with where to act`` () =
    let work = run [ go "#/work" ] (books ()) |> fst
    let kinds = items "workItems" work |> List.map (field "kind")
    Assert.Contains("Overdue", kinds)
    Assert.Contains("PDF", kinds)
    Assert.Contains("Period", kinds)
    let overdue = items "workItems" work |> List.find (fun r -> field "kind" r = "Overdue")
    Assert.StartsWith("#/invoices/INV-", field "href" overdue)
    Assert.EndsWith("?tab=payments", field "href" overdue)

[<Fact>]
let ``reminders and disputes are follow-up only: the invoice and the books are unchanged`` () =
    let model = run [ go "#/follow-up" ] (books ())
    let model = fst model
    Assert.Equal(2, (items "followRows" model).Length)
    let entries = model.Books.Value.Books.Ledger.Entries.Count
    let reminded = run [ ReminderRecorded "INV-0001"; ReminderRecorded "INV-0001" ] model |> fst
    Assert.Equal("Reminded 2 times", items "followRows" reminded |> List.find (fun r -> field "id" r = "INV-0001") |> field "collection")
    Assert.Equal(entries, reminded.Books.Value.Books.Ledger.Entries.Count)

    let invoice = run [ go "#/invoices/INV-0002?tab=payments" ] reminded |> fst
    Assert.Equal("Say what the customer disputes.", value "error" (run [ DisputeMarked ] invoice |> fst))
    let disputed = run [ FollowUpNoteChanged "Hours not agreed"; DisputeMarked ] invoice |> fst
    Assert.Equal("True", value "fuDisputed" disputed)
    // On the invoice's own page the reminder needs no key.
    Assert.Equal("Reminded once", value "fuCollection" (run [ ReminderRecorded "" ] disputed |> fst))

    let list, effects = run [ go "#/follow-up"; FollowUpShowChosen "disputed" ] disputed
    Assert.Contains(Navigate(NavigationEffect.Replace "/follow-up?show=disputed"), effects)
    Assert.Equal<string list>([ "INV-0002" ], items "followRows" list |> List.map (field "id"))
    let settled = run [ go "#/invoices/INV-0002?tab=payments"; FollowUpNoteChanged "Agreed"; DisputeSettled ] disputed |> fst
    Assert.Equal("False", value "fuDisputed" settled)
    Assert.StartsWith("Dispute resolved on", value "fuDispute" settled)

[<Fact>]
let ``a payment received on account waits in the inbox, then is applied oldest first or kept as credit`` () =
    let inbox = run [ go "#/inbox"; ReceiptCustomerChanged "CUST-0001"; ReceiptAmountChanged "300"; ReceiptReferenceChanged "CHK-9"; ReceiptRecorded ] (books ()) |> fst
    Assert.Equal("Payment recorded. Apply it, or keep it as credit.", value "notice" inbox)
    let row = items "inboxRows" inbox |> List.exactlyOne
    Assert.Equal("300.00 USD", field "unapplied" row)
    Assert.Equal("2 open invoices", field "openInvoices" row)

    let applied = run [ InboxApplied "PAY-0001" ] inbox |> fst
    Assert.Empty(items "inboxRows" applied)
    let owed id = applied.Books.Value.Books.Invoices[id] |> Summa.Ledger.Payments.outstanding applied.Books.Value
    // INV-0001 (100.00) is paid first, then 200.00 of INV-0002.
    Assert.Equal(0L, (owed "INV-0001").Minor)
    Assert.Equal(5000L, (owed "INV-0002").Minor)

    let credited = run [ ReceiptCustomerChanged "CUST-0001"; ReceiptAmountChanged "40"; ReceiptRecorded; InboxCredited "PAY-0002" ] applied |> fst
    Assert.Empty(items "inboxRows" credited)
    Assert.Equal("40.00 USD", value "custCredit" (run [ go "#/customers/CUST-0001" ] credited |> fst))
    Assert.Equal("Choose who paid.", value "error" (run [ ReceiptCustomerChanged ""; ReceiptAmountChanged "1"; ReceiptRecorded ] credited |> fst))

[<Fact>]
let ``the CPA workspace links the year's reports and downloads them as CSV`` () =
    let cpa = run [ go "#/cpa?year=2026" ] (books ()) |> fst
    Assert.Equal("#/reports/trial-balance?asOf=2026-12-31", value "cpaTrialHref" cpa)
    Assert.Equal("#/reports/income-statement?from=2026-01-01&to=2026-12-31&basis=cash", value "cpaCashIncomeHref" cpa)
    Assert.Equal("#/receivables?asOf=2026-12-31", value "cpaAgingHref" cpa)
    Assert.Equal("False", value "canExport" cpa)
    Assert.Equal("This browser cannot offer downloads here.", value "error" (run [ CpaExportRequested "journal" ] cpa |> fst))

    let withFiles = run [ PacksNegotiated { Files = true; Store = false } ] cpa |> fst

    match run [ CpaExportRequested "trial-balance" ] withFiles |> snd with
    | [ OfferDownload("trial-balance-2026.csv", "text/csv", data) ] ->
        let text = Encoding.UTF8.GetString(Convert.FromBase64String data)
        // Each export says what it is (SUM3-026), then the columns.
        Assert.StartsWith("# Export: trial-balance\n# SchemaVersion: summa.export/2\n# GeneratedAt: ", text)
        Assert.Contains("# AccountingPeriod: 2026-01-01..2026-12-31\n", text)
        Assert.Matches("# DataVersion: sha256:[0-9a-f]{64}\n", text)
        Assert.Contains("# Filters: currency=USD; year=2026\n# Classification: confidential financial data\nCode,Account,Debit,Credit\n", text)
        // The same books give the same data version.
        let again = run [ CpaExportRequested "trial-balance" ] withFiles |> snd

        match again with
        | [ OfferDownload(_, _, data') ] -> Assert.Equal(data, data')
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

    match run [ CpaExportRequested "journal" ] withFiles |> snd with
    | [ OfferDownload("journal-2026.csv", "text/csv", data) ] -> Assert.Contains("JE-INV-0002", Encoding.UTF8.GetString(Convert.FromBase64String data))
    | other -> failwith $"%A{other}"

[<Fact>]
let ``invoices download as CSV from the CPA workspace, and one invoice as JSON or HTML that says what it is`` () =
    let cpa = run [ go "#/cpa?year=2026"; PacksNegotiated { Files = true; Store = false } ] (books ()) |> fst

    match run [ CpaExportRequested "invoices" ] cpa |> snd with
    | [ OfferDownload("invoices-2026.csv", "text/csv", data) ] ->
        let text = Encoding.UTF8.GetString(Convert.FromBase64String data)
        Assert.StartsWith("# Export: invoices\n", text)
        Assert.Contains("\nInvoice,Number,Version,Customer,", text)
        Assert.Contains("INV-0001,", text)
    | other -> failwith $"%A{other}"

    let invoice = run [ go "#/invoices/INV-0001" ] cpa |> fst
    Assert.Equal("True", value "canExport" invoice)

    match run [ InvoiceExportRequested "json" ] invoice |> snd with
    | [ OfferDownload(name, "application/json", data) ] ->
        Assert.EndsWith(".json", name)
        let text = Encoding.UTF8.GetString(Convert.FromBase64String data)
        Assert.StartsWith("{\"schema\":\"summa.invoice-export/2\",\"classification\":\"confidential financial data\",", text)
        Assert.Contains("\"invoiceId\":\"INV-0001\"", text)
    | other -> failwith $"%A{other}"

    match run [ InvoiceExportRequested "html" ] invoice |> snd with
    | [ OfferDownload(_, "text/html", data) ] -> Assert.StartsWith("<!-- summa.invoice-export/2; classification=confidential financial data; ", Encoding.UTF8.GetString(Convert.FromBase64String data))
    | other -> failwith $"%A{other}"

[<Fact>]
let ``INV-OPS-006: invoice obligations feed the work queue, each with where to act`` () =
    let context: Summa.Ledger.Ledger.Context = { Who = "agent"; When = december.Now; Source = "test"; CorrelationId = None; Provenance = None }
    let ok r = match r with Ok v -> v | Error e -> failwith $"%A{e}"
    let base' = books ()

    // An invoice disputed, a draft waiting for approval, a proposal ready
    // for review, the PDF of another invoice failed, and a source that
    // Chrona corrected after it was invoiced.
    let disputed = runAt october [ go "#/invoices/INV-0002?tab=payments"; FollowUpNoteChanged "Hours not agreed"; DisputeMarked ] base' |> fst
    let opened = runAt october [ go "#/invoices/new?customer=CUST-0001" ] disputed |> fst
    let key = opened.Draft.Lines.Head.Key
    let awaiting = runAt october [ LineDescriptionChanged(key, "More work"); LineRateChanged(key, "75"); DraftSubmitted ] opened |> fst
    let r = awaiting.Books.Value
    let revenue = r.Books.Ledger.Accounts |> Map.findKey (fun _ a -> a.Code = "4000")

    let request: Summa.Ledger.Billing.ProposalRequest =
        { ProposalId = "P-1"
          CustomerId = "CUST-0001"
          EngagementId = None
          Currency = "USD"
          Time = []
          Grouping = []
          FixedFee = false
          Milestones = []
          Expenses = []
          Manual = [ { r.Books.Invoices["INV-0001"].Lines.Head with RevenueAccountId = revenue } ]
          Accounts = { TimeRevenue = revenue; FeeRevenue = revenue; ReimbursedExpenses = revenue } }

    let r =
        r
        |> Summa.Ledger.Billing.propose context request |> ok
        |> Summa.Ledger.Billing.markReady context false "P-1" |> ok
        |> Summa.Ledger.Issuance.pdfFailed context "INV-0001" "renderer unavailable" |> ok

    // A billing review as Chrona import raises it (Billing.importTime/withdrawTime, BillingTests).
    let review: Summa.Ledger.Sources.BillingReview =
        { Id = "RV-1"; PublicationId = "pub-1"; InvoiceId = "INV-0002"; Reason = "Chrona corrected time after it was invoiced"; RaisedAt = december.Now; Resolution = None }

    let r = { r with Books = { r.Books with Reviews = r.Books.Reviews.Add(review.Id, review) } }
    let work = run [ go "#/work" ] { awaiting with Books = Some r } |> fst
    let rows = items "workItems" work
    let row kind = rows |> List.filter (fun x -> field "kind" x = kind)

    let expect kind (href: string) (text: string) =
        match row kind with
        | [ x ] ->
            Assert.Equal(href, field "href" x)
            Assert.Contains(text, field "text" x)
        | other -> failwith $"{kind}: %A{other}"

    expect "Proposal ready" "#/proposals/P-1" "P-1"
    let waiting =
        r.Books.Drafts |> Map.toList |> List.filter (fun (_, d) -> d.Review.IsSubmittedForReview) |> List.map fst |> List.exactlyOne

    expect "Ready to issue" (Summa.Web.Engine.Routes.href (Summa.Web.Engine.Routes.Draft waiting)) "reviewed"
    expect "Source conflict" "#/invoices/INV-0002?tab=payments" "Chrona corrected time"
    expect "Delivery" "#/invoices/INV-0001?tab=payments" "the PDF failed: renderer unavailable"
    expect "Disputed" "#/invoices/INV-0002?tab=payments" "Hours not agreed"
    Assert.Contains("Overdue", rows |> List.map (field "kind"))
    // A settled review leaves the queue.
    let resolved = { r with Books = Summa.Ledger.Billing.resolveReview context "RV-1" "No action" r.Books |> ok }
    Assert.Empty(run [ go "#/work" ] { awaiting with Books = Some resolved } |> fst |> items "workItems" |> List.filter (fun x -> field "kind" x = "Source conflict"))

[<Fact>]
let ``SUM3-032: each integration's state is shown in one vocabulary, with what to do, and an outage changes nothing`` () =
    let r = (books ()).Books.Value
    let names states = states |> List.map (fun (name, state, _, _) -> name, state)

    // Local books, nothing sent or failed: every integration has succeeded.
    Assert.Equal<(string * Summa.Storage.Integrations.IntegrationState) list>(
        [ "Books storage", Summa.Storage.Integrations.Succeeded
          "Invoice delivery", Summa.Storage.Integrations.Succeeded
          "Time from Chrona", Summa.Storage.Integrations.Succeeded ],
        integrationStates false noneUnsent r |> names
    )

    // On GitHub: changes waiting are pending; a refused change needs a person.
    let waiting = { noneUnsent with Waiting = 2 }
    Assert.Equal(Summa.Storage.Integrations.Pending, integrationStates true waiting r |> List.head |> fun (_, s, _, _) -> s)

    let refused =
        { noneUnsent with
            Waiting = 1
            Blocked = Some(1L, "add a customer", "GitHub refused it (forbidden).")
            BlockedState = Some(Summa.Storage.Integrations.PermanentFailure "forbidden") }

    match integrationStates true refused r |> List.head with
    | "GitHub", Summa.Storage.Integrations.PermanentFailure _, detail, _ -> Assert.Equal("add a customer: GitHub refused it (forbidden).", detail)
    | other -> failwith $"%A{other}"

    // A failed delivery: retryable, or permanent for a bounce; the invoice is unchanged.
    let context: Summa.Ledger.Ledger.Context = { Who = "local-person"; When = december.Now; Source = "test"; CorrelationId = None; Provenance = None }
    let ok r = match r with Ok v -> v | Error e -> failwith $"%A{e}"
    let attempt =
        Summa.Ledger.Lifecycle.recordManual context (Summa.Ledger.Lifecycle.DeliveryAuthority(fun _ -> true)) "DL-1" "INV-0001" { To = [ "ap@acme.example" ]; Cc = []; ReplyTo = None } "email" r |> ok

    let failedOutcome outcome = { attempt with Books = { attempt.Books with Deliveries = attempt.Books.Deliveries |> Map.map (fun _ d -> { d with Outcome = outcome }) } }
    let delivery states = states |> List.item 1 |> fun (_, s, d, h) -> s, d, h

    match integrationStates false noneUnsent (failedOutcome (Summa.Ledger.Invoicing.DeliveryFailed "timeout")) |> delivery with
    | Summa.Storage.Integrations.Retryable "timeout", detail, href ->
        Assert.Contains("sending it again may succeed", detail)
        Assert.StartsWith("#/invoices/INV-0001", href)
    | other -> failwith $"%A{other}"

    let bounced = failedOutcome (Summa.Ledger.Invoicing.Bounced "mailbox unknown")

    match integrationStates false noneUnsent bounced |> delivery with
    | Summa.Storage.Integrations.PermanentFailure "mailbox unknown", detail, _ -> Assert.Contains("could not be delivered", detail)
    | other -> failwith $"%A{other}"

    Assert.True((bounced.Books.Invoices = r.Books.Invoices))
    Assert.True((bounced.Books.Ledger.Entries = r.Books.Ledger.Entries))

    // On the work page, as a table.
    let work = run [ go "#/work" ] { (books ()) with Books = Some bounced } |> fst
    Assert.Equal<string list>([ "Succeeded"; "Needs a person"; "Succeeded" ], items "integrations" work |> List.map (field "state"))

[<Fact>]
let ``SUM3-024: every financial export says it is confidential, and goes only to the download: nothing is kept`` () =
    let cpa = run [ go "#/cpa?year=2026"; PacksNegotiated { Files = true; Store = false } ] (books ()) |> fst

    for kind in [ "trial-balance"; "journal"; "invoices" ] do
        match run [ CpaExportRequested kind ] cpa with
        | after, [ OfferDownload(_, "text/csv", data) ] ->
            Assert.Contains("\n# Classification: confidential financial data\n", Encoding.UTF8.GetString(Convert.FromBase64String data))
            // Nothing else: no save, no copy in the tab or browser storage, no change to the books.
            Assert.True((after.Books = cpa.Books))
        | _, other -> failwith $"{kind}: %A{other}"

    let invoice = run [ go "#/invoices/INV-0001" ] cpa |> fst

    for format in [ "json"; "html" ] do
        match run [ InvoiceExportRequested format ] invoice with
        | after, [ OfferDownload(name, _, data) ] ->
            Assert.Contains("confidential financial data", Encoding.UTF8.GetString(Convert.FromBase64String data))
            // Named by the invoice's number only: no customer data in the file name.
            Assert.StartsWith("INV-", name)
            Assert.True((after.Books = invoice.Books))
        | _, other -> failwith $"{format}: %A{other}"
