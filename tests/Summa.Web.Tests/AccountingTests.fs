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

let private configured () = run [ Started; ConfigurationRead(Ok localConfig) ] initial |> fst

let private started () = run [ Started; ConfigurationRead(Ok localConfig); Loaded None ] initial |> fst

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
        [ Navigate "customers"
          CustomerNameChanged "ABC Corp"
          CustomerAddressChanged "1 Main Street"
          CustomerEmailChanged "ap@abc.example"
          CustomerTermsChanged "30"
          CustomerAdded ]
        model
    |> fst

let private drafted (model: Model) =
    let withDraft = run [ NewInvoice; DraftCustomerChanged "CUST-0001" ] model |> fst
    let key = withDraft.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "34.5"); LineRateChanged(key, "175.00") ] withDraft |> fst

[<Fact>]
let ``starting reads the deployment, then asks the browser for its books; an empty browser starts new ones`` () =
    let configuring, first = update ctx Started initial
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
    Assert.Equal<AppEffect list>([ SaveBooks(match effects with [ SaveBooks s ] -> s | _ -> "") ], effects)
    Assert.Equal("False", value "cannotIssue" reviewed)
    let issued = run [ DraftIssued ] reviewed |> fst
    Assert.Equal("invoice", routeName issued.Route)
    Assert.Equal("6,037.50 USD", value "docTotal" issued)
    Assert.Equal("Unpaid", value "detailStatus" issued)
    let paid = run [ PaymentAmountChanged "6037.50"; PaymentRecorded ] issued |> fst
    Assert.Equal("Paid", value "detailStatus" paid)
    Assert.Equal("False", value "canRecordPayment" paid)
    Assert.Equal("0.00 USD", value "totalOutstanding" paid)

[<Fact>]
let ``a draft that is not ready lists its blockers and is kept as a draft`` () =
    let model = started () |> customer
    let noAddress = run [ Navigate "settings"; CompanyAddressChanged ""; CompanySaved ] model |> fst
    let blocked = run [ DraftSubmitted ] (drafted noAddress) |> fst
    Assert.Equal("True", value "hasBlockers" blocked)
    Assert.Equal("1", value "draftCount" blocked)
    Assert.Equal("True", value "cannotIssue" blocked)
    Assert.Single(items "blockers" blocked) |> ignore

[<Fact>]
let ``bad input is refused with a message, never a guess`` () =
    let model = started () |> customer
    let noRate = run [ NewInvoice; DraftCustomerChanged "CUST-0001" ] model |> fst
    let key = noRate.Draft.Lines.Head.Key
    let refused = run [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "abc"); DraftSubmitted ] noRate |> fst
    Assert.Equal("'Work' needs a description, a quantity and a rate such as 150.00", value "error" refused)
    let noName = run [ Navigate "customers"; CustomerAdded ] model |> fst
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

// ---- The wire ---------------------------------------------------------------------------

let private aegis = Summa.Web.Application.Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

let private handshake (withPrint: bool) =
    let print = if withPrint then """{"id":"summa.print","version":1,"fingerprint":"summa.print/1: print"}""" else ""

    """{"kind":"Initialize","handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":["""
    + print
    + "]}}"

let private send (session: Wire.Session) (message: string) =
    let next, reply = Wire.handle aegis (fun () -> ctx.Now) "local-person" session message
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
    let configuring = update ctx Started initial |> fst
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
