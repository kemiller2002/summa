/// The routine run an organization's deployment schedules (SUM3-006): one
/// run makes the recurring proposals that are due, then takes a sealed
/// backup and keeps it only once a restore of it has verified, then prunes
/// the kept backups under the retention policy, and reports whether the
/// recovery point is met (DF-SUMMA-2026-0004, DF-SUMMA-2026-0042).
///
/// The run acts as a scheduled process admitted to the organization's
/// roster: recorded as `automation`, never a person, and able to propose
/// only, never to issue (DF-SUMMA-2026-0017). The backup key belongs to the
/// operator's key store and reaches the run as a value; nothing here
/// creates, stores or logs one. Running it again on the same day makes no
/// second proposal (INV-REC-004).
module Summa.Operations.Routine

open System
open System.Globalization
open Arca
open Summa.Access
open Summa.Access.Access
open Summa.Ledger.Recurring
open Summa.Storage

/// Where sealed backups are kept: outside the organization's folder, in the
/// deployment's backup location.
[<NoEquality; NoComparison>]
type BackupStore =
    { Put: string -> Backup.Sealed -> Async<Result<unit, string>>
      /// Every kept backup: its id and when it was taken.
      List: unit -> Async<Result<(string * DateTimeOffset) list, string>>
      Delete: string -> Async<Result<unit, string>> }

/// What a deployment sets once for its routine runs.
type Settings =
    { /// The scheduled process's id in the organization's roster.
      ProcessId: string
      ReceivablesAccount: string
      Retention: Backup.RetentionPolicy
      Objectives: Recovery.Objectives
      /// How many times a command is tried when the folder changes under it.
      Attempts: int }

/// One run: when it runs, the organization's date, the run's id, the key
/// and 12 fresh random bytes for the backup's nonce.
type Run =
    { Now: DateTimeOffset
      /// Today in the organization's time zone (see `today`).
      Today: DateOnly
      RunId: string
      Key: Backup.BackupKey
      Nonce: byte array }

/// What each job of a run did.
type Job =
    /// The proposals made, and each template that could not propose with why.
    | Proposals of made: string list * refused: (string * RecurringProblem list) list
    | ProposalsFailed of reason: string
    /// The backup kept, with what its restore verified.
    | BackedUp of backupId: string * entries: int * invoices: int * auditEvents: int
    /// Not kept: it could not be taken, did not restore, did not verify or could not be stored.
    | BackupFailed of reason: string
    | Pruned of backupIds: string list
    | PruneFailed of reason: string

type Report =
    { Jobs: Job list
      /// Whether the newest kept backup is within the recovery point after this run.
      RecoveryPointMet: bool }

/// The routine defaults: the 14/8/12 retention, RPO 24 hours and RTO 4 hours, three tries.
let settings (processId: string) (receivablesAccount: string) =
    { ProcessId = processId
      ReceivablesAccount = receivablesAccount
      Retention = Backup.defaultRetention
      Objectives = Recovery.objectives
      Attempts = 3 }

/// Today's date in an organization's time zone (IANA id).
let today (timeZone: string) (now: DateTimeOffset) =
    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById timeZone).DateTime)

/// A backup's id: the organization and the instant it was taken, in UTC.
let backupId (organizationId: string) (at: DateTimeOffset) =
    organizationId + "-" + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)

let private organizationOf (ns: Namespace) =
    ns.Dataset |> Option.map DatasetId.value |> Option.defaultValue ""

let private proposals (settings: Settings) (provider: StorageProvider) (ns: Namespace) (run: Run) =
    async {
        let actor = Actor.scheduled settings.ProcessId run.RunId
        let context = Commands.contextFor actor run.Now "summa-routine" (Some run.RunId) None
        let generate r = let next, _, _ = generateAllDue context run.Today r in Ok next

        let request: Commands.Request<RecurringProblem list> =
            { Actor = actor
              Capability = ProposeInvoice
              Summary = $"routine {run.RunId}: recurring proposals due by {run.Today:``yyyy-MM-dd``}"
              IdempotencyKey = $"routine-{run.RunId}-proposals"
              Transition = generate }

        let! before = Commands.readAll provider ns

        match! Commands.execute provider defaultApprovalGates ns settings.Attempts request with
        | Error failure -> return ProposalsFailed $"%A{failure}"
        | Ok outcome ->
            let had =
                match before with
                | Ok objects ->
                    let financial = objects |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type))
                    (FinancialRecords.load financial).State.Books.Proposals
                | Error _ -> Map.empty

            let made = outcome.State.Books.Proposals |> Map.keys |> Seq.filter (had.ContainsKey >> not) |> Seq.sort |> List.ofSeq
            // What is still due after the run is what could not be proposed.
            let _, _, refused = generateAllDue context run.Today outcome.State
            return Proposals(made, refused)
    }

let private backup (settings: Settings) (provider: StorageProvider) (ns: Namespace) (store: BackupStore) (run: Run) =
    async {
        let actor = Actor.scheduled settings.ProcessId run.RunId
        let id = backupId (organizationOf ns) run.Now

        match! Backup.take provider ns run.Key run.Nonce with
        | Error error -> return BackupFailed $"not taken: %A{error}"
        | Ok sealedBackup ->
            // It counts only once a restore of the sealed text verifies.
            let restored = InMemoryStore()

            let operation =
                Backup.unseal run.Key sealedBackup
                |> Result.mapError (sprintf "%A")
                |> Result.bind (fun archive ->
                    Commands.metadata actor $"routine {run.RunId}: verify backup {id}" $"routine-{run.RunId}-verify"
                    |> Result.mapError (sprintf "%A")
                    |> Result.bind (fun meta -> Backup.restoreOperation ns meta archive))

            match operation with
            | Error reason -> return BackupFailed $"did not restore: {reason}"
            | Ok operation ->
                match! restored.Provider.Commit operation with
                | Error failure -> return BackupFailed $"did not restore: %A{failure}"
                | Ok _ ->
                    match! Recovery.verify restored.Provider ns settings.ReceivablesAccount run.Today with
                    | Error problems -> return BackupFailed $"did not verify: %A{problems}"
                    | Ok verified ->
                        match! store.Put id sealedBackup with
                        | Error reason -> return BackupFailed $"not stored: {reason}"
                        | Ok() -> return BackedUp(id, verified.Entries, verified.Invoices, verified.AuditEvents)
    }

let private prune (settings: Settings) (store: BackupStore) (run: Run) =
    async {
        match! store.List() with
        | Error reason -> return PruneFailed reason, None
        | Ok kept ->
            let keep = Backup.retain settings.Retention run.Now kept
            let expired = kept |> List.map fst |> List.filter (keep.Contains >> not) |> List.sort

            let! deleted =
                expired
                |> List.map (fun id -> async {
                    let! result = store.Delete id
                    return id, result })
                |> Async.Sequential

            let newest = kept |> List.filter (fst >> keep.Contains) |> List.map snd |> List.sortDescending |> List.tryHead

            match deleted |> Array.tryPick (fun (id, result) -> match result with Error reason -> Some $"{id}: {reason}" | Ok() -> None) with
            | Some reason -> return PruneFailed reason, newest
            | None -> return Pruned expired, newest
    }

/// One routine run for one organization: proposals, then the backup (so it
/// includes them), then retention. Every job runs and reports even when an
/// earlier one failed.
let run (settings: Settings) (provider: StorageProvider) (ns: Namespace) (store: BackupStore) (run: Run) : Async<Report> =
    async {
        let! proposed = proposals settings provider ns run
        let! backedUp = backup settings provider ns store run
        let! pruned, newest = prune settings store run

        return
            { Jobs = [ proposed; backedUp; pruned ]
              RecoveryPointMet = Recovery.recoveryPointMet settings.Objectives run.Now newest }
    }
