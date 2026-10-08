/// Integrity before availability (WI-0021): invariants, outside edits,
/// schema compatibility and migration safety (SUM0-029..032, SUM0-047,
/// SUM3-004, SUM3-009, SUM3-010, SUM3-019).
module Summa.Storage.Tests.IntegrityTests

open System
open Xunit
open Arca
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Commands
open Summa.Storage.Diagnostics
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private rules (r: Receivables) = Invariants.check r |> List.map _.Rule |> List.distinct

[<Fact>]
let ``sound books break no invariant`` () =
    Assert.Empty(Invariants.check (full ()))

[<Fact>]
let ``every impossible condition SUM3-019 names is an integrity failure`` () =
    let r = full ()
    let ledger = r.Books.Ledger

    // An unbalanced posted journal.
    let entry = ledger.Entries["JE-000003"]
    let unbalanced = { r with Books = { r.Books with Ledger = { ledger with Entries = ledger.Entries.Add("JE-000003", { entry with Lines = [ entry.Lines.Head; { entry.Lines[1] with Side = Credit(usd 1L) } ] }) } } }
    Assert.Contains("posted-entry-balances", rules unbalanced)

    // Allocation greater than the payment, and greater than the invoice balance.
    let over = { r with Allocations = r.Allocations @ [ { r.Allocations.Head with Id = "AL-X"; JournalEntryId = "JE-000002" } ] }
    Assert.Contains("allocation-within-payment", rules over)
    Assert.Contains("no-negative-receivable", rules over)

    // An issued invoice without its ledger entry.
    let orphan = { r with Books = { r.Books with Ledger = { ledger with Entries = ledger.Entries.Remove "JE-000001" } } }
    Assert.Contains("issued-invoice-has-entry", rules orphan)

    // Duplicate invoice numbers.
    let invoice = r.Books.Invoices["INV-001"]
    let duplicate = { r with Books = { r.Books with Invoices = r.Books.Invoices.Add("INV-002", { invoice with InvoiceId = "INV-002" }) } }
    Assert.Contains("unique-invoice-numbers", rules duplicate)

    // A reversal that does not mirror its entry.
    let reversal = ledger.Entries["JE-000004"]
    let wrong = { r with Books = { r.Books with Ledger = { ledger with Entries = ledger.Entries.Add("JE-000004", { reversal with Lines = entry.Lines }) } } }
    Assert.Contains("reversals-mirror-their-entry", rules wrong)

let private founded () =
    let binding = bindingOf production
    let store = InMemoryStore()
    let ns = Storage.organizationNamespace production binding "org_acme" |> Support.ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    store, ns

let private kevin = Actor.person "583231" "kevin" "corr-1"

let private run (store: InMemoryStore) ns capability key transition =
    execute store.Provider defaultApprovalGates ns 3 { Actor = kevin; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

let private populated () =
    let store, ns = founded ()
    run store ns ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }) |> Support.ok |> ignore

    run store ns IssueInvoice "issue" (fun r ->
        saveDraft ledgerContext draft r.Books
        |> Result.bind (issue ledgerContext issueRequest)
        |> Result.map (fun (b, _) -> { r with Books = b }))
    |> Support.ok
    |> ignore

    store, ns

let private file (ns: Namespace) (relative: string) = RelativePath.render ns.Root + "/" + relative

let private contentOf (store: InMemoryStore) (ns: Namespace) (relative: string) =
    match store.Provider.Read ns (RelativePath.parse relative |> Support.ok) |> Async.RunSynchronously |> Support.ok with
    | ReadOutcome.Found o -> o.Content
    | ReadOutcome.Absent -> failwith "absent"

[<Fact>]
let ``a clean folder audits clean`` () =
    let store, ns = populated ()
    Assert.Empty(Verification.audit store.Provider ns |> Async.RunSynchronously |> Support.ok)

[<Fact>]
let ``an outside edit that passes every check is reported, not trusted, and does not stop commands`` () =
    let store, ns = populated ()
    let path = "records/summa.customer/CUST-ABC.json"
    // Rewritten outside Summa with identical, valid content.
    store.WriteExternally(ns.Location, file ns path, Some(contentOf store ns path))
    let problems = Verification.audit store.Provider ns |> Async.RunSynchronously |> Support.ok
    Assert.Equal<string list>([ "SUMMA.INTEGRITY.EDITED_OUTSIDE" ], problems |> List.map code)
    Assert.Empty(Verification.blocking problems)
    Assert.True(run store ns RecordPayment "pay" (recordPayment ledgerContext payment) |> Result.isOk)

[<Fact>]
let ``a posted record changed after it was written is an integrity failure and stops commands`` () =
    let store, ns = populated ()
    let path = "records/summa.entry/2026/10/JE-000001.json"
    let original = contentOf store ns path
    let tampered = original.Replace("Invoice INV-2026-0001", "Invoice INV-2026-0099")
    store.WriteExternally(ns.Location, file ns path, Some tampered)
    let problems = Verification.audit store.Provider ns |> Async.RunSynchronously |> Support.ok
    Assert.Contains("SUMMA.INTEGRITY.IMMUTABLE_CHANGED", problems |> List.map code)
    Assert.NotEmpty(Verification.blocking problems)

    // An amount changed by hand breaks an invariant, so commands refuse to run on it.
    let amounts = original.Replace("\"minor\":605000}", "\"minor\":605100}")
    store.WriteExternally(ns.Location, file ns path, Some amounts)

    match run store ns RecordPayment "pay" (recordPayment ledgerContext payment) with
    | Error(Untrustworthy problems) -> Assert.Contains("SUMMA.INTEGRITY.INVARIANT_VIOLATED", problems |> List.map code)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``schema versions decide whether this Summa may read and write`` () =
    let manifest version =
        { Scope = ManifestScope.Dataset(DatasetId.create "org_acme" |> Support.ok)
          Application = Application.id
          StorageSchema = Manifest.StorageSchema
          ProviderContract = StorageContract.Version
          RecordSchemas = Map.ofList [ "summa.organization", Organization.schema.Current; "summa.entry", version ]
          CreatedBy = { Kind = ActorKind.Human; Id = ActorId.create "github:1" |> Support.ok }
          CreatedAt = at
          Location = (bindingOf production).Location
          Migration = None }

    let current = { Type = FinancialRecords.entryType; OldestReadable = 1; Current = 2 }
    let supported = [ Organization.schema; current ]
    Assert.Equal(Compatibility.ReadWrite, Compatibility.access supported acme (manifest 2))
    Assert.True(match Compatibility.access supported acme (manifest 1) with Compatibility.ReadOnly _ -> true | _ -> false)
    Assert.True(match Compatibility.access supported acme (manifest 3) with Compatibility.Refused _ -> true | _ -> false)
    Assert.True(match Compatibility.access supported { acme with StorageVersion = 2 } (manifest 2) with Compatibility.Refused _ -> true | _ -> false)

[<Fact>]
let ``commands refuse a folder written by a newer Summa`` () =
    let store, ns = populated ()
    let path = "arca-manifest.json"
    let newer = (contentOf store ns path).Replace("\"summa.organization\":2", "\"summa.organization\":9")
    store.WriteExternally(ns.Location, file ns path, Some newer)

    match run store ns RecordPayment "pay" (recordPayment ledgerContext payment) with
    | Error(Incompatible(Compatibility.Refused _)) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a migration must keep identities, balances and invariants`` () =
    let before = full ()
    let none = { Verification.Lineage = Map.empty; Verification.ChangesBalances = false }
    Assert.Empty(Verification.checkMigration none before before)

    // Renaming a customer id is safe only with its lineage.
    let customer = before.Books.Customers["CUST-ABC"]
    let renamed = { before with Books = { before.Books with Customers = before.Books.Customers.Remove("CUST-ABC").Add("CUST-0001", { customer with Id = "CUST-0001" }) } }
    Assert.Contains(MigrationUnsafe "record CUST-ABC has no successor", Verification.checkMigration none before renamed)
    Assert.Empty(Verification.checkMigration { none with Lineage = Map.ofList [ "CUST-ABC", "CUST-0001" ] } before renamed)

    // Dropping a reversal changes the trial balance: unsafe unless declared, and it loses a record either way.
    let ledger = before.Books.Ledger
    let dropped = { before with Books = { before.Books with Ledger = { ledger with Entries = ledger.Entries.Remove "JE-000004"; Journal = ledger.Journal |> List.filter ((<>) "JE-000004") } } }
    let problems = Verification.checkMigration none before dropped
    Assert.Contains(MigrationUnsafe "the trial balance changed, and the migration does not declare an accounting correction", problems)
    Assert.Contains(MigrationUnsafe "record JE-000004 has no successor", problems)
