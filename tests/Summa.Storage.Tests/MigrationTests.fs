/// The organization manifest's schema 2 (WI-0037, DF-SUMMA-2026-0012): it
/// names the customer credits, customer deposits and bad-debt accounts, and
/// schema 1 books are migrated explicitly, from their own chart, never read
/// as current (SUM0-031, SUM0-032, SUM3-009, SUM3-010).
module Summa.Storage.Tests.MigrationTests

open System.Text.Json.Nodes
open Xunit
open Arca
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Commands
open Summa.Storage.Diagnostics
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private added = set [ "customerCreditsAccount"; "customerDepositsAccount"; "badDebtAccount" ]

/// A manifest's body as schema 1 wrote it: without the three accounts.
let private v1Body (manifest: Organization.OrganizationManifest) =
    match Organization.body manifest with
    | Json.Object members ->
        Json.Object(
            members
            |> List.map (function
                | "accounting", Json.Object accounting -> "accounting", Json.Object(accounting |> List.filter (fst >> added.Contains >> not))
                | other -> other)
        )
    | other -> other

/// The manifest's stored text at schema 1.
let private v1Text (manifest: Organization.OrganizationManifest) =
    Record.encode
        Record.DefaultMaxBytes
        { Id = RecordId.create manifest.OrganizationId |> ok
          Type = Organization.manifestType
          SchemaVersion = 1
          Mutability = Mutability.Mutable
          Body = v1Body manifest }
    |> ok

let private stored (organizationId: string) (content: string) =
    { Path = Organization.path organizationId |> ok
      Content = content
      Revision = Revision "r1" }

let private ledgerOf (accounts: Account list) =
    accounts |> List.fold (fun ledger a -> addAccount ledgerContext a ledger |> Books.ok) Summa.Ledger.Ledger.empty

[<Fact>]
let ``schema 2 names the credit, deposit and bad-debt accounts, and reads back as it was written`` () =
    Assert.Equal(2, Organization.schema.Current)
    Assert.Equal(("2100", "2200", "6500"), (acme.Accounting.CustomerCreditsAccount, acme.Accounting.CustomerDepositsAccount, acme.Accounting.BadDebtAccount))

    match Organization.decodeStored "org_acme" (stored "org_acme" (Organization.encode acme |> ok)) with
    | Ok(Organization.CurrentManifest read) -> Assert.Equal(acme, read)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a schema 1 manifest is read only to be migrated, never as current`` () =
    match Organization.decodeStored "org_acme" (stored "org_acme" (v1Text acme)) with
    | Ok(Organization.NeedsMigration(1, body)) -> Assert.Equal(v1Body acme, body)
    | other -> failwith $"%A{other}"

    Assert.Equal("SUMMA.INTEGRITY.INCOMPATIBLE_SCHEMA", Organization.decode "org_acme" (stored "org_acme" (v1Text acme)) |> codeOf)

[<Fact>]
let ``the migration names the accounts from the books' own chart`` () =
    let migrated = Migrations.organizationV2 chart (v1Body acme) |> ok
    Assert.Equal(Ok acme, Organization.ofBody migrated)

    match Organization.decodeStored "org_acme" (stored "org_acme" (v1Text acme)) |> ok |> Migrations.currentManifest chart with
    | Ok(manifest, true) -> Assert.Equal(acme, manifest)
    | other -> failwith $"%A{other}"

    match Organization.decodeStored "org_acme" (stored "org_acme" (Organization.encode acme |> ok)) |> ok |> Migrations.currentManifest chart with
    | Ok(manifest, false) -> Assert.Equal(acme, manifest)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the migration refuses a chart without the accounts, or with them as the wrong type, and never invents one`` () =
    let without code = accounts |> List.filter (fun a -> a.Code <> code) |> ledgerOf
    let refused = Migrations.organizationV2 (without "2200") (v1Body acme)
    Assert.Equal("SUMMA.INTEGRITY.MIGRATION_UNSAFE", codeOf refused)
    Assert.Contains("there is no account 2200", (match refused with Error d -> describe d | Ok _ -> ""))

    let mistyped = accounts |> List.map (fun a -> if a.Code = "6500" then { a with Type = Revenue } else a) |> ledgerOf
    let wrong = Migrations.organizationV2 mistyped (v1Body acme)
    Assert.Contains("account 6500 is Revenue, not Expense", (match wrong with Error d -> describe d | Ok _ -> ""))

    // A body that already names them is not schema 1.
    Assert.Equal("SUMMA.INTEGRITY.MIGRATION_UNSAFE", Migrations.organizationV2 chart (Organization.body acme) |> codeOf)

[<Fact>]
let ``books kept in this browser are migrated as they are read, once`` () =
    let manifest, books = LocalSnapshot.start ledgerContext "Local" acme.Company "ACH" |> ok
    let current = LocalSnapshot.encode manifest books |> ok
    let snapshot = (match JsonNode.Parse current with null -> failwith "not JSON" | node -> node.AsObject())
    snapshot["manifest"] <- JsonValue.Create(v1Text manifest)
    let older = snapshot.ToJsonString()

    let restored = LocalSnapshot.decode older |> ok
    Assert.True restored.Migrated
    // As the same books at schema 2 read back.
    Assert.Equal((LocalSnapshot.decode current |> ok).Manifest, restored.Manifest)
    Assert.Empty restored.Problems

    let again = LocalSnapshot.decode (LocalSnapshot.encode restored.Manifest restored.Books |> ok) |> ok
    Assert.False again.Migrated

[<Fact>]
let ``a schema 1 folder stays read-only, whatever its Arca manifest declares, until it is migrated`` () =
    let folder version =
        { Scope = ManifestScope.Dataset(DatasetId.create "org_acme" |> ok)
          Application = Application.id
          StorageSchema = Manifest.StorageSchema
          ProviderContract = StorageContract.Version
          RecordSchemas = Map.ofList [ "summa.organization", version ]
          CreatedBy = { Kind = ActorKind.Human; Id = ActorId.create "github:1" |> ok }
          CreatedAt = at
          Location = (bindingOf production).Location
          Migration = None }

    let older = Organization.NeedsMigration(1, v1Body acme)

    for version in [ 1; 2 ] do
        match Compatibility.storedAccess [ Organization.schema ] older (folder version) with
        | Compatibility.ReadOnly reasons -> Assert.Contains("SUMMA.INTEGRITY.INCOMPATIBLE_SCHEMA", reasons |> List.map code)
        | other -> failwith $"%A{other}"

    Assert.Equal(Compatibility.ReadWrite, Compatibility.storedAccess [ Organization.schema ] (Organization.CurrentManifest acme) (folder 2))
    Assert.True(match Compatibility.storedAccess [ Organization.schema ] older (folder 3) with Compatibility.Refused _ -> true | _ -> false)

let private kevin = Actor.person "583231" "kevin" "corr-1"

let private founded () =
    let binding = bindingOf production
    let store = InMemoryStore()
    let ns = Storage.organizationNamespace production binding "org_acme" |> ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> ok
    store.Provider.Commit found |> Async.RunSynchronously |> ok |> ignore
    store, ns

let private run (store: InMemoryStore) ns capability key transition =
    execute store.Provider defaultApprovalGates ns 3 { Actor = kevin; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

let private file (ns: Namespace) (relative: string) = RelativePath.render ns.Root + "/" + relative

let private contentOf (store: InMemoryStore) (ns: Namespace) (relative: string) =
    match store.Provider.Read ns (RelativePath.parse relative |> ok) |> Async.RunSynchronously |> ok with
    | ReadOutcome.Found o -> o.Content
    | ReadOutcome.Absent -> failwith "absent"

/// Acme's books on a store, as a schema 1 Summa left them.
let private storedAtSchema1 () =
    let store, ns = founded ()
    run store ns ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }) |> ok |> ignore
    let manifestFile = "records/summa.organization/org_acme.json"
    store.WriteExternally(ns.Location, file ns manifestFile, Some(v1Text acme))
    let arca = (contentOf store ns "arca-manifest.json").Replace("\"summa.organization\":2", "\"summa.organization\":1")
    store.WriteExternally(ns.Location, file ns "arca-manifest.json", Some arca)
    store, ns

let private migrate (store: InMemoryStore) ns actor =
    Migrations.migrate store.Provider defaultApprovalGates ns actor "migrate-org-0001" |> Async.RunSynchronously

[<Fact>]
let ``books on a store at schema 1 refuse commands until an administrator migrates them, in one commit`` () =
    let store, ns = storedAtSchema1 ()

    match compatibility store.Provider ns |> Async.RunSynchronously with
    | Ok(Compatibility.ReadOnly _) -> ()
    | other -> failwith $"%A{other}"

    match run store ns IssueInvoice "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b })) with
    | Error(Incompatible(Compatibility.ReadOnly _)) -> ()
    | other -> failwith $"%A{other}"

    match migrate store ns (Actor.person "999" "stranger" "corr-3") with
    | Error(NotAuthorized _) -> ()
    | other -> failwith $"%A{other}"

    match migrate store ns kevin with
    | Ok(Migrations.Committed _) -> ()
    | other -> failwith $"%A{other}"

    match Organization.decode "org_acme" (stored "org_acme" (contentOf store ns "records/summa.organization/org_acme.json")) with
    | Ok manifest -> Assert.Equal(acme, manifest)
    | other -> failwith $"%A{other}"

    Assert.Equal(Ok Compatibility.ReadWrite, compatibility store.Provider ns |> Async.RunSynchronously)
    Assert.Equal(Ok Migrations.AlreadyCurrent, migrate store ns kevin |> Result.mapError (fun _ -> ()))
    Assert.True(run store ns IssueInvoice "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b })) |> Result.isOk)

[<Fact>]
let ``a schema 1 folder whose chart lacks the accounts is not migrated, and nothing is written`` () =
    let store, ns = founded ()
    let lacking = accounts |> List.filter (fun a -> a.Code <> "2100") |> ledgerOf
    run store ns ManageSettings "chart" (fun r -> Ok { r with Books = openBooks lacking }) |> ok |> ignore
    store.WriteExternally(ns.Location, file ns "records/summa.organization/org_acme.json", Some(v1Text acme))
    let before = contentOf store ns "records/summa.organization/org_acme.json"

    match migrate store ns kevin with
    | Error(Untrustworthy [ MigrationUnsafe detail ]) -> Assert.Contains("there is no account 2100", detail)
    | other -> failwith $"%A{other}"

    Assert.Equal(before, contentOf store ns "records/summa.organization/org_acme.json")
