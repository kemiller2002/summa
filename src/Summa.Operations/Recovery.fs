/// Restore verification, recovery objectives and the recovery drill
/// (SUM3-007, SUM3-008, SUM3-044): a backup counts only once it has been
/// restored and the restored books proved sound.
module Summa.Operations.Recovery

open System
open Arca
open Summa.Ledger.Reports
open Summa.Ledger.Payments
open Summa.Storage
open Summa.Storage.Diagnostics

/// The recovery targets, chosen deliberately (DF-SUMMA-2026-0004).
type Objectives =
    { /// Most data that may be lost: the age of the newest verified backup.
      RecoveryPoint: TimeSpan
      /// Longest acceptable time from loss to working books.
      RecoveryTime: TimeSpan }

/// RPO 24 hours, RTO 4 hours.
let objectives =
    { RecoveryPoint = TimeSpan.FromHours 24.0
      RecoveryTime = TimeSpan.FromHours 4.0 }

/// Whether the newest verified backup still meets the recovery point.
let recoveryPointMet (target: Objectives) (now: DateTimeOffset) (newestVerified: DateTimeOffset option) =
    newestVerified |> Option.exists (fun at -> now - at <= target.RecoveryPoint)

/// What a restore verification proved, or why the restored books cannot be trusted.
type Verified =
    { State: Receivables
      Entries: int
      Invoices: int
      Allocations: int
      AuditEvents: int
      TrialBalances: Map<string, TrialBalance> }

/// Verifies a restored organization folder (SUM3-007): it opens through its
/// manifests (schema valid), every record validates, every invariant and
/// reconciliation holds (ledger balances, invoices linked, payments
/// allocated), the audit trail exists, and the reports can be produced.
let verify (provider: StorageProvider) (ns: Namespace) (receivablesAccount: string) (asOf: DateOnly) : Async<Result<Verified, Diagnostic list>> =
    async {
        match! Commands.compatibility provider ns with
        | Error(Commands.Untrustworthy problems) -> return Error problems
        | Error other -> return Error [ StorageOperationRefused $"%A{other}" ]
        | Ok(Compatibility.Refused reasons) -> return Error reasons
        | Ok _ ->
            match! Verification.audit provider ns with
            | Error failure -> return Error [ StorageOperationRefused $"%A{failure}" ]
            | Ok problems when not (Verification.blocking problems).IsEmpty -> return Error(Verification.blocking problems)
            | Ok _ ->
                match! Commands.readAll provider ns with
                | Error failure -> return Error [ StorageOperationRefused $"%A{failure}" ]
                | Ok objects ->
                    let financial = objects |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type))
                    let loaded = FinancialRecords.load financial
                    let r = loaded.State
                    let findings = Reconciliation.run receivablesAccount asOf r

                    let problems =
                        loaded.Problems
                        @ (findings |> List.map (fun f -> InvariantViolated(f.Check, "reconciliation", f.Detail)))
                        @ (if r.Books.Ledger.Audit.IsEmpty && not r.Books.Ledger.Entries.IsEmpty then
                               [ InvariantViolated("audit-trail-exists", "audit", "the books have entries but no audit trail") ]
                           else
                               [])

                    if not problems.IsEmpty then
                        return Error problems
                    else
                        let currencies =
                            r.Books.Ledger.Entries
                            |> Map.toList
                            |> List.collect (fun (_, e) -> e.Lines |> List.map (fun l -> match l.Side with Summa.Ledger.Ledger.Debit m | Summa.Ledger.Ledger.Credit m -> m.Currency))
                            |> List.distinct

                        return
                            Ok
                                { State = r
                                  Entries = r.Books.Ledger.Entries.Count
                                  Invoices = r.Books.Invoices.Count
                                  Allocations = r.Allocations.Length
                                  AuditEvents = r.Books.Ledger.Audit.Length
                                  TrialBalances = currencies |> List.map (fun c -> c, trialBalance c asOf r.Books.Ledger) |> Map.ofList }
    }
