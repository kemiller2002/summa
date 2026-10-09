/// Financial schema migrations and relocations (WI-0038, SUM0-032, SUM3-010,
/// SUM3-039, DF-SUMMA-2026-0016): an explicit, versioned migration of an
/// organization's folder to a new location through Arca's migration workflow
/// (validate, copy, verify, activate; the source is retired only when asked).
///
/// Before anything is written:
/// - the migration must state its rollback (SUM3-039);
/// - the books it reads must pass every check;
/// - the books it would produce are computed and checked against the books
///   it reads (`Verification.checkMigration`): no record lost without
///   lineage, the trial balance unchanged unless a correction is declared,
///   no invariant broken.
/// Then a sealed backup is taken, the books are reconciled, Arca copies and
/// verifies, and the copy is read back: it must be exactly the books that
/// were checked, and reconcile as before.
module Summa.Operations.FinancialMigration

open System
open Arca
open Summa.Ledger.Payments
open Summa.Storage
open Summa.Storage.Diagnostics

/// A migration, as its author wrote it.
[<NoComparison; NoEquality>]
type Definition =
    { /// Recorded in both manifests and every commit: `A-Z a-z 0-9 . _ : -`.
      Id: string
      /// What it does and why, in one line.
      Purpose: string
      /// How to undo it, stated before it runs (SUM3-039): for example, keep
      /// using the source, which is untouched until retired, and the backup.
      Rollback: string
      /// The record types it upgrades and the schema versions it writes.
      RecordSchemas: Map<string, int>
      /// What it does to each record. `Ok` unchanged for a relocation.
      Transform: Record -> Result<Record, string>
      /// What it declares about identities and balances.
      Intent: Verification.MigrationIntent }

/// A relocation: the same books at another location, nothing transformed
/// (ARCA-LOC-009).
let relocation (id: string) (purpose: string) (rollback: string) : Definition =
    { Id = id
      Purpose = purpose
      Rollback = rollback
      RecordSchemas = Map.empty
      Transform = Ok
      Intent = { Lineage = Map.empty; ChangesBalances = false } }

/// Why a migration did not run, or did not finish.
[<NoComparison; NoEquality>]
type Failure =
    /// No rollback was stated (SUM3-039).
    | NoRollback
    /// The books it reads fail their checks.
    | Untrustworthy of Diagnostic list
    /// The books it would produce are not the same books (lost records,
    /// changed balances, broken invariants), or a record cannot be
    /// transformed. Nothing was written.
    | Unsafe of Diagnostic list
    | BackupFailed of Backup.BackupError
    | Refused of MigrationError
    /// The copy, read back, is not the books that were checked.
    | CopyDiffers of string list
    | StorageFailed of StorageFailure

/// What a finished run did.
[<NoComparison; NoEquality>]
type Outcome =
    { Report: MigrationReport
      /// The backup taken before anything was written.
      Backup: Backup.Sealed
      /// Reconciliation of the books before, and of the copy after.
      Before: Reconciliation.Finding list
      After: Reconciliation.Finding list }

/// Arca's plan for a definition, from `source` to `target`.
let plan (definition: Definition) (source: Namespace) (target: Namespace) (actor: Actor) : Result<MigrationPlan, Failure> =
    match MigrationId.create definition.Id with
    | Error _ -> Error(Refused(MigrationError.InvalidPlan $"'{definition.Id}' is not a migration id"))
    | Ok id ->
        let plan =
            { Id = id
              Source = source
              Target = target
              RecordSchemas = definition.RecordSchemas
              Transform = definition.Transform
              Actor = actor
              BatchSize = 100 }

        Migration.validate plan |> Result.map (fun () -> plan) |> Result.mapError Refused

/// The stored objects the transform makes of `objects`, at the same paths:
/// what the target will hold. Pure.
let transformed (definition: Definition) (objects: StoredObject list) : Result<StoredObject list, Failure> =
    objects
    |> List.map (fun o ->
        match Record.decode Record.DefaultMaxBytes o.Content with
        | Error error -> Error(Unsafe [ InvalidStoredRecord(RelativePath.render o.Path, $"%A{error}") ])
        | Ok record ->
            match definition.Transform record with
            | Error why -> Error(Unsafe [ MigrationUnsafe $"{RelativePath.render o.Path}: {why}" ])
            | Ok changed ->
                match Record.encode Record.DefaultMaxBytes changed with
                | Error error -> Error(Unsafe [ InvalidStoredRecord(RelativePath.render o.Path, $"%A{error}") ])
                | Ok content -> Ok { o with Content = content })
    |> List.fold (fun acc item -> acc |> Result.bind (fun done' -> item |> Result.map (fun next -> done' @ [ next ]))) (Ok [])

let private financial (objects: StoredObject list) =
    objects
    |> List.filter (fun o ->
        match Layout.keyOf o.Path with
        | Some key -> FinancialRecords.isFinancial key.Type
        | None -> false)

/// The books the migration would produce from the books it reads, checked
/// before anything is written (SUM0-032, SUM3-010). Pure.
let check (definition: Definition) (objects: StoredObject list) : Result<Receivables * Receivables, Failure> =
    if String.IsNullOrWhiteSpace definition.Rollback then
        Error NoRollback
    else
        let before = FinancialRecords.load (financial objects)

        if not before.Problems.IsEmpty then
            Error(Untrustworthy before.Problems)
        else
            transformed definition (financial objects)
            |> Result.bind (fun after ->
                let loaded = FinancialRecords.load after

                match loaded.Problems @ Verification.checkMigration definition.Intent before.State loaded.State with
                | [] -> Ok(before.State, loaded.State)
                | problems -> Error(Unsafe problems))

/// What differs between two sets of books, by record, for the person.
let private differences (expected: Receivables) (found: Receivables) =
    match FinancialRecords.toRecords expected |> Result.bind FinancialRecords.contents, FinancialRecords.toRecords found |> Result.bind FinancialRecords.contents with
    | Ok wanted, Ok actual ->
        let paths = Set.union (wanted |> Map.keys |> Set.ofSeq) (actual |> Map.keys |> Set.ofSeq)

        paths
        |> Set.toList
        |> List.choose (fun path ->
            match wanted.TryFind path, actual.TryFind path with
            | Some(_, a), Some(_, b) when a = b -> None
            | Some _, Some _ -> Some $"{path} differs"
            | Some _, None -> Some $"{path} is missing"
            | None, Some _ -> Some $"{path} is extra"
            | None, None -> None)
    | Error problems, _
    | _, Error problems -> problems |> List.map describe

/// Runs a migration from `source` to `target`, up to activating the target.
/// The source is never changed; retiring it is `retire`, an explicit step.
let run
    (sourceProvider: StorageProvider)
    (targetProvider: StorageProvider)
    (source: Namespace)
    (target: Namespace)
    (actor: Actor)
    (key: Backup.BackupKey)
    (nonce: byte array)
    (receivablesAccount: string)
    (asOf: DateOnly)
    (definition: Definition)
    : Async<Result<Outcome, Failure>> =
    async {
        match plan definition source target actor with
        | Error failure -> return Error failure
        | Ok arcaPlan ->
            match! Commands.readAll sourceProvider source with
            | Error(Commands.StorageFailed failure) -> return Error(StorageFailed failure)
            | Error(Commands.Untrustworthy problems) -> return Error(Untrustworthy problems)
            | Error _ -> return Error(Untrustworthy [ StorageOperationRefused "the source could not be read" ])
            | Ok objects ->
                match check definition objects with
                | Error failure -> return Error failure
                | Ok(before, expected) ->
                    match! Backup.take sourceProvider source key nonce with
                    | Error error -> return Error(BackupFailed error)
                    | Ok backup ->
                        let reconciledBefore = Reconciliation.run receivablesAccount asOf before

                        match! Migration.run sourceProvider targetProvider arcaPlan with
                        | Error error -> return Error(Refused error)
                        | Ok report ->
                            match! Commands.readAll targetProvider target with
                            | Error _ -> return Error(CopyDiffers [ "the copy could not be read back" ])
                            | Ok copied ->
                                let found = FinancialRecords.load (financial copied)

                                match found.Problems, differences expected found.State with
                                | [], [] ->
                                    return
                                        Ok
                                            { Report = report
                                              Backup = backup
                                              Before = reconciledBefore
                                              After = Reconciliation.run receivablesAccount asOf found.State }
                                | problems, differing -> return Error(CopyDiffers((problems |> List.map describe) @ differing))
    }

/// Retires the source after the target was activated and checked: Arca
/// confirms the target still holds what the source implies, then marks the
/// source retired, so applications refuse it.
let retire (sourceProvider: StorageProvider) (targetProvider: StorageProvider) (arcaPlan: MigrationPlan) : Async<Result<unit, Failure>> =
    async {
        match! Migration.retire sourceProvider targetProvider arcaPlan with
        | Ok() -> return Ok()
        | Error error -> return Error(Refused error)
    }
