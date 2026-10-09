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

    Commands.execute store.Provider defaultApprovalGates ns 3
        { Actor = kevinActor
          Capability = ManageSettings
          Summary = "chart"
          IdempotencyKey = "cmd-chart-0001"
          Transition = fun r -> Ok { r with Books = Summa.Ledger.Invoicing.openBooks chart } }
    |> Async.RunSynchronously
    |> ok
    |> ignore

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
