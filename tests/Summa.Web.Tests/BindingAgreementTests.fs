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

    /// The application on every screen, with every list populated.
    let views =
        let ctx = { Now = System.DateTimeOffset(2026, 10, 7, 9, 0, 0, System.TimeSpan.Zero); Actor = "local-person" }
        let step model msg = update ctx msg model |> fst
        let configured = [ Started; ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}""") ] |> List.fold step initial
        let started = step configured (Loaded None)

        let withCustomer =
            [ Navigate "customers"; CustomerNameChanged "ABC"; CustomerAddressChanged "1 Main"; CustomerTermsChanged "0"; CustomerAdded ]
            |> List.fold step started

        let opened = [ NewInvoice; DraftCustomerChanged "CUST-0001" ] |> List.fold step withCustomer
        let key = opened.Draft.Lines.Head.Key
        let drafted = [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "100") ] |> List.fold step opened

        let blocked = [ Navigate "settings"; CompanyAddressChanged ""; CompanySaved; DraftSubmitted ] |> List.fold step drafted
        let fixedUp = [ Navigate "settings"; CompanyAddressChanged "1 Way"; CompanySaved; DraftOpened "D-0001"; DraftSubmitted; DraftIssued ] |> List.fold step blocked
        let paid = [ PaymentAmountChanged "50"; PaymentRecorded ] |> List.fold step fixedUp
        let late = { paid with Today = System.DateOnly(2027, 6, 1) }
        let untrustworthy = step configured (Loaded(Some "{}"))
        let misconfigured = [ Started; ConfigurationRead(Error "HTTP 404") ] |> List.fold step initial
        let extra = [ "canPrint", Value(Flag true) ]

        [ view started @ extra
          view blocked
          view paid
          view late
          view { late with Route = Receivables }
          view untrustworthy
          view misconfigured
          view (step initial Started) ]

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
