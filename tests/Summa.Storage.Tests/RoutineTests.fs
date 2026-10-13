/// The routine run a deployment schedules (WI-0029 slice 21, SUM3-006):
/// due recurring proposals as a scheduled process, a sealed backup kept only
/// once its restore verifies, retention, and the recovery point.
module Summa.Storage.Tests.RoutineTests

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
let private processId = "summa-routine-acme"

let private execute (provider: StorageProvider) ns (actor: Actor.Actor) capability key transition =
    Commands.execute provider defaultApprovalGates ns 3 { Actor = actor; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

/// A monthly retainer on the 15th from 1 October 2026.
let private retainer: RecurringTemplate =
    { Id = "RET-ABC"
      Version = 1
      CustomerId = abc.Id
      EngagementId = None
      Currency = "USD"
      Schedule = Monthly 15
      Lines = draft.Lines
      Terms = Some(Net 15)
      Recipients = None
      StartsOn = DateOnly(2026, 10, 1)
      EndsOn = None
      Occurrences = None
      Active = true }

let private admit (store: InMemoryStore) ns (principal: Principal) capabilities =
    let listing = store.Provider.List ns MemberRecord.folder |> Async.RunSynchronously |> Support.ok
    let members = listing.Entries |> List.map (fun e -> match store.Provider.Read ns e.Path |> Async.RunSynchronously |> Support.ok with ReadOutcome.Found s -> s | _ -> failwith "absent")
    let roster, revisions = MemberRecord.roster "org_acme" members |> Support.ok

    Access.execute "github:583231" (Admit(principal, capabilities)) roster
    |> Result.map (fun next ->
        let changes = MemberRecord.changes revisions roster next |> Support.ok
        store.Provider.Commit(Storage.operation ns (context "admit") "admit" changes |> Support.ok) |> Async.RunSynchronously |> Support.ok |> ignore)

let private scheduledProcess = { PrincipalId = processId; Kind = ScheduledProcess; DisplayName = "routine run" }

/// Books with an issued invoice, a payment, its allocation and the retainer;
/// `admitted` says whether the scheduled process is on the roster.
let private books (admitted: bool) =
    let store = InMemoryStore()
    let binding = bindingOf production
    let ns = Storage.organizationNamespace production binding "org_acme" |> Support.ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    execute store.Provider ns kevin ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }) |> Support.ok |> ignore
    execute store.Provider ns kevin IssueInvoice "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b })) |> Support.ok |> ignore
    execute store.Provider ns kevin RecordPayment "pay" (recordPayment ledgerContext { payment with Amount = usd 300000L }) |> Support.ok |> ignore
    execute store.Provider ns kevin AllocatePayment "allocate" (allocate ledgerContext { allocation with Amount = usd 300000L }) |> Support.ok |> ignore
    execute store.Provider ns kevin ManageBilling "template" (fun r -> Summa.Ledger.Recurring.saveTemplate ledgerContext retainer r.Books |> Result.map (fun b -> { r with Books = b })) |> Support.ok |> ignore

    if admitted then
        admit store ns scheduledProcess (Grants.forKind ScheduledProcess (set [ ViewFinancials; ProposeInvoice ])) |> Support.ok

    store, ns

/// A backup location in memory; `failPut` makes every store attempt fail.
let private location (failPut: bool) (seed: (string * DateTimeOffset) list) =
    let kept = Collections.Generic.Dictionary<string, DateTimeOffset * string>()
    seed |> List.iter (fun (id, at) -> kept[id] <- (at, "older"))

    let store: Routine.BackupStore =
        { Put =
            fun id sealedBackup ->
                async {
                    if failPut then
                        return Error "backup location unavailable"
                    else
                        kept[id] <- (DateTimeOffset.ParseExact(id.Substring(id.Length - 16), "yyyyMMdd'T'HHmmss'Z'", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal), Backup.Sealed.text sealedBackup)
                        return Ok()
                }
          List = fun () -> async { return Ok [ for pair in kept -> pair.Key, fst pair.Value ] }
          Delete = fun id -> async { return (if kept.Remove id then Ok() else Error "absent") } }

    store, kept

let private key = Array.init 32 byte |> Backup.BackupKey.create |> Support.ok
let private now = DateTimeOffset(2026, 12, 20, 6, 0, 0, TimeSpan.Zero)

let private runOf (runId: string) (at: DateTimeOffset) : Routine.Run =
    { Now = at
      Today = Routine.today "America/New_York" at
      RunId = runId
      Key = key }

let private settings = Routine.settings processId "ar"

let private routine provider ns backups run =
    Routine.run settings provider ns backups run |> Async.RunSynchronously

[<Fact>]
let ``SUM3-006: a routine run proposes what is due as a scheduled process, then keeps a backup whose restore verified`` () =
    let store, ns = books true
    let backups, kept = location false []
    let report = routine store.Provider ns backups (runOf "run-20261220" now)

    match report.Jobs with
    | [ Routine.Proposals(made, refused); Routine.BackedUp(id, entries, invoices, auditEvents); Routine.Pruned [] ] ->
        Assert.Equal<string list>([ "RET-ABC-20261015"; "RET-ABC-20261115"; "RET-ABC-20261215" ], made)
        Assert.Empty refused
        Assert.Equal("org_acme-20261220T060000Z", id)
        Assert.True(entries > 0 && invoices = 1 && auditEvents > 0)
        // The kept text is a sealed backup that opens under the key, and it holds the proposals.
        let archive = Backup.unseal key (Backup.Sealed.ofText (snd kept[id])) |> Support.ok
        Assert.Contains(archive.Objects, fun o -> o.Path.Contains "RET-ABC-20261215")
    | other -> failwith $"%A{other}"

    Assert.True report.RecoveryPointMet

    // The proposals are the scheduled process's, recorded as automation, never a person.
    let state = Commands.readAll store.Provider ns |> Async.RunSynchronously |> Support.ok
    let financial = state |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type))
    let r = (FinancialRecords.load financial).State
    let proposal = r.Books.Proposals["RET-ABC-20261215"]
    Assert.Equal(Proposed, proposal.State)
    let generated = proposal.Contributions |> List.find (fun c -> c.What.StartsWith "generated from recurring template")
    Assert.Equal(processId, generated.Who)
    Assert.Equal(Some "automation", generated.Provenance |> Option.map _.ActorKind)
    Assert.Equal(Some "run-20261220", generated.Provenance |> Option.bind _.ExecutionId)
    Assert.True(r.Books.Drafts.IsEmpty)

[<Fact>]
let ``SUM3-006: running again the same day, or after a restart, makes no second proposal`` () =
    let store, ns = books true
    let backups, _ = location false []
    routine store.Provider ns backups (runOf "run-a" now) |> ignore
    let again = routine store.Provider ns backups (runOf "run-b" (now.AddHours 1.0))

    match again.Jobs with
    | Routine.Proposals([], []) :: _ -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``SUM3-006: a process not on the roster proposes nothing, the backup still runs, and the process can never issue`` () =
    let store, ns = books false
    let backups, _ = location false []
    let report = routine store.Provider ns backups (runOf "run-x" now)

    match report.Jobs with
    | [ Routine.ProposalsFailed reason; Routine.BackedUp _; _ ] -> Assert.Contains("NotAMember", reason)
    | other -> failwith $"%A{other}"

    // Admitted, it may propose but not issue, and it is never given the roster.
    let actor = Actor.scheduled processId "run-y"
    admit store ns scheduledProcess (Grants.forKind ScheduledProcess (set [ ViewFinancials; ProposeInvoice ])) |> Support.ok

    match execute store.Provider ns actor IssueInvoice "issue-by-routine" (fun r -> Ok r) with
    | Error(NotAuthorized _) -> ()
    | other -> failwith $"%A{other}"

    Assert.True(Result.isError (admit store ns { scheduledProcess with PrincipalId = "summa-routine-other" } (set [ ManageUsers ])))

[<Fact>]
let ``SUM3-006: a backup that cannot be stored is not counted, and old backups are pruned under retention`` () =
    let store, ns = books true
    let failing, _ = location true []
    let report = routine store.Provider ns failing (runOf "run-f" now)

    match report.Jobs with
    | [ Routine.Proposals _; Routine.BackupFailed reason; Routine.Pruned [] ] -> Assert.StartsWith("not stored", reason)
    | other -> failwith $"%A{other}"

    Assert.False report.RecoveryPointMet

    // A year of daily backups: after the run, retention keeps 14 days, 8 weeks and 12 months.
    let year = [ for d in 1..400 -> $"org_acme-old-{d}", now.AddDays(float -d) ]
    let backups, kept = location false year
    let report = routine store.Provider ns backups (runOf "run-r" now)

    match report.Jobs with
    | [ _; Routine.BackedUp(id, _, _, _); Routine.Pruned pruned ] ->
        Assert.True(kept.ContainsKey id)
        Assert.Contains("org_acme-old-400", pruned)
        Assert.DoesNotContain("org_acme-old-13", pruned)
        Assert.True((Set.ofList pruned = Set.ofList [ for (id, _) in year do if not (kept.ContainsKey id) then id ]))
    | other -> failwith $"%A{other}"

    Assert.True report.RecoveryPointMet

[<Fact>]
let ``SUM3-006: the run's date is the organization's, not the server's`` () =
    let lateUtc = DateTimeOffset(2026, 12, 15, 2, 0, 0, TimeSpan.Zero)
    Assert.Equal(DateOnly(2026, 12, 14), Routine.today "America/New_York" lateUtc)
    Assert.Equal(DateOnly(2026, 12, 15), Routine.today "Europe/Berlin" lateUtc)
    Assert.Equal("org_acme-20261215T020000Z", Routine.backupId "org_acme" lateUtc)

/// The nonce a sealed backup carries (the second dot-separated field).
let private nonceOf (sealedText: string) = sealedText.Split('.')[1]

[<Fact>]
let ``SUM3-006: two routine runs seal their backups under different nonces the run drew itself`` () =
    let store, ns = books true
    let backups, kept = location false []
    routine store.Provider ns backups (runOf "run-n1" now) |> ignore
    routine store.Provider ns backups (runOf "run-n2" (now.AddMinutes 1.0)) |> ignore
    let nonces = [ for pair in kept -> nonceOf (snd pair.Value) ]
    Assert.Equal(2, nonces.Length)
    Assert.Equal(2, nonces |> List.distinct |> List.length)
    // Both still open under the one key.
    for pair in kept do
        Assert.True(Result.isOk (Backup.unseal key (Backup.Sealed.ofText (snd pair.Value))))

[<Fact>]
let ``SUM3-024: the production nonce source is the secure generator, never a constant`` () =
    let drawn = [ for _ in 1..64 -> Backup.freshNonce () ]
    Assert.All(drawn, fun n -> Assert.Equal(12, n.Length))
    Assert.Equal(64, drawn |> List.map Convert.ToBase64String |> List.distinct |> List.length)
    Assert.DoesNotContain(drawn, fun n -> n |> Array.forall ((=) 0uy))
    // Two backups of unchanged books differ, because each draws its own nonce.
    let store, ns = books false
    let first = Backup.take store.Provider ns key |> Async.RunSynchronously |> Support.ok |> Backup.Sealed.text
    let second = Backup.take store.Provider ns key |> Async.RunSynchronously |> Support.ok |> Backup.Sealed.text
    Assert.NotEqual<string>(nonceOf first, nonceOf second)
    // A source that does not give 12 bytes seals nothing.
    let archive = Export.take store.Provider ns 3 |> Async.RunSynchronously |> Support.ok
    Assert.Equal(Error Backup.KeyInvalid, Backup.sealWith (fun () -> [| 1uy |]) key archive |> Result.map ignore)
