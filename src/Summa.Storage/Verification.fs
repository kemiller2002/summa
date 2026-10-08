/// Integrity before availability (SUM0-029..032, SUM0-047, SUM3-009,
/// SUM3-010): what Summa proves about stored data before it acts on it.
///
/// - **Outside edits.** Git history is evidence, never domain state: a
///   financial record whose latest commit Arca did not write was edited
///   outside Summa and is validated like everything else, never trusted for
///   it; an immutable record touched by more than one commit was changed
///   after it was posted, which is an integrity failure.
/// - **Schema compatibility.** An organization's Arca manifest declares the
///   schema version of every record type; this Summa reads and writes only
///   what it supports, opens older data read-only, and refuses newer data.
/// - **Migrations.** A migration of financial data must keep every
///   record's identity (or name its lineage), keep the trial balance unless
///   it declares a correction, and leave books that satisfy every invariant.
module Summa.Storage.Verification

open System
open Arca
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Reports
open Summa.Ledger.Payments
open Summa.Storage.Diagnostics

// ---- Outside edits ------------------------------------------------------------

/// What the history of one financial record shows.
let judgeHistory (path: string) (immutable: bool) (history: HistoryEntry list) : Diagnostic list =
    [ match history with
      | newest :: _ when newest.Origin = CommitOrigin.External -> EditedOutsideSumma path
      | _ -> ()
      if immutable && history.Length > 1 then ImmutableRecordChanged path ]

let private isImmutable (path: RelativePath) =
    match Layout.keyOf path with
    | Some key ->
        FinancialRecords.types
        |> List.exists (fun (t, m, _) -> t = key.Type && m = Mutability.Immutable)
    | None -> false

/// Audits an organization's financial records: each record's history, then
/// the records themselves (schema, identity, references and invariants).
/// Meant for start-up and reconciliation, not for every command: it asks
/// the provider for each record's history.
let audit (provider: StorageProvider) (ns: Namespace) : Async<Result<Diagnostic list, StorageFailure>> =
    async {
        match! Commands.readAll provider ns with
        | Error(Commands.StorageFailed failure) -> return Error failure
        | Error(Commands.Untrustworthy problems) -> return Ok problems
        | Error _ -> return Ok [ StorageOperationRefused "the folder could not be read" ]
        | Ok objects ->
            let financial =
                objects
                |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type))

            let! histories =
                financial
                |> List.map (fun o ->
                    async {
                        let! history = provider.History ns o.Path
                        return history |> Result.map (judgeHistory (RelativePath.render o.Path) (isImmutable o.Path))
                    })
                |> Async.Sequential

            match histories |> Array.tryPick (function Error f -> Some f | Ok _ -> None) with
            | Some failure -> return Error failure
            | None ->
                let fromHistory = histories |> Array.toList |> List.collect (function Ok d -> d | Error _ -> [])
                return Ok(fromHistory @ (FinancialRecords.load financial).Problems)
    }

/// Whether a command may run after an audit: outside edits that pass every
/// check are allowed (they are reported, not trusted for anything else);
/// anything else stops financial commands.
let blocking (problems: Diagnostic list) =
    problems
    |> List.filter (function
        | EditedOutsideSumma _ -> false
        | _ -> true)

// ---- Migrations ---------------------------------------------------------------

/// What a migration of financial data declares about itself.
type MigrationIntent =
    { /// Old id -> new id, for records the migration renames. Every other id must survive.
      Lineage: Map<string, string>
      /// True only for a migration that is an explicit accounting correction.
      ChangesBalances: bool }

let private ids (r: Receivables) =
    [ yield! r.Books.Ledger.Entries |> Map.keys
      yield! r.Books.Invoices |> Map.keys
      yield! r.Books.Obligations |> Map.keys
      yield! r.Payments |> Map.keys
      yield! r.Allocations |> List.map _.Id
      yield! r.Books.Ledger.Accounts |> Map.keys
      yield! r.Books.Customers |> Map.keys ]
    |> Set.ofSeq

let private currencies (r: Receivables) =
    r.Books.Ledger.Entries
    |> Map.toList
    |> List.collect (fun (_, e) ->
        e.Lines
        |> List.map (fun l ->
            match l.Side with
            | Debit m
            | Credit m -> m.Currency))
    |> List.distinct

let private balances (r: Receivables) =
    let latest =
        r.Books.Ledger.Entries |> Map.toList |> List.map (fun (_, e) -> e.Date) |> List.fold max DateOnly.MinValue

    currencies r
    |> List.map (fun currency ->
        let tb = trialBalance currency latest r.Books.Ledger
        currency, tb.Rows |> List.map (fun row -> row.Code, row.Debit, row.Credit))
    |> Map.ofList

/// Every reason a migration from `before` to `after` is unsafe (SUM0-032,
/// SUM3-010): a record identity lost without lineage, a trial balance that
/// changed without a declared correction, or broken invariants afterwards.
let checkMigration (intent: MigrationIntent) (before: Receivables) (after: Receivables) : Diagnostic list =
    let expected = ids before |> Set.map (fun id -> intent.Lineage.TryFind id |> Option.defaultValue id)
    let lost = Set.difference expected (ids after)

    [ for id in lost do
          yield MigrationUnsafe $"record {id} has no successor"
      if not intent.ChangesBalances && balances before <> balances after then
          yield MigrationUnsafe "the trial balance changed, and the migration does not declare an accounting correction"
      for v in Invariants.check after do
          yield MigrationUnsafe $"after the migration, {v.Rule} fails for {v.Subject}: {v.Detail}" ]
