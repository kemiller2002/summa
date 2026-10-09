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
        Assert.StartsWith("# Export: trial-balance\n# SchemaVersion: summa.export/1\n# GeneratedAt: ", text)
        Assert.Contains("# AccountingPeriod: 2026-01-01..2026-12-31\n", text)
        Assert.Matches("# DataVersion: sha256:[0-9a-f]{64}\n", text)
        Assert.Contains("# Filters: currency=USD; year=2026\nCode,Account,Debit,Credit\n", text)
        // The same books give the same data version.
        let again = run [ CpaExportRequested "trial-balance" ] withFiles |> snd

        match again with
        | [ OfferDownload(_, _, data') ] -> Assert.Equal(data, data')
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

    match run [ CpaExportRequested "journal" ] withFiles |> snd with
    | [ OfferDownload("journal-2026.csv", "text/csv", data) ] -> Assert.Contains("JE-INV-0002", Encoding.UTF8.GetString(Convert.FromBase64String data))
    | other -> failwith $"%A{other}"
