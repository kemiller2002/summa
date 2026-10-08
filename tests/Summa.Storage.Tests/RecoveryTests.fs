/// The rebuildable index, reconciliation jobs, failure recovery, backup
/// backups, restore verification and the recovery drill (WI-0022; SUM0-015,
/// SUM3-002, SUM3-006..008, SUM3-020, SUM3-044).
module Summa.Storage.Tests.RecoveryTests

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
open Summa.Operations
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private kevin = Actor.person "583231" "kevin" "corr-1"

let private founded (store: InMemoryStore) =
    let binding = bindingOf production
    let ns = Storage.organizationNamespace production binding "org_acme" |> Support.ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    ns

let private run (provider: StorageProvider) ns capability key transition =
    execute provider defaultApprovalGates ns 3 { Actor = kevin; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

/// A folder with a chart, an issued invoice, a payment and its allocation.
let private books () =
    let store = InMemoryStore()
    let ns = founded store
    run store.Provider ns ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }) |> Support.ok |> ignore
    run store.Provider ns IssueInvoice "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b })) |> Support.ok |> ignore
    run store.Provider ns RecordPayment "pay" (recordPayment ledgerContext { payment with Amount = usd 300000L }) |> Support.ok |> ignore
    run store.Provider ns AllocatePayment "allocate" (allocate ledgerContext { allocation with Amount = usd 300000L }) |> Support.ok |> ignore
    store, ns

let private meta key = Commands.metadata kevin key $"op-{key}-0001" |> Support.ok

[<Fact>]
let ``the runtime index is rebuildable, detects staleness and never decides anything`` () =
    let store, ns = books ()
    let index, receipt = Derived.rebuild store.Provider ns (meta "index") Projection.definition |> Async.RunSynchronously |> Support.ok
    Assert.True receipt.IsSome
    // Rebuilding a current index writes nothing.
    Assert.Equal(None, Derived.rebuild store.Provider ns (meta "index-2") Projection.definition |> Async.RunSynchronously |> Support.ok |> snd)
    Assert.Equal(300000L, (Projection.balances index)[("cash", "USD")])
    Assert.Equal(305000L, (Projection.balances index)[("ar", "USD")])
    Assert.Equal(305000L, (Projection.outstanding index)["INV-001"])

    // A new record makes the stored index stale.
    run store.Provider ns RecordPayment "pay-2" (recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 5000L }) |> Support.ok |> ignore
    run store.Provider ns AllocatePayment "allocate-2" (allocate ledgerContext { allocation with AllocationId = "AL-2"; PaymentId = "PAY-2"; Amount = usd 5000L; JournalEntryId = "JE-000009" }) |> Support.ok |> ignore
    let snapshot = Snapshot.take store.Provider ns 3 |> Async.RunSynchronously |> Support.ok
    let current = Derived.sources Projection.definition snapshot |> Support.ok |> Derived.sourceSet
    let stored = Derived.read store.Provider ns Projection.definition |> Async.RunSynchronously |> Support.ok |> Option.map fst
    Assert.True(match Derived.check Projection.definition current stored with IndexStatus.Stale _ -> true | _ -> false)

    // A corrupt index is detected, commands are unaffected, and rebuilding repairs it.
    let indexPath = Derived.path Projection.definition |> Support.ok
    store.WriteExternally(ns.Location, RelativePath.render ns.Root + "/" + RelativePath.render indexPath, Some "{ not an index")
    Assert.True(Result.isError (Derived.read store.Provider ns Projection.definition |> Async.RunSynchronously))
    Assert.True(Result.isOk (run store.Provider ns RecordPayment "pay-3" (recordPayment ledgerContext { payment with Id = "PAY-3"; Amount = usd 100L })))
    let rebuilt, _ = Derived.rebuild store.Provider ns (meta "index-3") Projection.definition |> Async.RunSynchronously |> Support.ok
    Assert.True((Projection.outstanding rebuilt) |> Map.forall (fun _ v -> v >= 0L))
    Assert.Equal(300000L, (Projection.outstanding rebuilt)["INV-001"])

[<Fact>]
let ``reconciliation jobs agree on sound books and catch disagreement`` () =
    let r = full ()
    Assert.Empty(Reconciliation.run "ar" (DateOnly(2026, 12, 31)) r)
    // Receivables that no longer match the control account.
    let dropped = { r with Allocations = [] }
    let found = Reconciliation.run "ar" (DateOnly(2026, 12, 31)) dropped |> List.map _.Check
    Assert.Contains("receivables-control", found)
    let doubled = { r with Allocations = r.Allocations @ [ { r.Allocations.Head with Id = "AL-9" } ] }
    let found = Reconciliation.run "ar" (DateOnly(2026, 12, 31)) doubled |> List.map _.Check
    Assert.Contains("allocations-within-payments", found)
    Assert.Contains("invoice-balances", found)

[<Fact>]
let ``a failure mid-operation leaves the previous valid state and a retry has one effect`` () =
    let store, ns = books ()
    let before = store.State.Objects
    store.Arrange(InMemoryFault.RateLimited None)

    match run store.Provider ns RecordPayment "pay-2" (recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 100L }) with
    | Error(StorageFailed _) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal<Map<string, StoredObject>>(before, store.State.Objects)
    let once = run store.Provider ns RecordPayment "pay-2" (recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 100L }) |> Support.ok
    let again = run store.Provider ns RecordPayment "pay-2" (recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 100L }) |> Support.ok
    Assert.True once.Receipt.IsSome
    Assert.Equal(None, again.Receipt)
    Assert.Equal(2, again.State.Payments.Count)

let private key = Array.init 32 byte |> Backup.BackupKey.create |> Support.ok
let private nonce = Array.init 12 (fun i -> byte (100 + i))

[<Fact>]
let ``backups are backup, verified before use, and refuse a wrong key or tampering`` () =
    let store, ns = books ()
    let backup = Backup.take store.Provider ns key nonce |> Async.RunSynchronously |> Support.ok
    let text = Backup.Sealed.text backup
    Assert.StartsWith("summa-backup-v1.", text)
    Assert.DoesNotContain("CUST-ABC", text) // encrypted, not just encoded
    let archive = Backup.unseal key backup |> Support.ok
    Assert.Equal(Export.take store.Provider ns 3 |> Async.RunSynchronously |> Support.ok, archive)

    let other = Array.init 32 (fun i -> byte (255 - i)) |> Backup.BackupKey.create |> Support.ok
    Assert.Equal(Error Backup.AuthenticationFailed, Backup.unseal other backup)
    let flipped = text.Substring(0, text.Length - 4) + (if text.EndsWith "AAAA" then "BBBB" else "AAAA")
    Assert.True(Result.isError (Backup.unseal key (Backup.Sealed.ofText flipped)))
    Assert.Equal(Error Backup.NotABackup, Backup.unseal key (Backup.Sealed.ofText "not a backup"))
    Assert.Equal(Error Backup.KeyInvalid, Backup.BackupKey.create [| 1uy |] |> Result.map ignore)

[<Fact>]
let ``the recovery drill: back up, destroy, restore, verify and report`` () =
    let store, ns = books ()
    let backup = Backup.take store.Provider ns key nonce |> Async.RunSynchronously |> Support.ok
    let asOf = DateOnly(2026, 12, 31)
    let before = Recovery.verify store.Provider ns "ar" asOf |> Async.RunSynchronously |> Support.ok

    // Destroy: a new, empty environment at the same location.
    let restored = InMemoryStore()
    let archive = Backup.unseal key backup |> Support.ok
    let operation = Backup.restoreOperation ns (meta "restore") archive |> Support.ok
    restored.Provider.Commit operation |> Async.RunSynchronously |> Support.ok |> ignore

    // Start, check integrity, produce the reports.
    let after = Recovery.verify restored.Provider ns "ar" asOf |> Async.RunSynchronously |> Support.ok
    Assert.Equal(before.Entries, after.Entries)
    Assert.Equal(before.Invoices, after.Invoices)
    Assert.Equal(before.Allocations, after.Allocations)
    Assert.True(after.AuditEvents > 0)
    let tb = after.TrialBalances["USD"]
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)
    Assert.Equal<Summa.Ledger.Reports.TrialBalanceRow list>(before.TrialBalances["USD"].Rows, tb.Rows)
    Assert.Equal(PartiallyPaid, status after.State after.State.Books.Invoices["INV-001"])

    // And the restored folder takes commands again.
    Assert.True(Result.isOk (run restored.Provider ns RecordPayment "pay-after" (recordPayment ledgerContext { payment with Id = "PAY-9"; Amount = usd 1L })))

[<Fact>]
let ``a restore that does not verify is not a backup`` () =
    let store, ns = books ()
    let archive = Export.take store.Provider ns 3 |> Async.RunSynchronously |> Support.ok
    // An archive missing the payment record restores books that do not reconcile.
    let partial = { archive with Objects = archive.Objects |> List.filter (fun o -> not (o.Path.Contains "summa.payment")) }
    let restored = InMemoryStore()
    restored.Provider.Commit(Backup.restoreOperation ns (meta "restore") partial |> Support.ok) |> Async.RunSynchronously |> Support.ok |> ignore
    Assert.True(Result.isError (Recovery.verify restored.Provider ns "ar" (DateOnly(2026, 12, 31)) |> Async.RunSynchronously))

[<Fact>]
let ``recovery objectives and retention are explicit`` () =
    let now = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)
    Assert.Equal(TimeSpan.FromHours 24.0, Recovery.objectives.RecoveryPoint)
    Assert.Equal(TimeSpan.FromHours 4.0, Recovery.objectives.RecoveryTime)
    Assert.True(Recovery.recoveryPointMet Recovery.objectives now (Some(now.AddHours -23.0)))
    Assert.False(Recovery.recoveryPointMet Recovery.objectives now (Some(now.AddHours -25.0)))
    Assert.False(Recovery.recoveryPointMet Recovery.objectives now None)

    let daily = [ for d in 0..400 -> $"b{d}", now.AddDays(float -d) ]
    let kept = Backup.retain Backup.defaultRetention now daily
    Assert.True(kept.Contains "b0" && kept.Contains "b13")
    Assert.True(kept.Count < 14 + 9 + 13 + 1)
    Assert.False(kept.Contains "b400")
