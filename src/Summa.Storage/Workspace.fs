/// An organization's books on a store, for the signed-in person (WI-0037,
/// DF-SUMMA-2026-0014): opening them, setting a new organization up, a
/// listed administrator confirming themself, and what each person may do.
///
/// Provider-neutral: Arca's in-memory provider in tests, Arca's GitHub
/// provider in the application. The rules are the domain's (`Governance`,
/// `Compatibility`, `FinancialRecords`, `Commands`); this module only
/// sequences the reads and builds the commits.
module Summa.Storage.Workspace

open System
open Arca
open Summa.Access.Access
open Summa.Ledger.Payments
open Summa.Storage.Diagnostics
open Summa.Storage.Deployment

/// Books opened for a person.
[<NoComparison; NoEquality>]
type Opened =
    { Namespace: Namespace
      Manifest: Organization.OrganizationManifest
      Books: Receivables
      /// What this person may do here (their roster membership).
      Capabilities: Set<Capability>
      /// Read-write, or read-only with the reasons.
      Access: Compatibility.Access
      /// What was read, and the change token it was read at: the basis for
      /// deciding a command when the store cannot be reached.
      Read: ChangeToken option * StoredObject list }

/// What opening found.
[<NoComparison; NoEquality>]
type Opening =
    | Opened of Opened
    /// The organization's folder is not set up. `Found`: this person may
    /// set it up; `Refused`: why not.
    | NotSetUp of Governance.Founding
    /// No listed administrator in the roster: nothing is granted until a
    /// listed account confirms itself (`canConfirm`: this person may).
    | Held of canConfirm: bool
    /// The organization manifest is at an older schema; an administrator
    /// migrates it (`Migrations.migrate`) before anyone works.
    | NeedsMigration of mayMigrate: bool
    /// Signed in, but not on this organization's roster.
    | NotAMember
    /// The books cannot be used: integrity problems, or data this Summa
    /// does not read.
    | Unusable of Diagnostic list

/// The record schemas this Summa reads and writes in an organization's folder.
let supported = Organization.schema :: MemberRecord.schema :: FinancialRecords.schemas

let private isMember (o: StoredObject) =
    match Layout.keyOf o.Path with
    | Some key -> key.Type = MemberRecord.recordType
    | None -> false

let private read (provider: StorageProvider) (ns: Namespace) (path: Result<RelativePath, 'e>) =
    async {
        match path with
        | Error _ -> return Ok ReadOutcome.Absent
        | Ok path -> return! provider.Read ns path
    }

/// What the stored objects of a set-up folder allow this person.
let decide
    (environment: EnvironmentKind)
    (organization: OrganizationConfig)
    (ns: Namespace)
    (folder: StoredObject)
    (manifest: StoredObject)
    (objects: StoredObject list)
    (actorId: string)
    : Opening =
    match Storage.openNamespace ns (ReadOutcome.Found folder), Organization.decodeStored organization.Id manifest, MemberRecord.roster organization.Id (objects |> List.filter isMember) with
    | Error problems, _, _
    | _, _, Error problems -> Unusable problems
    | _, Error problem, _ -> Unusable [ problem ]
    | Ok arca, Ok stored, Ok(roster, _) ->
        match Governance.decide environment organization false roster actorId with
        | Governance.NeedsConfirmation canConfirm -> Held canConfirm
        | Governance.Found -> Held true
        | Governance.Refused reason -> Unusable [ InvalidOrganizationManifest reason ]
        | Governance.Proceed ->
            let capabilities = capabilitiesOf roster actorId

            if capabilities.IsEmpty then
                NotAMember
            else
                match Compatibility.storedAccess supported stored arca, stored with
                | Compatibility.Refused reasons, _ -> Unusable reasons
                | _, Organization.NeedsMigration _ -> NeedsMigration(capabilities.Contains ManageSettings)
                | access, Organization.CurrentManifest current ->
                    let loaded = FinancialRecords.load (objects |> List.filter (isMember >> not))

                    if loaded.Problems.IsEmpty then
                        Opened
                            { Namespace = ns
                              Manifest = current
                              Books = loaded.State
                              Capabilities = capabilities
                              Access = access
                              Read = None, objects }
                    else
                        Unusable loaded.Problems

/// Opens an organization's books for `actorId` (a `github:<id>` principal).
let openBooks
    (provider: StorageProvider)
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (organization: OrganizationConfig)
    (actorId: string)
    : Async<Result<Opening, Commands.CommandFailure<unit>>> =
    async {
        match Storage.organizationNamespace config binding organization.Id with
        | Error problem -> return Ok(Unusable [ problem ])
        | Ok ns ->
            let! folder = read provider ns Layout.manifestPath
            let! manifest = read provider ns (Organization.path organization.Id)

            match folder, manifest with
            | Error failure, _
            | _, Error failure -> return Error(Commands.StorageFailed failure)
            | Ok ReadOutcome.Absent, _ -> return Ok(NotSetUp(Governance.decide binding.Environment.Kind organization true (empty organization.Id) actorId))
            | Ok(ReadOutcome.Found _), Ok ReadOutcome.Absent -> return Ok(Unusable [ NamespaceNotInitialized(RelativePath.render ns.Root) ])
            | Ok(ReadOutcome.Found folder), Ok(ReadOutcome.Found manifest) ->
                match! provider.ChangeToken ns with
                | Error failure -> return Error(Commands.StorageFailed failure)
                | Ok token ->
                    match! Commands.readAll provider ns with
                    | Error failure -> return Error failure
                    | Ok objects ->
                        return
                            Ok(
                                match decide binding.Environment.Kind organization ns folder manifest objects actorId with
                                | Opened opened -> Opened { opened with Read = Some token, objects }
                                | other -> other
                            )
    }

/// The commits that set a new organization up for its founder, in order:
/// Summa's own namespace first when the repository has none yet, then the
/// organization's folder with the founder as its first administrator.
/// Refused for production data in a public repository (SUM3-040).
let foundingOperations
    (provider: StorageProvider)
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (context: Storage.OperationContext)
    (organization: OrganizationConfig)
    (founder: Principal)
    : Async<Result<Operation list, Commands.CommandFailure<unit>>> =
    async {
        match Storage.applicationNamespace binding with
        | Error problem -> return Error(Commands.Untrustworthy [ problem ])
        | Ok application ->
            let! existing = read provider application Layout.manifestPath

            match existing with
            | Error failure -> return Error(Commands.StorageFailed failure)
            | Ok found ->
                let applicationSetUp =
                    match found with
                    | ReadOutcome.Found _ -> Ok []
                    | ReadOutcome.Absent -> Storage.initializeApplication binding visibility None context |> Result.map List.singleton

                let manifest = Storage.manifestFor organization context.At

                return
                    applicationSetUp
                    |> Result.bind (fun first ->
                        Governance.found config binding visibility None context manifest founder
                        |> Result.map (fun organizationSetUp -> first @ [ organizationSetUp ]))
                    |> Result.mapError Commands.Unstorable
    }

/// The commit by which a listed administrator confirms themself in an
/// organization whose roster has no listed administrator (Governance).
let confirmation
    (ns: Namespace)
    (context: Storage.OperationContext)
    (organization: OrganizationConfig)
    (objects: StoredObject list)
    (principal: Principal)
    : Result<Operation, Diagnostic list> =
    if not (isBootstrapAdministrator organization principal.PrincipalId) then
        Error [ InvalidOrganizationManifest $"only {organization.DisplayName}'s listed administrators can confirm themselves" ]
    else
        MemberRecord.roster organization.Id (objects |> List.filter isMember)
        |> Result.bind (fun (roster, revisions) ->
            let membership = Governance.administrator principal roster
            let after = { roster with Members = roster.Members.Add(principal.PrincipalId, membership) }

            MemberRecord.changes revisions roster after
            |> Result.bind (fun changes ->
                Storage.operation ns context $"confirm {principal.PrincipalId} as administrator of {organization.Id}" changes))

/// Summa's application manifest, read on start-up (SUM0-007): Summa's
/// namespace must be Summa's, at a storage version this Summa reads, and
/// must not need a newer Summa than this one. A repository with no
/// namespace yet is fine: setting the books up creates it.
let checkApplication (provider: StorageProvider) (binding: ApplicationBinding) : Async<Result<unit, Commands.CommandFailure<unit>>> =
    async {
        match Storage.applicationNamespace binding with
        | Error problem -> return Error(Commands.Untrustworthy [ problem ])
        | Ok application ->
            match! provider.Read application Application.path with
            | Error failure -> return Error(Commands.StorageFailed failure)
            | Ok ReadOutcome.Absent -> return Ok()
            | Ok(ReadOutcome.Found stored) ->
                match Application.decode stored with
                | Error problem -> return Error(Commands.Untrustworthy [ problem ])
                | Ok manifest ->
                    match Version.TryParse manifest.MinimumApplicationVersion, Version.TryParse Application.ApplicationVersion with
                    | (true, needed), (true, running) when needed > running ->
                        return
                            Error(
                                Commands.Untrustworthy
                                    [ NamespaceUnusable(RelativePath.render application.Root, $"these books need Summa {manifest.MinimumApplicationVersion} or newer; this is {Application.ApplicationVersion}") ]
                            )
                    | _ -> return Ok()
    }
