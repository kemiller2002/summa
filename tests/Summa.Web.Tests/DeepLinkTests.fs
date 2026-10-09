/// Deep links in the accounting application (WI-0041, SUM-LINK-001..012):
/// the engine adopts the address the kernel reports, links are hrefs, the
/// engine pushes for a new place and replaces for a refinement, bad links
/// say so, and "Copy link" copies the canonical address.
module Summa.Web.Tests.DeepLinkTests

open System
open System.Text.Json.Nodes
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
module Routes = Summa.Web.Engine.Routes
module Wire = Summa.Web.Application.AccountingWire

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private address hash : PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = ""
      Hash = hash }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private localConfig = """{"environment":"local","environmentName":"test"}"""

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

let private go hash = LocationChanged(address hash)

/// Opened at `hash`, with the books started.
let private openedAt hash =
    run [ Started(address hash); ConfigurationRead(Ok localConfig); Loaded None ] initial |> fst

/// Two customers, two issued invoices (one paid), and one draft.
let private books () =
    let customer name =
        [ go "#/customers"; CustomerNameChanged name; CustomerAddressChanged "1 Main Street"; CustomerTermsChanged "30"; CustomerAdded ]

    let invoice customerId (rate: string) =
        fun (model: Model) ->
            let opened = run [ go $"#/invoices/new?customer={customerId}" ] model |> fst
            let key = opened.Draft.Lines.Head.Key
            run [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, rate); DraftSubmitted; DraftIssued ] opened |> fst

    openedAt ""
    |> run (customer "Acme" @ customer "Beta")
    |> fst
    |> invoice "CUST-0001" "100"
    |> invoice "CUST-0002" "250"
    |> run [ go "#/invoices/INV-0002?tab=payments"; PaymentAmountChanged "250"; PaymentRecorded ]
    |> fst
    |> fun model ->
        let opened = run [ go "#/invoices/new" ] model |> fst
        let key = opened.Draft.Lines.Head.Key
        run [ DraftCustomerChanged "CUST-0001"; LineDescriptionChanged(key, "Later"); LineRateChanged(key, "75") ] opened |> fst
    |> run [ DraftSubmitted ]
    |> fst

// ---- Opening a link (SUM-LINK-001, 002) -------------------------------------------------------

[<Fact>]
let ``a deep link opens its view once the books are read`` () =
    let model = books ()
    let link = run [ Started(address "#/invoices/INV-0001?tab=history"); ConfigurationRead(Ok localConfig) ] initial |> fst
    let snapshot = Summa.Storage.LocalSnapshot.encode model.Manifest.Value model.Books.Value |> Result.defaultWith (fun e -> failwith $"%A{e}")
    let opened = update ctx (Loaded(Some snapshot)) link |> fst
    Assert.Equal("True", value "onInvoice" opened)
    Assert.Equal("True", value "onHistoryTab" opened)
    Assert.NotEmpty(items "history" opened)
    Assert.Equal("Invoices", items "navigation" opened |> List.find (fun row -> field "current" row = "page") |> field "label")

[<Fact>]
let ``a deep link to a draft opens the draft in the editor`` () =
    let model = books ()
    let opened = run [ go "#/"; go "#/drafts/D-0003" ] model |> fst
    Assert.Equal("True", value "onEditor" opened)
    Assert.Equal("Draft D-0003", value "draftLabel" opened)
    Assert.Equal("CUST-0001", value "draftCustomer" opened)

[<Fact>]
let ``a new invoice link can name its customer`` () =
    let model = books ()
    let opened = run [ go "#/invoices/new?customer=CUST-0002" ] model |> fst
    Assert.Equal("CUST-0002", value "draftCustomer" opened)
    Assert.Equal("New invoice", value "draftLabel" opened)

[<Fact>]
let ``every link on the page is a relative hash link`` () =
    let model = books ()

    Assert.Equal<string list>(
        [ "#/"; "#/work"; "#/invoices"; "#/proposals"; "#/customers"; "#/engagements"; "#/payments"; "#/credit-memos"; "#/receivables"; "#/ledger"; "#/reports"; "#/periods"; "#/settings" ],
        items "navigation" model |> List.map (field "href")
    )

    let invoices = run [ go "#/invoices" ] model |> fst
    Assert.Equal<string list>([ "#/invoices/INV-0002"; "#/invoices/INV-0001" ], items "invoices" invoices |> List.map (field "href"))
    Assert.Equal<string list>([ "#/drafts/D-0003" ], items "drafts" (run [ go "#/" ] model |> fst) |> List.map (field "href"))

// ---- Filters, sort and search (SUM-LINK-002, 003, 006) -------------------------------------------

[<Fact>]
let ``list filters refine the address in place and filter the list`` () =
    let model = run [ go "#/invoices" ] (books ()) |> fst
    let searched, effects = run [ InvoiceSearchChanged "beta" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices?q=beta") ], effects)
    Assert.Equal<string list>([ "INV-0002" ], items "invoices" searched |> List.map (field "id"))

    let filtered, more = run [ InvoiceSearchChanged ""; InvoiceStatusToggled "unpaid"; InvoiceSortChosen "number" ] model
    Assert.Equal(Navigate(NavigationEffect.Replace "/invoices?status=unpaid&sort=number"), List.last more)
    Assert.Equal<string list>([ "INV-0001" ], items "invoices" filtered |> List.map (field "id"))
    Assert.Equal("1 of 2 invoices", value "invoiceCountText" filtered)

    let cleared, last = run [ InvoiceFiltersCleared ] filtered
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices") ], last)
    Assert.Equal(2, (items "invoices" cleared).Length)

[<Fact>]
let ``a status no invoice can have is ignored, not put in the address`` () =
    let model = run [ go "#/invoices" ] (books ()) |> fst
    Assert.Equal<AppEffect list>([], run [ InvoiceStatusToggled "open" ] model |> snd)

[<Fact>]
let ``customer search and sort are in the address`` () =
    let model = run [ go "#/customers" ] (books ()) |> fst
    let sorted, effects = run [ CustomerSortChosen "balance" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/customers?sort=balance") ], effects)
    Assert.Equal<string list>([ "Acme"; "Beta" ], items "customers" sorted |> List.map (field "name"))
    Assert.Equal<string list>([ "Beta" ], items "customers" (run [ CustomerSearchChanged "bet" ] model |> fst) |> List.map (field "name"))

[<Fact>]
let ``receivables can be aged as of an earlier date`` () =
    let model = books ()
    let now = run [ go "#/receivables" ] model |> fst
    Assert.Equal<string list>([ "CUST-0001" ], items "aging" now |> List.map (field "key"))
    let before = run [ go "#/receivables?asOf=2026-10-06" ] model |> fst
    Assert.Empty(items "aging" before)
    Assert.Equal("2026-10-06", value "receivablesAsOf" before)
    let one, effects = run [ ReceivablesCustomerChosen "CUST-0002" ] now
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/receivables?customer=CUST-0002") ], effects)
    Assert.Empty(items "aging" one)

[<Fact>]
let ``choosing an invoice tab replaces the address`` () =
    let model = run [ go "#/invoices/INV-0001" ] (books ()) |> fst
    let history, effects = run [ InvoiceTabChosen "history" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices/INV-0001?tab=history") ], effects)
    Assert.Equal("True", value "onHistoryTab" history)
    Assert.Equal("False", value "onDocumentTab" history)

// ---- History (SUM-LINK-006) -----------------------------------------------------------------------

[<Fact>]
let ``issuing goes to the new invoice with a push, and Back finds the issued draft`` () =
    let model = books ()
    let issued, effects = run [ go "#/drafts/D-0003"; DraftIssued ] model
    Assert.Contains(Navigate(NavigationEffect.Push "/invoices/INV-0003"), effects)
    let back, none = run [ go "#/drafts/D-0003" ] issued
    Assert.Empty none
    Assert.Equal("True", value "isIssuedDraft" back)
    Assert.Equal("#/invoices/INV-0003", value "issuedDraftHref" back)

[<Fact>]
let ``adopting the address the browser reports never pushes, and corrects a non-canonical one`` () =
    let model = books ()
    let _, canonical = run [ go "#/invoices/INV-0001" ] model
    Assert.Empty canonical
    let _, corrected = run [ go "#/invoices/INV-0001?tab=document&utm_source=mail" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices/INV-0001") ], corrected)

[<Fact>]
let ``the skip link's anchor keeps the place and puts the address back`` () =
    let model = run [ go "#/invoices?q=acme" ] (books ()) |> fst
    let anchored, effects = run [ go "#main" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices?q=acme") ], effects)
    Assert.Equal(model.Place, anchored.Place)

// ---- Bad links (SUM-LINK-007) ---------------------------------------------------------------------

[<Theory>]
[<InlineData("#/no-such-place")>]
[<InlineData("#/invoices/INV-9999")>]
[<InlineData("#/drafts/D-9999")>]
[<InlineData("#/not-found")>]
let ``a link to nothing shows the not-found page`` (hash: string) =
    let model = run [ go hash ] (books ()) |> fst
    Assert.Equal("True", value "isNotFound" model)
    Assert.Equal("False", value "onInvoice" model)
    Assert.Equal("False", value "onEditor" model)
    Assert.True(items "navigation" model |> List.forall (fun row -> field "current" row = "false"))

[<Fact>]
let ``a link with a value Summa does not understand names it`` () =
    let model = run [ go "#/periods/2026-13" ] (books ()) |> fst
    Assert.Equal("True", value "isInvalidLink" model)
    Assert.Equal("The link's 'period' is '2026-13', but Summa expects month.", value "invalidLink" model)
    Assert.Equal("True", value "isInvalidLink" (run [ go "#/invoices/%E0%A4" ] model |> fst))

// ---- Sign-in (SUM-LINK-008) ------------------------------------------------------------------------

[<Fact>]
let ``local books need no sign-in, so a sign-in link goes straight to its target`` () =
    let model = books ()
    let resumed, effects = run [ go "#/sign-in?returnTo=%2Finvoices%2FINV-0001%3Ftab%3Dhistory" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/invoices/INV-0001?tab=history") ], effects)
    Assert.Equal(Routes.Invoice("INV-0001", Routes.History), resumed.Place)
    let home, foreign = run [ go "#/sign-in?returnTo=%2F%2Fevil.example" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/") ], foreign)
    Assert.Equal(Routes.Home, home.Place)

// ---- Copy link (SUM-LINK-009) ----------------------------------------------------------------------

[<Fact>]
let ``copy link copies the absolute address of the canonical place`` () =
    let model = run [ go "#/invoices?sort=newest&q=acme" ] (books ()) |> fst
    let _, effects = run [ LinkCopyRequested ] model
    Assert.Equal<AppEffect list>([ CopyText "https://summa.example/app/#/invoices?q=acme" ], effects)
    let copied = run [ LinkCopied true ] model |> fst
    Assert.Equal("Link copied.", value "notice" copied)
    let refused = run [ LinkCopied false ] model |> fst
    Assert.Equal("True", value "hasCopyFallback" refused)
    Assert.Equal("https://summa.example/app/#/invoices?q=acme", value "copyFallback" refused)

// ---- The wire (SUM-LINK-012) -----------------------------------------------------------------------

let private aegis = Summa.Web.Application.Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

let private send (session: Wire.Session) (message: string) =
    let next, reply = Wire.handle aegis (Summa.Web.Tests.Support.wireEnv ctx.Now) session message
    next, JsonNode.Parse(reply) |> Option.ofObj |> Option.get

[<Fact>]
let ``the wire reads the address, asks the kernel to navigate and to copy, and hears the copy's outcome`` () =
    let initialize =
        """{"kind":"Initialize","location":{"origin":"https://summa.example","path":"/app/","query":"","hash":"#/invoices?sort=newest"},"handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":[]}}"""

    let session, reply = send Wire.initial initialize
    let effects = reply["effects"].AsArray() |> Seq.map (fun e -> e.ToJsonString()) |> List.ofSeq
    Assert.Contains(effects, fun e -> e.Contains "\"kind\":\"Navigation\"" && e.Contains "\"operation\":\"replace\"" && e.Contains "\"url\":\"#/invoices\"")
    Assert.Equal(Routes.Invoices Routes.allInvoices, session.Model.Place)

    let moved, _ = send session """{"kind":"LocationChanged","location":{"origin":"https://summa.example","path":"/app/","query":"","hash":"#/settings"}}"""
    Assert.Equal(Routes.Settings, moved.Model.Place)

    let copying, reply = send moved """{"kind":"Event","event":{"name":"linkCopyRequested"}}"""
    let copy = reply["effects"].AsArray()[0]
    let text (name: string) = copy[name].GetValue<string>()
    Assert.Equal("Clipboard", text "kind")
    Assert.Equal("https://summa.example/app/#/settings", text "text")
    let correlation = text "correlationId"

    let _, copied =
        send copying ("{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"ClipboardResult\",\"correlationId\":\"" + correlation + "\",\"outcome\":{\"kind\":\"Success\"}}}")

    let notice = copied["view"]["notice"]
    Assert.Equal("Link copied.", notice.GetValue<string>())
