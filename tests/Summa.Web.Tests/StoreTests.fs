/// Books on GitHub through the application (WI-0037): the engine, the wire,
/// the store port and Arca, with Arca's in-memory provider standing in for
/// GitHub and a signed-in person standing in for Fides. Setting the books
/// up, working in them, someone else's change in between, capabilities,
/// and the older manifest's migration.
module Summa.Web.Tests.StoreTests

open System
open System.Text.Json.Nodes
open Xunit
open Arca
open Summa.Web.Engine.Accounting
open Summa.Web.Application

module Wire = Summa.Web.Application.AccountingWire

let private configuration =
    """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"summa-data","branch":"main","basePath":"deployments/test"},"identity":{"exchange":"https://fides.test","application":"summa-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://summa.example/app/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","defaultCurrency":"USD","timeZone":"America/New_York","administrators":["583231"]}]}"""

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.Zero)
let private aegis = Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

let private snapshot: CapabilitySnapshot =
    { Identity =
        { Provider = "github"
          Subject = "583231"
          Login = Some "octocat"
          Kind = IdentityKind.User }
      RepositoryId = "R_1"
      Repository = RepositoryRef.create "acme" "summa-data" |> Result.defaultWith (fun e -> failwith $"%A{e}")
      Visibility = RepositoryVisibility.Private
      CanRead = true
      CanWrite = true
      Archived = false
      Branch = BranchAccess.Writable }

/// An identity port that has already signed `subject` in.
let private signedIn (bridge: Bridge.Bridge) (subject: string) (login: string) : Identity.IdentityPort =
    { Identity.none with
        Begin = fun _ _ -> bridge.Start(async { return [ IdentityChanged(IdentitySignedIn("github", subject, login)) ] }) }

let private keys = ref 0

/// The in-memory provider, its answers made synchronous. In the browser
/// everything runs on one thread; here Arca's in-memory commit may finish on
/// another, after the page has replied.
let private synchronous (provider: StorageProvider) : StorageProvider =
    let now (work: Async<'a>) = async.Return(Async.RunSynchronously work)

    { provider with
        Read = fun ns path -> now (provider.Read ns path)
        List = fun ns path -> now (provider.List ns path)
        Commit = fun operation -> now (provider.Commit operation)
        ChangeToken = fun ns -> now (provider.ChangeToken ns)
        NamespaceState = fun ns -> now (provider.NamespaceState ns)
        History = fun ns path -> now (provider.History ns path)
        Reconcile = fun ns pending -> now (provider.Reconcile ns pending) }

/// GitHub as the pages see it: the repository, and whether it can be reached.
type private Network(repository: InMemoryStore) =
    member val Online = true with get, set
    member this.Provider: StorageProvider =
        let provider = synchronous repository.Provider
        let offline () = StorageFailure.ProviderFailed("github", true, "offline")
        let guard (work: Async<Result<'a, StorageFailure>>) = if this.Online then work else async.Return(Error(offline ()))

        { provider with
            Read = fun ns path -> guard (provider.Read ns path)
            List = fun ns path -> guard (provider.List ns path)
            Commit = fun operation -> guard (provider.Commit operation)
            ChangeToken = fun ns -> guard (provider.ChangeToken ns)
            NamespaceState = fun ns -> guard (provider.NamespaceState ns) }

/// One browser: its localStorage, and which tab holds which Web Lock.
type private Browser() =
    member val Storage = Collections.Generic.Dictionary<string, string>()
    member val Locks = Collections.Generic.HashSet<string>()

let private networks = Collections.Generic.Dictionary<InMemoryStore, Network>(HashIdentity.Reference)

let private networkOf (repository: InMemoryStore) =
    match networks.TryGetValue repository with
    | true, found -> found
    | _ ->
        let created = Network repository
        networks[repository] <- created
        created

/// One page, signed in as `subject`, over the shared in-memory repository.
let private page (repository: InMemoryStore) (subject: string) (login: string) : Wire.Env =
    let bridge = Bridge.Bridge()

    let backend: Store.Backend =
        { Provider = fun _ -> (networkOf repository).Provider
          Resolve = fun _ -> async.Return(Ok snapshot) }

    { Now = fun () -> start
      LocalActor = "local-person"
      Bridge = bridge
      Identity = signedIn bridge subject login
      Store =
        Store.arca bridge backend (fun () -> start) (fun () ->
            keys.Value <- keys.Value + 1
            $"test-key-{keys.Value:D6}") }

let private text (node: JsonNode | null) =
    match node with
    | null -> failwith "missing"
    | n -> n.GetValue<string>()

/// The browser the pages of a test share, when they keep unsent changes.
let mutable private browser: Browser option = None

/// Sends a message and answers the configuration request (and, in a test
/// with a browser, its storage and locks), until nothing is left to answer.
/// The store and the identity answer through the bridge.
let private pump (env: Wire.Env) (session: Wire.Session) (message: string) =
    let rec loop (session: Wire.Session) (queue: string list) (last: string) =
        match queue with
        | [] -> session, last
        | message :: rest ->
            let session, reply = Wire.handle aegis env session message
            let parsed = JsonNode.Parse reply |> Option.ofObj |> Option.get

            let effects =
                match parsed["effects"] with
                | null -> []
                | effects -> effects.AsArray() |> Seq.choose Option.ofObj |> List.ofSeq

            let answers =
                effects
                |> List.choose (fun effect ->
                    match text effect["kind"] with
                    | "Http" when text effect["method"] = "GET" ->
                        let id = text effect["correlationId"]
                        let body = Text.Json.JsonSerializer.Serialize configuration
                        Some $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{id}","outcome":{{"kind":"Success","status":200,"body":{body}}}}}}}"""
                    | "Storage" when browser.IsSome ->
                        let id = text effect["correlationId"]
                        let key = text effect["key"]
                        let storage = browser.Value.Storage
                        let json (v: string) = Text.Json.JsonSerializer.Serialize v

                        let value =
                            match text effect["operation"] with
                            | "get" -> (match storage.TryGetValue key with | true, v -> $",\"value\":{json v}" | _ -> "")
                            | "set" ->
                                storage[key] <- text effect["value"]
                                ""
                            | _ ->
                                storage.Remove key |> ignore
                                ""

                        Some $"""{{"kind":"EffectResult","result":{{"kind":"StorageResult","correlationId":"{id}","outcome":{{"kind":"Success"{value}}}}}}}"""
                    | "Capability" when browser.IsSome && text effect["capability"] = "limen.coordination" ->
                        let id = text effect["correlationId"]
                        let name = match effect["request"] with null -> "" | r -> text r.["name"]
                        let kind = if browser.Value.Locks.Add name then "Acquired\",\"lock\":\"lock-1" else "Busy"
                        Some $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"limen.coordination","version":1,"outcome":{{"kind":"Completed","result":{{"kind":"{kind}"}}}}}}}}"""
                    | _ -> None)

            loop session (rest @ answers) reply

    loop session [ message ] ""

let private initialize (hash: string) =
    let packs =
        if browser.IsSome then
            $"""{{"id":"limen.coordination","version":1,"fingerprint":"{Wire.coordination.Fingerprint}"}}"""
        else
            ""

    $"""{{"kind":"Initialize","location":{{"origin":"https://summa.example","path":"/app/","query":"","hash":"{hash}"}},"handshake":{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{packs}]}}}}"""

let private event (name: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","value":{Text.Json.JsonSerializer.Serialize value}}}}}"""

let private flag (key: string) (reply: string) =
    match (JsonNode.Parse reply |> Option.ofObj |> Option.get)["view"] with
    | null -> failwith "no view"
    | view ->
        match view[key] with
        | null -> failwith key
        | v -> v.GetValue<bool>()

let private openAs (repository: InMemoryStore) (subject: string) (login: string) =
    let env = page repository subject login
    let session, reply = pump env Wire.initial (initialize "#/customers")
    env, session, reply

let private send (env: Wire.Env) (session: Wire.Session) (events: (string * string) list) =
    events |> List.fold (fun (session, _) (name, value) -> pump env session (event name value)) (session, "")

let private addCustomer (env: Wire.Env) (session: Wire.Session) (name: string) =
    send env session [ "customerNameChanged", name; "customerAddressChanged", "1 Main Street"; "customerAdded", "" ]

/// The customers stored on the repository, as anyone opening it would see.
let private storedCustomers (repository: InMemoryStore) =
    let config = Summa.Storage.Deployment.parse configuration |> Result.defaultWith (fun e -> failwith $"%A{e}")
    let binding = Summa.Storage.Storage.binding config |> Result.defaultWith (fun e -> failwith $"%A{e}")

    match Summa.Storage.Workspace.openBooks repository.Provider config binding config.Organizations.Head "github:583231" |> Async.RunSynchronously with
    | Ok(Summa.Storage.Workspace.Opened opened) -> opened.Books.Books.Customers |> Map.toList |> List.map (fun (id, c) -> id, c.Name)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a listed administrator sets the books up on GitHub and works in them; each change is one commit`` () =
    let repository = InMemoryStore()
    let env, session, reply = openAs repository "583231" "octocat"
    Assert.True(flag "booksNotSetUp" reply)
    Assert.True(flag "mayFound" reply)

    let session, reply = send env session [ "foundRequested", "" ]
    Assert.True(flag "isReady" reply)
    // The link asked for is where the page is, and what it shows.
    Assert.Equal(Some "/customers", session.Model.Router.Current)
    Assert.True(flag "onCustomers" reply)

    let session, reply = addCustomer env session "Acme"
    Assert.False(flag "hasError" reply)
    Assert.Equal(0, session.Model.Unsaved)
    Assert.Equal<(string * string) list>([ "CUST-0001", "Acme" ], storedCustomers repository)

[<Fact>]
let ``someone else's change in between is kept: the command is decided again on the books as they stand`` () =
    let repository = InMemoryStore()
    let first, firstSession, _ = openAs repository "583231" "octocat"
    let firstSession, _ = send first firstSession [ "foundRequested", "" ]

    // A second page opens the same books before the first adds anything.
    let second, secondSession, reply = openAs repository "583231" "octocat"
    Assert.True(flag "isReady" reply)

    addCustomer first firstSession "Acme" |> ignore
    let secondSession, reply = addCustomer second secondSession "Globex"
    Assert.False(flag "hasError" reply)

    // Both are kept, each under its own id: the second was numbered again.
    Assert.Equal<(string * string) list>([ "CUST-0001", "Acme"; "CUST-0002", "Globex" ], storedCustomers repository)
    Assert.Equal(2, secondSession.Model.Books.Value.Books.Customers.Count)

[<Fact>]
let ``someone signed in but not a member sees nothing, and an unlisted person cannot set the books up`` () =
    let repository = InMemoryStore()
    let _, _, reply = openAs repository "999" "stranger"
    Assert.True(flag "booksNotSetUp" reply)
    Assert.False(flag "mayFound" reply)

    let env, session, _ = openAs repository "583231" "octocat"
    send env session [ "foundRequested", "" ] |> ignore
    let _, _, reply = openAs repository "999" "stranger"
    Assert.True(flag "notAMember" reply)
    Assert.False(flag "isReady" reply)

[<Fact>]
let ``a change needs the capability: without it nothing is committed`` () =
    let repository = InMemoryStore()
    let env, session, _ = openAs repository "583231" "octocat"
    let session, _ = send env session [ "foundRequested", "" ]
    let model = { session.Model with Capabilities = set [ Summa.Access.Access.ViewFinancials ] }
    let session, reply = addCustomer env { session with Model = model } "Acme"
    Assert.True(flag "hasError" reply)
    Assert.Contains("does not include", session.Model.Error.Value)
    Assert.Empty(storedCustomers repository)


let private waiting (session: Wire.Session) = session.Model.Unsent.Waiting

[<Fact>]
let ``a change made while GitHub cannot be reached is kept, shown, and sent when it can`` () =
    browser <- None
    let repository = InMemoryStore()
    let env, session, _ = openAs repository "583231" "octocat"
    let session, _ = send env session [ "foundRequested", "" ]

    (networkOf repository).Online <- false
    let session, reply = addCustomer env session "Acme"
    // Shown at once, kept to send, and not on GitHub.
    Assert.Equal(1, session.Model.Books.Value.Books.Customers.Count)
    Assert.Equal(1, waiting session)
    Assert.True(flag "hasUnsent" reply)
    Assert.Equal(0, session.Model.Unsaved)

    // Still unreachable: sending again keeps it.
    let session, _ = send env session [ "sendUnsentRequested", "" ]
    Assert.Equal(1, waiting session)

    (networkOf repository).Online <- true
    let session, reply = send env session [ "sendUnsentRequested", "" ]
    Assert.Equal(0, waiting session)
    Assert.False(flag "hasUnsent" reply)
    Assert.Equal<(string * string) list>([ "CUST-0001", "Acme" ], storedCustomers repository)

[<Fact>]
let ``unsent changes outlive the page in this browser, held by one tab at a time`` () =
    browser <- Some(Browser())
    let repository = InMemoryStore()
    let env, session, _ = openAs repository "583231" "octocat"
    let session, _ = send env session [ "foundRequested", "" ]

    (networkOf repository).Online <- false
    let session, _ = addCustomer env session "Acme"
    Assert.Equal(1, waiting session)
    Assert.Contains(browser.Value.Storage.Keys, fun key -> key.StartsWith "arca.queue.")
    // Kept under the account GitHub resolved, so sign-out matches it by a stable id (Arca 0.4.0).
    Assert.Contains(browser.Value.Storage.Values, fun text -> text.Contains "subject:github:583231")

    // Another tab of the same browser does not take them: one lock, one holder.
    openAs repository "583231" "octocat" |> ignore
    Assert.Equal(1, browser.Value.Locks.Count)

    // The page closes (its lock goes with it) and GitHub can be reached again;
    // a new page sends what the old one kept before showing the books.
    browser.Value.Locks.Clear()
    (networkOf repository).Online <- true
    let _, reopened, _ = openAs repository "583231" "octocat"
    Assert.Equal(0, waiting reopened)
    Assert.Equal<(string * string) list>([ "CUST-0001", "Acme" ], storedCustomers repository)
    Assert.Equal(1, reopened.Model.Books.Value.Books.Customers.Count)
    browser <- None

[<Fact>]
let ``a kept change GitHub moved under, from an earlier page, waits for the person, who may give it up`` () =
    browser <- Some(Browser())
    let repository = InMemoryStore()
    let env, session, _ = openAs repository "583231" "octocat"
    let session, _ = send env session [ "foundRequested", "" ]

    (networkOf repository).Online <- false
    addCustomer env session "Acme" |> ignore

    // Meanwhile someone else adds a customer on GitHub.
    browser.Value.Locks.Clear()
    (networkOf repository).Online <- true
    let savedBrowser = browser
    browser <- None
    let elsewhere, elsewhereSession, _ = openAs repository "583231" "octocat"
    addCustomer elsewhere elsewhereSession "Globex" |> ignore
    browser <- savedBrowser

    // A new page in the first browser cannot decide the old change again: it waits.
    let env, reopened, reply = openAs repository "583231" "octocat"
    Assert.Equal(1, waiting reopened)
    Assert.True(flag "hasBlockedChange" reply)
    Assert.True(reopened.Model.Unsent.Blocked.IsSome)
    Assert.Equal<(string * string) list>([ "CUST-0001", "Globex" ], storedCustomers repository)

    let after, reply = send env reopened [ "abandonUnsentRequested", "" ]
    Assert.Equal(0, waiting after)
    Assert.False(flag "hasUnsent" reply)
    browser <- None

[<Fact>]
let ``checking the books shows a record edited outside Summa, and changes nothing`` () =
    browser <- None
    let repository = InMemoryStore()
    let env, session, _ = openAs repository "583231" "octocat"
    let session, _ = send env session [ "foundRequested", "" ]
    let session, _ = addCustomer env session "Acme"

    let session, _ = send env session [ "checkRequested", "" ]
    Assert.Equal(Checked [], session.Model.Check)

    // Someone rewrites the customer's file on GitHub, by hand.
    let config = Summa.Storage.Deployment.parse configuration |> Result.defaultWith (fun e -> failwith $"%A{e}")
    let binding = Summa.Storage.Storage.binding config |> Result.defaultWith (fun e -> failwith $"%A{e}")
    let ns = Summa.Storage.Storage.organizationNamespace config binding "org_acme" |> Result.defaultWith (fun e -> failwith $"%A{e}")
    let path = RelativePath.parse "records/summa.customer/CUST-0001.json" |> Result.defaultWith (fun e -> failwith $"%A{e}")

    let content =
        match repository.Provider.Read ns path |> Async.RunSynchronously with
        | Ok(ReadOutcome.Found found) -> found.Content
        | other -> failwith $"%A{other}"

    repository.WriteExternally(ns.Location, RelativePath.render ns.Root + "/records/summa.customer/CUST-0001.json", Some content)
    let session, reply = send env session [ "checkRequested", "" ]

    match session.Model.Check with
    | Checked [ finding ] -> Assert.Contains("CUST-0001", finding)
    | other -> failwith $"%A{other}"

    Assert.True(flag "booksChecked" reply)
    Assert.Equal<(string * string) list>([ "CUST-0001", "Acme" ], storedCustomers repository)
