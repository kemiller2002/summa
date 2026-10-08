/// index.html and the engine are not type-checked against each other: the
/// page names events and view keys as strings. These tests hold each page to
/// its engine in both directions, so a renamed key fails here instead of
/// silently rendering nothing (Limen unmounts a data-if whose key is missing).
module Summa.Web.Tests.BindingAgreementTests

open System.Text.RegularExpressions
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Tests.Support

let private attributeValues (attribute: string) (html: string) =
    Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

/// Every key the page binds: data-text, data-if, data-each, data-key and every data-bind-*.
let private boundKeys (html: string) =
    let bindings =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    [ "data-text"; "data-if"; "data-each"; "data-key" ]
    |> List.map (fun attribute -> attributeValues attribute html)
    |> Set.unionMany
    |> Set.union bindings

/// The names a view offers: its own keys and the fields of its list items.
let private viewNames (views: View list) =
    views
    |> List.collect (fun view ->
        view
        |> List.collect (fun (name, value) ->
            match value with
            | Value _ -> [ name ]
            | Items items -> name :: (items |> List.collect (List.map fst))))
    |> Set.ofList

let private faultNames = Summa.Web.Application.Boundary.faultView None |> List.map fst |> Set.ofList

module private Backlog =
    open Summa.Web.Engine.BacklogPage

    /// A page with every list populated, so every item field is projected.
    let views =
        let liveRow =
            { row "WI-1" with
                Attachments =
                    [ { Id = "a"
                        Name = "a.txt"
                        Size = 1L
                        ContentType = None
                        UploadedAt = "" } ] }

        let page =
            [ Started
              Replied("c1", Answered(Rows [ liveRow ]))
              RowAction(0, "WI-1")
              RowAction(2, "WI-1") ]
            |> List.fold (fun page msg -> update msg page |> fst) initial

        [ view page; view { page with Evidence = [ { Key = "e"; Type = ""; Path = "" } ] } ]

module private Hub =
    open Summa.Web.Engine.Hub
    open Summa.Web.Engine.HubPage

    let views =
        let repo =
            { Id = "r"
              Name = "R"
              Path = "/r"
              RegisteredAt = "" }

        let row =
            { RepoId = "r"
              RepoName = "R"
              Id = Some "WI-1"
              Title = None
              Status = None
              Tags = []
              Priority = None
              Error = None }

        let page =
            [ Started; Replied("c1", Answered(Repos [ repo ])); Replied("c2", Answered(Work [ row ])) ]
            |> List.fold (fun page msg -> update msg page |> fst) initial

        [ view page ]

module private Accounting =
    open Summa.Web.Engine.Accounting

    let private at hash : Limen.Routing.PageLocation =
        { Origin = "https://summa.example"
          Path = "/app/"
          Query = ""
          Hash = hash }

    /// The application on every screen, with every list populated.
    let views =
        let ctx = { Now = System.DateTimeOffset(2026, 10, 7, 9, 0, 0, System.TimeSpan.Zero); Actor = "local-person" }
        let step model msg = update ctx msg model |> fst
        let go hash = LocationChanged(at hash)
        let configured = [ Started(at ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}""") ] |> List.fold step initial
        let started = step configured (Loaded None)

        let withCustomer =
            [ go "#/customers"; CustomerNameChanged "ABC"; CustomerAddressChanged "1 Main"; CustomerTermsChanged "0"; CustomerAdded ]
            |> List.fold step started

        let opened = [ go "#/invoices/new"; DraftCustomerChanged "CUST-0001" ] |> List.fold step withCustomer
        let key = opened.Draft.Lines.Head.Key
        let drafted = [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "100") ] |> List.fold step opened

        let blocked = [ go "#/settings"; CompanyAddressChanged ""; CompanySaved; go "#/invoices/new"; DraftCustomerChanged "CUST-0001" ] |> List.fold step drafted
        let blocked = [ LineDescriptionChanged(blocked.Draft.Lines.Head.Key, "Work"); LineRateChanged(blocked.Draft.Lines.Head.Key, "100"); DraftSubmitted ] |> List.fold step blocked
        let fixedUp = [ go "#/settings"; CompanyAddressChanged "1 Way"; CompanySaved; go "#/drafts/D-0001"; DraftSubmitted; DraftIssued ] |> List.fold step blocked
        let paid = [ InvoiceTabChosen "payments"; PaymentAmountChanged "50"; PaymentRecorded ] |> List.fold step fixedUp
        let late = { paid with Today = System.DateOnly(2027, 6, 1) }
        let untrustworthy = step configured (Loaded(Some "{}"))
        let misconfigured = [ Started(at ""); ConfigurationRead(Error "HTTP 404") ] |> List.fold step initial
        let extra = [ "canPrint", Value(Flag true) ]

        // An engagement and a credit memo, so every list on every screen has rows.
        let full =
            [ go "#/engagements"
              EngagementCustomerChanged "CUST-0001"
              EngagementNameChanged "Retainer"
              EngagementFeeChanged "1000"
              EngagementAdded
              go "#/invoices/INV-0001?tab=payments"
              CreditAmountChanged "10"
              CreditReasonChanged "Goodwill"
              CreditMemoIssued ]
            |> List.fold step paid

        let visit hash = step full (go hash)

        [ view started @ extra
          view blocked
          view paid
          view (visit "#/invoices/INV-0001")
          view (visit "#/invoices/INV-0001?tab=history")
          view late
          view (visit "#/invoices?status=paid")
          view (visit "#/invoices?q=nothing-matches")
          view (visit "#/customers")
          view (visit "#/customers?q=nothing-matches")
          view { (visit "#/receivables") with Today = System.DateOnly(2027, 6, 1) }
          view (visit "#/drafts/D-0001")
          view (visit "#/no-such-place")
          view (visit "#/periods/2026-13")
          view (visit "#/reports")
          view (visit "#/invoices/INV-0001?tab=payments")
          view (visit "#/payments")
          view (visit "#/payments?q=nothing-matches")
          view (visit "#/payments/PAY-0001")
          view (visit "#/customers/CUST-0001")
          view (visit "#/customers/CUST-0001?tab=payments")
          view (visit "#/customers/CUST-0001?tab=credits")
          view (visit "#/credit-memos")
          view (visit "#/credit-memos/CM-0001")
          view (visit "#/engagements")
          view (visit "#/engagements/ENG-0001")
          view (step paid (LinkCopied false))
          view untrustworthy
          view misconfigured
          view (step initial (Started(at ""))) ]

let private agree (page: string) (views: View list) (events: Set<string>) =
    let html = readRepoFile $"{page}/index.html"
    let offered = Set.union (viewNames views) faultNames
    let bound = boundKeys html
    Assert.Empty(Set.difference bound offered)
    let named = attributeValues "data-event" html
    // Every event the page can send is one the engine handles, and every
    // event the engine handles is one the page can send.
    Assert.Equal<Set<string>>(events, named)

[<Fact>]
let ``the backlog page binds only what its engine projects and sends only what it handles`` () =
    agree "web" Backlog.views (Summa.Web.Application.BacklogWire.events |> Map.keys |> Set.ofSeq)

[<Fact>]
let ``the hub page binds only what its engine projects and sends only what it handles`` () =
    agree "web-hub" Hub.views (Summa.Web.Application.HubWire.events |> Map.keys |> Set.ofSeq)

[<Fact>]
let ``the files-pack inputs on each page are the ones its engine listens for`` () =
    Assert.Equal<Set<string>>(
        set [ Summa.Web.Engine.BacklogPage.AddFileInput; Summa.Web.Engine.BacklogPage.AttachFileInput ],
        attributeValues "data-files-input" (readRepoFile "web/index.html")
    )

    Assert.Equal<Set<string>>(
        set [ Summa.Web.Engine.HubPage.CreateFileInput ],
        attributeValues "data-files-input" (readRepoFile "web-hub/index.html")
    )

[<Fact>]
let ``every dialog a row action opens exists on the page`` () =
    let html = readRepoFile "web/index.html"
    let dialogs = Regex.Matches(html, "<dialog[^>]*\\sid=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    let targets =
        Backlog.views
        |> List.collect (List.collect (fun (_, value) ->
            match value with
            | Items items -> items |> List.collect id |> List.choose (fun (name, scalar) ->
                match scalar with
                | Text dialog when name.EndsWith "Dialog" && dialog <> "" -> Some dialog
                | _ -> None)
            | _ -> []))
        |> Set.ofList

    Assert.NotEmpty targets
    Assert.Empty(Set.difference targets dialogs)

[<Fact>]
let ``the accounting page binds only what its engine projects and sends only what it handles`` () =
    agree "app" Accounting.views (Summa.Web.Application.AccountingWire.events |> Map.keys |> Set.ofSeq)

/// Limen renders a data-if or data-each template's first element only, so a
/// template with two elements would silently drop the second. Each one on
/// every page has exactly one element at its root.
[<Fact>]
let ``every bound template has exactly one root element`` () =
    let voids = set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "source"; "track"; "wbr" ]

    for page in [ "web"; "web-hub"; "app" ] do
        let html = Regex.Replace(readRepoFile $"{page}/index.html", "<!--.*?-->", "", RegexOptions.Singleline)

        // Walk the tags, counting each open template's direct child elements.
        let _, counts =
            Regex.Matches(html, @"<(/?)([a-zA-Z][\w-]*)([^>]*)>")
            |> Seq.fold
                (fun (stack: (string * int * string) list, counts: (string * int) list) m ->
                    let closing = m.Groups[1].Value = "/"
                    let name = m.Groups[2].Value.ToLowerInvariant()
                    let selfClosing = voids.Contains name || m.Groups[3].Value.EndsWith "/"

                    let counted =
                        match stack with
                        | ("template", n, label) :: rest when not closing -> ("template", n + 1, label) :: rest
                        | other -> other

                    match closing, counted with
                    | true, ("template", n, label) :: rest when name = "template" -> rest, (label, n) :: counts
                    | true, _ :: rest -> rest, counts
                    | true, [] -> [], counts
                    | false, _ when selfClosing -> counted, counts
                    | false, _ -> (name, 0, $"{page}: <{name}{m.Groups[3].Value}>") :: counted, counts)
                ([], [])

        for label, children in counts do
            Assert.True((children = 1), $"{label} has {children} root elements")
