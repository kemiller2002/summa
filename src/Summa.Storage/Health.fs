/// System health apart from accounting health (SUM3-036) and safe start-up
/// (SUM3-037, SUM3-009): Summa starts only when its configuration is
/// complete, its storage answers, the data's schema is one it supports and
/// the books pass their integrity checks; otherwise it fails clearly.
module Summa.Storage.Health

open System
open Arca
open Summa.Storage.Diagnostics

type SystemHealth =
    { /// The provider answered with a change token.
      StorageAvailable: bool
      /// What the provider said, when it did not answer.
      StorageProblem: string option }

/// Whether storage answers for the namespace.
let system (provider: StorageProvider) (ns: Namespace) : Async<SystemHealth> =
    async {
        match! provider.ChangeToken ns with
        | Ok _ -> return { StorageAvailable = true; StorageProblem = None }
        | Error failure -> return { StorageAvailable = false; StorageProblem = Some $"%A{failure}" }
    }

type AccountingHealth =
    { Violations: Invariants.Violation list
      Findings: Reconciliation.Finding list }

let healthy (a: AccountingHealth) = a.Violations.IsEmpty && a.Findings.IsEmpty

/// Whether the books hold together: invariants (balanced ledger, no orphan
/// invoices, no invalid allocations, no broken references) and the
/// reconciliation jobs.
let accounting (receivablesAccount: string) (asOf: DateOnly) (books: Summa.Ledger.Payments.Receivables) : AccountingHealth =
    { Violations = Invariants.check books
      Findings = Reconciliation.run receivablesAccount asOf books }

/// Why Summa will not start, or that it may.
type Startup =
    | Ready of Compatibility.Access
    | Refused of Diagnostic list

/// The start-up decision for one organization: configuration, storage,
/// schema compatibility, then the integrity audit. Read-only access is
/// allowed to start (it shows data but takes no commands).
let start (config: Deployment.DeploymentConfig) (provider: StorageProvider) (organizationId: string) : Async<Startup> =
    async {
        let required =
            [ if config.Location.IsNone then MissingField "location"
              if config.Identity.IsNone && config.Environment <> EnvironmentKind.Local then MissingField "identity"
              if (Deployment.organization config organizationId).IsNone then UnknownOrganization organizationId ]

        if not required.IsEmpty then
            return Refused required
        else
            match Storage.binding config |> Result.bind (fun b -> Storage.organizationNamespace config b organizationId) with
            | Error problem -> return Refused [ problem ]
            | Ok ns ->
                let! health = system provider ns

                if not health.StorageAvailable then
                    return Refused [ StorageOperationRefused(health.StorageProblem |> Option.defaultValue "storage is unavailable") ]
                else
                    match! Commands.compatibility provider ns with
                    | Error(Commands.Untrustworthy problems) -> return Refused problems
                    | Error other -> return Refused [ StorageOperationRefused $"%A{other}" ]
                    | Ok(Compatibility.Refused reasons) -> return Refused reasons
                    | Ok access ->
                        match! Verification.audit provider ns with
                        | Error failure -> return Refused [ StorageOperationRefused $"%A{failure}" ]
                        | Ok problems ->
                            match Verification.blocking problems with
                            | [] -> return Ready access
                            | blocking -> return Refused blocking
    }
