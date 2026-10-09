/// Financial migrations and relocations through Arca's migration workflow
/// (WI-0038; SUM0-032, SUM3-010, SUM3-039): checked before anything is
/// written, a backup first, the copy read back and reconciled, the source
/// untouched until it is retired.
module Summa.Storage.Tests.FinancialMigrationTests

open System
open Xunit
open Arca
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Commands
open Summa.Operations
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private kevin = Actor.person "583231" "kevin" "corr-1"
let private key = Array.init 32 byte |> Backup.BackupKey.create |> ok
let private nonce = Array.init 12 (fun i -> byte (100 + i))
let private asOf = DateOnly(2026, 12, 31)

let private arcaActor: Arca.Actor =
    { Kind = ActorKind.Human
      Id = ActorId.create "github:583231" |> ok }

let private run (provider: StorageProvider) ns capability key transition =
    execute provider defaultApprovalGates ns 3 { Actor = kevin; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

/// Acme's folder with a chart, an issued invoice and a payment applied.
let private source () =
    let store = InMemoryStore()
    let binding = bindingOf production
    let ns = Storage.organizationNamespace production binding "org_acme" |> ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> ok
    store.Provider.Commit found |> Async.RunSynchronously |> ok |> ignore
    run store.Provider ns ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }) |> ok |> ignore
    run store.Provider ns IssueInvoice "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b })) |> ok |> ignore
    run store.Provider ns RecordPayment "pay" (recordPayment ledgerContext { payment with Amount = usd 300000L }) |> ok |> ignore
    run store.Provider ns AllocatePayment "allocate" (allocate ledgerContext { allocation with Amount = usd 300000L }) |> ok |> ignore
    store, ns

/// Acme's folder at another repository.
let private moved () =
    let binding = bindingOf production
    let dataset = Organization.dataset "org_acme" |> ok

    let location =
        Deployment.dataLocation { Owner = "acme"; Repository = "summa-moved"; Branch = "main"; BasePath = "deployments/prod" }
        |> Result.defaultWith (fun e -> failwith $"%A{e}")

    Namespace.ofDataset binding dataset (Some location) |> Result.defaultWith (fun e -> failwith $"%A{e}")

let private migrate (store: InMemoryStore) ns target definition =
    FinancialMigration.run store.Provider store.Provider ns target arcaActor key nonce "ar" asOf definition |> Async.RunSynchronously

let private books (store: InMemoryStore) ns =
    (FinancialRecords.load (readAll store.Provider ns |> Async.RunSynchronously |> ok |> List.filter (fun o -> match Layout.keyOf o.Path with Some k -> FinancialRecords.isFinancial k.Type | None -> false))).State

let private targetObjects (store: InMemoryStore) (target: Namespace) =
    store.State.Objects |> Map.toList |> List.filter (fun (address, _) -> address.Contains "summa-moved")

[<Fact>]
let ``a relocation copies the same books, after a backup, and leaves the source as it was`` () =
    let store, ns = source ()
    let before = books store ns
    let target = moved ()

    let outcome =
        migrate store ns target (FinancialMigration.relocation "relocate-acme-2026" "move Acme's books to summa-moved" "keep using summa-data, which is untouched until retired, and the sealed backup")
        |> ok

    Assert.True(outcome.Report.Written > 0)
    // The copy is at the other repository (so an empty one there means nothing was written).
    Assert.NotEmpty(targetObjects store target)
    // The backup opens with the key and holds the source as it was.
    Assert.True(Backup.unseal key outcome.Backup |> Result.isOk)
    Assert.Empty outcome.Before
    Assert.Empty outcome.After
    let copied = books store target
    Assert.True((before.Books.Ledger.Entries = copied.Books.Ledger.Entries))
    Assert.True((before.Books.Invoices = copied.Books.Invoices))
    Assert.True((before.Payments = copied.Payments))
    // The source still opens and holds the same books.
    Assert.True((before.Books.Invoices = (books store ns).Books.Invoices))

[<Fact>]
let ``a migration that does not say how to roll back does not run`` () =
    let store, ns = source ()
    let target = moved ()

    match migrate store ns target (FinancialMigration.relocation "relocate-acme-2026" "move" " ") with
    | Error FinancialMigration.NoRollback -> ()
    | other -> failwith $"%A{other}"

    Assert.Empty(targetObjects store target)

[<Fact>]
let ``a transform that changes the balances without declaring it is refused before anything is written`` () =
    let store, ns = source ()
    let target = moved ()

    // Halves every payment: the cash and receivable balances change.
    let halve (record: Record) =
        if RecordType.value record.Type <> "summa.payment" then
            Ok record
        else
            match record.Body with
            | Json.Object members ->
                let amount =
                    members
                    |> List.map (function
                        | "amount", Json.Object money ->
                            "amount", Json.Object(money |> List.map (function "minor", Json.Number n -> "minor", Json.Number(n / 2m) | other -> other))
                        | other -> other)

                Ok { record with Body = Json.Object amount }
            | _ -> Error "not an object"

    let definition =
        { FinancialMigration.relocation "halve-payments" "an unsafe change" "use the source" with
            Transform = halve }

    match migrate store ns target definition with
    | Error(FinancialMigration.Unsafe problems) -> Assert.NotEmpty problems
    | other -> failwith $"%A{other}"

    Assert.Empty(targetObjects store target)

[<Fact>]
let ``a schema migration writes the new versions to the copy and the copy is the books that were checked`` () =
    let store, ns = source ()
    let target = moved ()

    // Customers recorded as subject to tax, at customer schema 2.
    let subjectToTax (record: Record) =
        if RecordType.value record.Type <> "summa.customer" then
            Ok record
        else
            match record.Body with
            | Json.Object members ->
                Ok
                    { record with
                        SchemaVersion = 2
                        Body = Json.Object(members @ [ "tax", Json.Object [ "kind", Json.String "subject"; "jurisdiction", Json.String "New York" ] ]) }
            | _ -> Error "not an object"

    let definition =
        { FinancialMigration.relocation "customers-subject-to-tax" "record every customer as subject to New York tax" "use the source, untouched until retired" with
            RecordSchemas = Map.ofList [ "summa.customer", 2 ]
            Transform = subjectToTax }

    migrate store ns target definition |> ok |> ignore
    let copied = books store target
    Assert.Equal(SubjectToTax(Some "New York"), copied.Books.Customers["CUST-ABC"].Tax)
    Assert.Equal(TaxNotAssessed, (books store ns).Books.Customers["CUST-ABC"].Tax)

[<Fact>]
let ``the source is retired only when asked, and only while the copy still holds what it implies`` () =
    let store, ns = source ()
    let target = moved ()
    let definition = FinancialMigration.relocation "relocate-acme-2026" "move" "use the source and the backup"
    migrate store ns target definition |> ok |> ignore
    let plan = FinancialMigration.plan definition ns target arcaActor |> ok

    FinancialMigration.retire store.Provider store.Provider plan |> Async.RunSynchronously |> ok

    // A retired source is refused.
    match compatibility store.Provider ns |> Async.RunSynchronously with
    | Error(Untrustworthy _) -> ()
    | other -> failwith $"%A{other}"
