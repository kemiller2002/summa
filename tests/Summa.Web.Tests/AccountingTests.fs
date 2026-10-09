/// The accounting application's engine and its Limen wiring (WI-0030):
/// starting and restoring the books, the invoicing flow through domain
/// commands, refusing untrustworthy books, and the protocol mechanics.
module Summa.Web.Tests.AccountingTests

open System
open System.Text.Json.Nodes
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
module Wire = Summa.Web.Application.AccountingWire

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private localConfig = """{"environment":"local","environmentName":"local development"}"""

/// The page's address with a fragment, as the kernel reports it.
let address (hash: string) : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = ""
      Hash = hash }

let private home = address ""

let private configured () = run [ Started home; ConfigurationRead(Ok localConfig) ] initial |> fst

let private started () = run [ Started home; ConfigurationRead(Ok localConfig); Loaded None ] initial |> fst

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

let private customer (model: Model) =
    run
        [ LocationChanged(address "#/customers")
          CustomerNameChanged "ABC Corp"
          CustomerAddressChanged "1 Main Street"
          CustomerEmailChanged "ap@abc.example"
          CustomerTermsChanged "30"
          CustomerAdded ]
        model
    |> fst

let private drafted (model: Model) =
    let withDraft = run [ LocationChanged(address "#/invoices/new"); DraftCustomerChanged "CUST-0001" ] model |> fst
    let key = withDraft.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "34.5"); LineRateChanged(key, "175.00") ] withDraft |> fst

[<Fact>]
let ``starting reads the deployment, then asks the browser for its books; an empty browser starts new ones`` () =
    let configuring, first = update ctx (Started home) initial
    Assert.Equal<AppEffect list>([ LoadConfiguration ], first)
    Assert.Equal("True", value "isConfiguring" configuring)
    let loading, effects = update ctx (ConfigurationRead(Ok localConfig)) configuring
    Assert.Equal<AppEffect list>([ LoadBooks ], effects)
    Assert.Equal("True", value "isLoading" loading)
    Assert.Equal("SUMMA · LOCAL · local development · books are kept only in this browser", value "environmentBanner" loading)
    let fresh, saved = update ctx (Loaded None) loading
    Assert.Equal("True", value "isReady" fresh)

    match saved with
    | [ SaveBooks snapshot ] ->
        // What was saved restores to the same books.
        let restored, _ = update ctx (Loaded(Some snapshot)) loading
        Assert.Equal("True", value "isReady" restored)
        Assert.Equal(fresh.Books.Value.Books.Ledger.Accounts.Count, restored.Books.Value.Books.Ledger.Accounts.Count)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the invoicing flow runs through Summa's domain commands`` () =
    let model = started () |> customer |> drafted
    Assert.Equal("6,037.50 USD", value "draftTotal" model)
    Assert.Equal("True", value "cannotIssue" model)
    let reviewed, effects = run [ DraftSubmitted ] model
    // Saved, the new invoice is its draft: the address is replaced, not pushed.
    Assert.Equal<AppEffect list>(
        [ SaveBooks(match effects with SaveBooks s :: _ -> s | _ -> "")
          Navigate(Limen.Routing.NavigationEffect.Replace "/drafts/D-0001") ],
        effects
    )
    Assert.Equal("False", value "cannotIssue" reviewed)
    let issued, issuing = run [ DraftIssued ] reviewed
    Assert.Equal(Summa.Web.Engine.Routes.Invoice("INV-0001", Summa.Web.Engine.Routes.Document), issued.Place)
    Assert.Contains(Navigate(Limen.Routing.NavigationEffect.Push "/invoices/INV-0001"), issuing)
    Assert.Equal("6,037.50 USD", value "docTotal" issued)
    Assert.Equal("Unpaid", value "detailStatus" issued)
    let paid = run [ InvoiceTabChosen "payments"; PaymentAmountChanged "6037.50"; PaymentRecorded ] issued |> fst
    Assert.Equal("Paid", value "detailStatus" paid)
    Assert.Equal("False", value "canRecordPayment" paid)
    Assert.Equal("0.00 USD", value "totalOutstanding" paid)

[<Fact>]
let ``a draft that is not ready lists its blockers and is kept as a draft`` () =
    let model = started () |> customer
    let noAddress = run [ LocationChanged(address "#/settings"); CompanyAddressChanged ""; CompanySaved ] model |> fst
    let blocked = run [ DraftSubmitted ] (drafted noAddress) |> fst
    Assert.Equal("True", value "hasBlockers" blocked)
    Assert.Equal("1", value "draftCount" blocked)
    Assert.Equal("True", value "cannotIssue" blocked)
    Assert.Single(items "blockers" blocked) |> ignore

[<Fact>]
let ``bad input is refused with a message, never a guess`` () =
    let model = started () |> customer
    let noRate = run [ LocationChanged(address "#/invoices/new"); DraftCustomerChanged "CUST-0001" ] model |> fst
    let key = noRate.Draft.Lines.Head.Key
    let refused = run [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "abc"); DraftSubmitted ] noRate |> fst
    Assert.Equal("'Work' needs a description, a quantity and a rate such as 150.00", value "error" refused)
    let noName = run [ LocationChanged(address "#/customers"); CustomerAdded ] model |> fst
    Assert.Equal("A customer needs a name.", value "error" noName)

[<Fact>]
let ``books that fail their checks are refused, and only then can they be replaced`` () =
    let model = started () |> customer |> drafted |> run [ DraftSubmitted; DraftIssued ] |> fst

    let snapshot =
        match Summa.Storage.LocalSnapshot.encode model.Manifest.Value model.Books.Value with
        | Ok text -> text
        | Error e -> failwith $"%A{e}"

    let tampered = snapshot.Replace("\\u0022minor\\u0022:603750", "\\u0022minor\\u0022:603751")
    Assert.NotEqual<string>(snapshot, tampered)
    let refused = update ctx (Loaded(Some tampered)) (configured ()) |> fst
    Assert.Equal("True", value "isUntrustworthy" refused)
    Assert.NotEmpty(items "storageProblems" refused)
    Assert.Equal("False", value "isReady" refused)
    // Resetting is offered only for untrustworthy books.
    Assert.Equal("True", value "isReady" (update ctx ResetConfirmed refused |> fst))
    Assert.Equal(model.Books.Value.Books.Invoices.Count, (update ctx ResetConfirmed model |> fst).Books.Value.Books.Invoices.Count)
    Assert.Equal("True", value "isUntrustworthy" (update ctx (Loaded(Some "not json")) (configured ()) |> fst))

[<Fact>]
let ``books saved before the manifest named its credit, deposit and bad-debt accounts are migrated and saved once`` () =
    let model = started () |> customer

    let snapshot =
        match Summa.Storage.LocalSnapshot.encode model.Manifest.Value model.Books.Value with
        | Ok text -> text
        | Error e -> failwith $"%A{e}"

    // The manifest as schema 1 wrote it.
    let v1 =
        let added = set [ "customerCreditsAccount"; "customerDepositsAccount"; "badDebtAccount" ]

        let body =
            match Summa.Storage.Organization.body model.Manifest.Value with
            | Arca.Json.Object members ->
                Arca.Json.Object(
                    members
                    |> List.map (function
                        | "accounting", Arca.Json.Object a -> "accounting", Arca.Json.Object(a |> List.filter (fst >> added.Contains >> not))
                        | other -> other)
                )
            | other -> other

        match Summa.Storage.Organization.toRecord model.Manifest.Value with
        | Ok record ->
            match Arca.Record.encode Arca.Record.DefaultMaxBytes { record with SchemaVersion = 1; Body = body } with
            | Ok text -> text
            | Error e -> failwith $"%A{e}"
        | Error e -> failwith $"%A{e}"

    let older =
        match JsonNode.Parse snapshot with
        | null -> failwith "not JSON"
        | node ->
            let o = node.AsObject()
            o["manifest"] <- JsonValue.Create v1
            o.ToJsonString()

    let migrated, effects = update ctx (Loaded(Some older)) (configured ())
    Assert.Equal("True", value "isReady" migrated)
    Assert.Equal("These books were updated to name their credit, deposit and bad-debt accounts.", value "notice" migrated)
    Assert.Equal("6500", migrated.Manifest.Value.Accounting.BadDebtAccount)

    match effects with
    | [ SaveBooks saved ] ->
        let reopened, again = update ctx (Loaded(Some saved)) (configured ())
        Assert.Equal("True", value "isReady" reopened)
        Assert.Empty again
    | other -> failwith $"%A{other}"

// ---- The wire ---------------------------------------------------------------------------

let private aegis = Summa.Web.Application.Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

let private handshake (withPrint: bool) =
    let print = if withPrint then """{"id":"summa.print","version":1,"fingerprint":"summa.print/1: print"}""" else ""

    """{"kind":"Initialize","location":{"origin":"https://summa.example","path":"/app/","query":"","hash":""},"handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":["""
    + print
    + "]}}"

let private send (session: Wire.Session) (message: string) =
    let next, reply = Wire.handle aegis (Summa.Web.Tests.Support.wireEnv ctx.Now) session message
    next, JsonNode.Parse(reply) |> Option.ofObj |> Option.get

/// The node at a path of object keys and array indices.
let private at (path: obj list) (node: JsonNode) =
    path
    |> List.fold
        (fun (n: JsonNode) step ->
            let next =
                match step with
                | :? int as i -> n.AsArray()[i]
                | :? string as k -> n.AsObject()[k]
                | _ -> null

            next |> Option.ofObj |> Option.defaultWith (fun () -> failwith $"nothing at {path}"))
        node

let private str path node = (at path node).GetValue<string>()
let private bool' path node = (at path node).GetValue<bool>()

[<Fact>]
let ``the wire accepts Limen Core, selects the print pack when offered, and asks Storage for the books`` () =
    let session, reply = send Wire.initial (handshake true)
    Assert.Equal("Accepted", str [ "handshake"; "kind" ] reply)
    Assert.Equal("summa.print", str [ "handshake"; "capabilities"; 0; "id" ] reply)
    Assert.Equal("Http", str [ "effects"; 0; "kind" ] reply)
    Assert.Equal(ConfigurationUrl, str [ "effects"; 0; "url" ] reply)
    let configured =
        """{"kind":"EffectResult","result":{"kind":"HttpResult","correlationId":"""
        + "\"" + str [ "effects"; 0; "correlationId" ] reply + "\""
        + ""","outcome":{"kind":"Success","status":200,"body":{"environment":"local","environmentName":"test"}}}}"""

    let session, reply = send session configured
    Assert.Equal("Storage", str [ "effects"; 0; "kind" ] reply)
    Assert.Equal("get", str [ "effects"; 0; "operation" ] reply)
    Assert.Equal(StorageKey, str [ "effects"; 0; "key" ] reply)
    let correlation = str [ "effects"; 0; "correlationId" ] reply
    // Nothing stored yet: new books, saved with a Storage set.
    let result =
        """{"kind":"EffectResult","result":{"kind":"StorageResult","correlationId":"""
        + "\"" + correlation + "\""
        + ""","outcome":{"kind":"Success","value":null}}}"""

    let _, started = send session result
    Assert.True(bool' [ "view"; "isReady" ] started)
    Assert.True(bool' [ "view"; "canPrint" ] started)
    Assert.Equal("set", str [ "effects"; 0; "operation" ] started)
    let _, noPrint = send Wire.initial (handshake false)
    Assert.Equal(0, (at [ "handshake"; "capabilities" ] noPrint).AsArray().Count)

[<Fact>]
let ``an event the page and engine disagree on is a defect, not an operational fault`` () =
    let session, _ = send Wire.initial (handshake false)
    Assert.Throws<InvalidOperationException>(fun () -> send session """{"kind":"Event","event":{"name":"noSuchEvent"}}""" |> ignore) |> ignore

[<Fact>]
let ``a malformed kernel message is an operational fault, and the model is kept`` () =
    let session, _ = send Wire.initial (handshake false)
    let faulted, reply = send session """{"kind":"Mystery"}"""
    Assert.True(bool' [ "view"; "hasOperationalFault" ] reply)
    Assert.Equal(session.Model.Storage, faulted.Model.Storage)

[<Fact>]
let ``a configuration that cannot be used stops the page with the reason`` () =
    let configuring = update ctx (Started home) initial |> fst
    let unreadable = update ctx (ConfigurationRead(Error "HTTP 404")) configuring |> fst
    Assert.Equal("True", value "isMisconfigured" unreadable)
    Assert.Equal("False", value "isReady" unreadable)
    Assert.Equal("The deployment's configuration could not be read (HTTP 404).", value "misconfiguration" unreadable)
    let invalid, effects = update ctx (ConfigurationRead(Ok """{"environment":"moon","environmentName":"x"}""")) configuring
    Assert.Empty(effects)
    Assert.Equal("True", value "isMisconfigured" invalid)
    let production = update ctx (ConfigurationRead(Ok """{"environment":"production","environmentName":"production"}""")) configuring |> fst
    // Production shows no banner.
    Assert.Equal("False", value "hasBanner" production)

[<Fact>]
let ``an invoice's history says when an agent acted, with its identity and run, apart from people`` () =
    let model = started () |> customer |> drafted |> run [ DraftSubmitted; DraftIssued ] |> fst
    // The application's own changes are a person's.
    let own = model.Books.Value.Books.Ledger.Audit |> List.last
    Assert.Equal(Some "human", own.Provenance |> Option.map _.ActorKind)

    let byAgent: Summa.Ledger.Ledger.AuditRecord =
        { own with
            Who = "summa-agent"
            What = "invoice-sent"
            Provenance =
                Some
                    { ActorKind = "agent"
                      Agent = Some { Provider = "anthropic"; Model = "claude"; Runtime = "claude-code" }
                      ExecutionId = Some "EXE-summa.7"
                      SourceSystem = None
                      SourceId = None
                      Reason = Some "customer asked for a copy" } }

    let books = model.Books.Value
    let withAgent = { model with Books = Some { books with Books = { books.Books with Ledger = { books.Books.Ledger with Audit = books.Books.Ledger.Audit @ [ byAgent ] } } } }
    let history = run [ LocationChanged(address "#/invoices/INV-0001?tab=history") ] withAgent |> fst |> items "history"
    let last = history |> List.last |> Map.ofList
    Assert.Equal(Text "Agent summa-agent (anthropic claude), run EXE-summa.7", last["who"])
    Assert.Equal(Text "invoice sent: customer asked for a copy", last["what"])
