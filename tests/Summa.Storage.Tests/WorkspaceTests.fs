/// An organization's books on a store, for the signed-in person (WI-0037):
/// opening, setting up, confirming an administrator, migrating, and what
/// opening refuses (SUM0-007, SUM0-033, SUM3-009, SUM3-040).
module Summa.Storage.Tests.WorkspaceTests

open Xunit
open Arca
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Diagnostics
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private acmeConfig = Deployment.organization production "org_acme" |> Option.get
let private kevin = { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" }
let private stranger = "github:999"

let private opening (store: InMemoryStore) (actorId: string) =
    Workspace.openBooks store.Provider production (bindingOf production) acmeConfig actorId |> Async.RunSynchronously |> ok

let private commitAll (store: InMemoryStore) (operations: Operation list) =
    operations |> List.iter (fun operation -> store.Provider.Commit operation |> Async.RunSynchronously |> ok |> ignore)

let private founded () =
    let store = InMemoryStore()

    Workspace.foundingOperations store.Provider production (bindingOf production) RepositoryVisibility.Private (context "found") acmeConfig kevin
    |> Async.RunSynchronously
    |> ok
    |> commitAll store

    store

[<Fact>]
let ``an empty repository is set up only by a listed administrator, Summa's namespace first`` () =
    let store = InMemoryStore()

    match opening store "github:583231" with
    | Workspace.NotSetUp Governance.Found -> ()
    | other -> failwith $"%A{other}"

    match opening store stranger with
    | Workspace.NotSetUp(Governance.Refused reason) -> Assert.Contains("listed administrators", reason)
    | other -> failwith $"%A{other}"

    let operations =
        Workspace.foundingOperations store.Provider production (bindingOf production) RepositoryVisibility.Private (context "found") acmeConfig kevin
        |> Async.RunSynchronously
        |> ok

    Assert.Equal(2, operations.Length)
    commitAll store operations

    match opening store "github:583231" with
    | Workspace.Opened opened ->
        Assert.Equal("Acme Consulting", opened.Manifest.DisplayName)
        Assert.Equal(Compatibility.ReadWrite, opened.Access)
        Assert.Contains(ManageUsers, opened.Capabilities)
        Assert.True(opened.Books.Books.Invoices.IsEmpty)
    | other -> failwith $"%A{other}"

    // Founding again where Summa's namespace exists sets up only the organization.
    let again =
        Workspace.foundingOperations store.Provider production (bindingOf production) RepositoryVisibility.Private (context "again") acmeConfig kevin
        |> Async.RunSynchronously
        |> ok

    Assert.Equal(1, again.Length)

[<Fact>]
let ``production data is never set up in a public repository`` () =
    let store = InMemoryStore()

    match
        Workspace.foundingOperations store.Provider production (bindingOf production) RepositoryVisibility.Public (context "found") acmeConfig kevin
        |> Async.RunSynchronously
    with
    | Error(Commands.Unstorable problems) -> Assert.Contains("SUMMA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY", problems |> List.map code)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``someone signed in but not on the roster sees nothing of the books`` () =
    let store = founded ()

    match opening store stranger with
    | Workspace.NotAMember -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``books at an older manifest schema wait for an administrator's migration`` () =
    let store = founded ()
    let ns = Storage.organizationNamespace production (bindingOf production) "org_acme" |> ok
    let kevinActor = Summa.Access.Actor.person "583231" "kevin" "corr-1"
    // Founding wrote the starting chart, with 2100, 2200 and 6500 (DF-SUMMA-2026-0024).

    // As a schema 1 Summa left the manifest.
    let file = RelativePath.render ns.Root + "/records/summa.organization/org_acme.json"
    let manifest = Storage.manifestFor acmeConfig at

    let v1 =
        match Organization.toRecord manifest with
        | Ok record ->
            let body =
                match record.Body with
                | Json.Object members ->
                    Json.Object(
                        members
                        |> List.map (function
                            | "accounting", Json.Object a ->
                                "accounting", Json.Object(a |> List.filter (fun (k, _) -> not (List.contains k [ "customerCreditsAccount"; "customerDepositsAccount"; "badDebtAccount" ])))
                            | other -> other)
                    )
                | other -> other

            Record.encode Record.DefaultMaxBytes { record with SchemaVersion = 1; Body = body } |> ok
        | Error e -> failwith $"%A{e}"

    store.WriteExternally(ns.Location, file, Some v1)

    match opening store "github:583231" with
    | Workspace.NeedsMigration true -> ()
    | other -> failwith $"%A{other}"

    Migrations.migrate store.Provider defaultApprovalGates ns kevinActor "migrate-org-0001" |> Async.RunSynchronously |> ok |> ignore

    match opening store "github:583231" with
    | Workspace.Opened opened -> Assert.Equal("6500", opened.Manifest.Accounting.BadDebtAccount)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a roster without a listed administrator is held until a listed account confirms itself`` () =
    let store = InMemoryStore()
    let ns = Storage.organizationNamespace production (bindingOf production) "org_acme" |> ok
    // Founded by someone the configuration no longer lists.
    let former = { PrincipalId = "github:111"; Kind = Human; DisplayName = "former" }

    Governance.found production (bindingOf production) RepositoryVisibility.Private None (context "found") (Storage.manifestFor acmeConfig at) former
    |> ok
    |> List.singleton
    |> commitAll store

    match opening store "github:583231" with
    | Workspace.Held true -> ()
    | other -> failwith $"%A{other}"

    match opening store stranger with
    | Workspace.Held false -> ()
    | other -> failwith $"%A{other}"

    let objects = Commands.readAll store.Provider ns |> Async.RunSynchronously |> ok
    Assert.True(Workspace.confirmation ns (context "confirm") acmeConfig objects { kevin with PrincipalId = stranger } |> Result.isError)
    Workspace.confirmation ns (context "confirm") acmeConfig objects kevin |> ok |> List.singleton |> commitAll store

    match opening store "github:583231" with
    | Workspace.Opened opened -> Assert.Contains(ManageUsers, opened.Capabilities)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``books that fail their checks are not opened`` () =
    let store = founded ()
    let ns = Storage.organizationNamespace production (bindingOf production) "org_acme" |> ok
    let file = RelativePath.render ns.Root + "/records/summa.organization/org_acme.json"
    store.WriteExternally(ns.Location, file, Some "{not a record")

    match opening store "github:583231" with
    | Workspace.Unusable problems -> Assert.NotEmpty problems
    | other -> failwith $"%A{other}"

[<Fact>]
let ``start-up reads Summa's application manifest and refuses books that need a newer Summa`` () =
    let empty = InMemoryStore()
    Assert.Equal(Ok(), Workspace.checkApplication empty.Provider (bindingOf production) |> Async.RunSynchronously |> Result.mapError ignore)

    let store = founded ()
    Assert.Equal(Ok(), Workspace.checkApplication store.Provider (bindingOf production) |> Async.RunSynchronously |> Result.mapError ignore)

    let application = Storage.applicationNamespace (bindingOf production) |> ok
    let file = RelativePath.render application.Root + "/" + RelativePath.render Application.path

    let current =
        match store.Provider.Read application Application.path |> Async.RunSynchronously |> ok with
        | ReadOutcome.Found found -> found.Content
        | ReadOutcome.Absent -> failwith "absent"
        | ReadOutcome.Erased _ -> failwith "erased"

    let newer = current.Replace($"\"minimumApplicationVersion\":\"{Application.ApplicationVersion}\"", "\"minimumApplicationVersion\":\"9.0.0\"")
    Assert.NotEqual<string>(current, newer)
    store.WriteExternally(application.Location, file, Some newer)

    match Workspace.checkApplication store.Provider (bindingOf production) |> Async.RunSynchronously with
    | Error(Commands.Untrustworthy problems) -> Assert.Contains("need Summa 9.0.0", problems |> List.map describe |> String.concat " ")
    | other -> failwith $"%A{other}"

[<Fact>]
let ``founding writes the starting chart as the organization's accounts, with one audit event naming each account`` () =
    let store = founded ()

    match opening store kevin.PrincipalId with
    | Workspace.Opened opened ->
        let ledger = opened.Books.Books.Ledger
        // Every account of the starting chart is the organization's own record.
        Assert.Equal<Summa.Ledger.Ledger.Account list>(Organization.startingChart, ledger.Accounts |> Map.toList |> List.map snd |> List.sortBy _.Code)

        // One event by the founder, and it names every account it created: code, name and type.
        match ledger.Audit |> List.filter (fun a -> a.What = "chart-founded") with
        | [ founding ] ->
            Assert.Equal(kevin.PrincipalId, founding.Who)
            Assert.Equal("org_acme", founding.Subject)
            let named = founding.Change |> Option.map _.Changed |> Option.defaultValue []
            Assert.Equal(Organization.startingChart.Length, named.Length)

            for account in Organization.startingChart do
                Assert.Contains($"account {account.Code}: {account.Name} ({account.Type})", named)

            Assert.Contains("account 4000: Consulting Revenue (Revenue)", named)
        | other -> failwith $"%A{other}"

        Assert.DoesNotContain(ledger.Audit, fun a -> a.What = "account-saved")
    | other -> failwith $"%A{other}"

/// A provider as the browser's bridge makes it: every read and listing waits
/// until a "kernel" answers it, one answer at a time, on one thread.
type private Parked(inner: StorageProvider) =
    let waiting = System.Collections.Generic.Queue<unit -> unit>()

    let park (work: Async<'a>) : Async<'a> =
        Async.FromContinuations(fun (resume, _, _) ->
            let answer = Async.RunSynchronously work
            waiting.Enqueue(fun () -> resume answer))

    member _.Provider: StorageProvider =
        { inner with
            Read = fun ns path -> park (inner.Read ns path)
            List = fun ns path -> park (inner.List ns path) }

    member _.InFlight = waiting.Count

    /// The host's turn: a resumption waits for the driver, like the bridge's.
    member _.Turn: Commands.Turn = { Defer = fun resume -> waiting.Enqueue resume }

    /// Answers until nothing waits; the most requests ever waiting at once.
    member _.Drive() =
        let mutable most = waiting.Count

        while waiting.Count > 0 do
            (waiting.Dequeue()) ()
            most <- max most waiting.Count

        most

[<Fact>]
let ``reads run at most six at a time and finish within the answers, on the caller's thread`` () =
    let store = founded ()
    let ns = Storage.organizationNamespace production (bindingOf production) "org_acme" |> ok
    let parked = Parked store.Provider
    let thread = System.Threading.Thread.CurrentThread.ManagedThreadId
    let mutable result = None

    Async.StartWithContinuations(
        Commands.readAllKnowing parked.Turn Map.empty parked.Provider ns,
        (fun r -> result <- Some(r, System.Threading.Thread.CurrentThread.ManagedThreadId)),
        (fun ex -> raise ex),
        ignore
    )

    let most = parked.Drive()

    // Done by the time the last answer is handled, on this thread: no hop to the thread pool.
    match result with
    | Some(Ok objects, finishedOn) ->
        Assert.Equal<int>(thread, finishedOn)
        Assert.True(objects.Length >= Organization.startingChart.Length)
        Assert.InRange(most, 2, Commands.ReadWidth)
    | other -> failwith $"not finished within the answers: %A{other}"

    // The same objects as read one by one.
    let direct = Commands.readAll store.Provider ns |> Async.RunSynchronously |> ok
    Assert.Equal<string list>(direct |> List.map (fun o -> RelativePath.render o.Path) |> List.sort, (match result with Some(Ok o, _) -> o | _ -> []) |> List.map (fun o -> RelativePath.render o.Path) |> List.sort)

[<Fact>]
let ``an opening reads every file and listing through a pin at the commit it observed, when the host offers pins`` () =
    let store = founded ()
    let provider = store.Provider
    let mutable unpinned = 0
    let mutable pinnedAt: ChangeToken list = []
    let mutable pinnedReads = 0

    // The provider's own reads count as unpinned; a pin records its token and reads.
    let counted =
        { provider with
            Read = fun ns path -> (unpinned <- unpinned + 1; provider.Read ns path)
            List = fun ns path -> (unpinned <- unpinned + 1; provider.List ns path) }

    let pins (token: ChangeToken) : PinnedReads option =
        pinnedAt <- pinnedAt @ [ token ]

        Some
            { At = token
              NamespaceState = provider.NamespaceState
              Read = fun ns path -> (pinnedReads <- pinnedReads + 1; provider.Read ns path)
              List = fun ns path -> (pinnedReads <- pinnedReads + 1; provider.List ns path) }

    let ns = Storage.organizationNamespace production (bindingOf production) "org_acme" |> ok
    let observed = provider.NamespaceState ns |> Async.RunSynchronously |> ok

    match Workspace.openBooksKnowing Commands.Turn.now Map.empty pins counted production (bindingOf production) acmeConfig kevin.PrincipalId |> Async.RunSynchronously |> ok with
    | Workspace.Opened opened ->
        // Pinned once, at the repository token the namespace state named.
        Assert.Equal<ChangeToken list>([ observed.RepositoryToken ], pinnedAt)
        Assert.Equal(Some observed, fst opened.Read)
        // Only the two manifests are read before the state is observed; everything after goes through the pin.
        Assert.Equal(2, unpinned)
        Assert.True(pinnedReads >= Organization.startingChart.Length)
        // The same books as an opening without pins.
        match opening store kevin.PrincipalId with
        | Workspace.Opened plain -> Assert.Equal<string list>(snd plain.Read |> List.map (fun o -> RelativePath.render o.Path), snd opened.Read |> List.map (fun o -> RelativePath.render o.Path))
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"
