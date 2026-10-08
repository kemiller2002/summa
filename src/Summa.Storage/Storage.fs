/// Where Summa keeps its data (SUM-DATALOC-001..005, SUM0-005..011,
/// SUM0-033, SUM0-043): the deployment's configured location, the namespace
/// Summa owns inside it, one folder per organization, initializing and
/// opening those folders, and the refusal to initialize production data in a
/// public repository.
///
/// Pure. Nothing here names a repository, owner, branch or base path. This
/// module builds Arca operations and reads Arca objects; an Arca provider (in
/// memory for tests, GitHub in the application) carries them out. Every path
/// resolves through Arca's namespace rules, so no write can leave Summa's
/// folder (SUM0-043).
module Summa.Storage.Storage

open System
open Arca
open Summa.Storage.Diagnostics
open Summa.Storage.Deployment

let private locationOf (config: LocationConfig) =
    Deployment.dataLocation config |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// Summa's storage binding for a deployment, or why its configuration is refused.
let binding (config: DeploymentConfig) : Result<ApplicationBinding, Diagnostic> =
    match config.Location with
    | None -> Error(MissingField "location")
    | Some configured ->
        locationOf configured
        |> Result.map (fun location ->
            { Application = Application.id
              Environment =
                { Kind = config.Environment
                  Name = config.EnvironmentName }
              Location = location })

/// Summa's own namespace, `<base path>/summa` (SUM-DATALOC-002, SUM0-006).
let applicationNamespace (binding: ApplicationBinding) : Result<Namespace, Diagnostic> =
    Namespace.ofApplication binding |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// One organization's folder, `<base path>/summa/datasets/<OrganizationId>`,
/// in the organization's own repository when the deployment configures one
/// (SUM0-008, SUM0-009). Named by the immutable id, so renaming never moves
/// data (SUM0-010). Only a configured organization has a folder (SUM0-033).
let organizationNamespace (config: DeploymentConfig) (binding: ApplicationBinding) (organizationId: string) : Result<Namespace, Diagnostic> =
    match Deployment.organization config organizationId with
    | None -> Error(UnknownOrganization organizationId)
    | Some organization ->
        Organization.dataset organizationId
        |> Result.bind (fun dataset ->
            match organization.Location with
            | None -> Ok None
            | Some location -> locationOf location |> Result.map Some
            |> Result.bind (fun location ->
                Namespace.ofDataset binding dataset location |> Result.mapError (LocationError.describe >> InvalidDataLocation)))

/// Every namespace the deployment configures, application first, then each
/// organization in configuration order: deterministic discovery from
/// configuration, never by scanning the repository (SUM0-033).
let namespaces (config: DeploymentConfig) (binding: ApplicationBinding) : Result<Namespace list, Diagnostic> =
    let organizations =
        config.Organizations
        |> List.map (fun organization -> organizationNamespace config binding organization.Id)

    List.foldBack
        (fun item state -> state |> Result.bind (fun rest -> item |> Result.map (fun ns -> ns :: rest)))
        (applicationNamespace binding :: organizations)
        (Ok [])

/// Whether data may be initialized in a repository of this visibility
/// (SUM3-040, ARCA-LOC-008): production in a public repository is refused
/// unless someone decided otherwise and recorded why.
let permitsInitialization (environment: EnvironmentKind) (visibility: RepositoryVisibility) (overrideDecision: PublicProductionOverride option) =
    Visibility.permitsInitialization environment visibility overrideDecision
    |> Result.mapError (function
        | VisibilityRefusal.PublicProductionRepository -> PublicProductionRepository
        | VisibilityRefusal.OverrideWithoutReason -> OverrideWithoutReason)

let private place (location: DataLocation) =
    $"{location.Repository} ({BranchName.value location.Branch}, '{RelativePath.render location.BasePath}')"

let private describeProblem =
    function
    | ManifestProblem.WrongApplication(found, expected) -> $"the folder belongs to '{found}', not '{expected}'"
    | ManifestProblem.WrongScope -> "the folder's manifest describes a different scope"
    | ManifestProblem.UnsupportedStorageSchema(found, supported) -> $"storage schema {found} is not {supported}"
    | ManifestProblem.UnsupportedProviderContract(found, supported) -> $"provider contract {found} is newer than {supported}"
    | ManifestProblem.Relocated relocation ->
        $"the data lives at {place relocation.Recorded}, not the configured {place relocation.Configured}; moving it is a migration"
    | ManifestProblem.MigrationInProgress state -> $"migration {state.MigrationId} is in progress"
    | ManifestProblem.Retired migrationId -> $"the data was moved by migration {migrationId} and this copy retired"

/// A namespace's Arca manifest, checked against the namespace Summa is
/// configured for before any record in it is read or written (SUM0-007,
/// SUM0-033). No manifest means not initialized; a manifest for another
/// location means a migration, never a silent re-point.
let openNamespace (ns: Namespace) (stored: ReadOutcome) : Result<Manifest, Diagnostic list> =
    let root = RelativePath.render ns.Root

    match stored with
    | ReadOutcome.Absent -> Error [ NamespaceNotInitialized root ]
    | ReadOutcome.Found found ->
        Manifest.decode found.Content
        |> Result.mapError (fun error -> [ InvalidStoredRecord(RelativePath.render found.Path, $"%A{error}") ])
        |> Result.bind (fun manifest ->
            match Manifest.check ns manifest with
            | [] -> Ok manifest
            | problems -> Error(problems |> List.map (fun problem -> NamespaceUnusable(root, describeProblem problem))))

/// Who is changing storage, and the operation's identifiers. Supplied by the
/// caller: this module reads no clock and draws no randomness.
type OperationContext =
    { Actor: Actor
      ProviderIdentity: string option
      CorrelationId: CorrelationId
      IdempotencyKey: IdempotencyKey
      At: DateTimeOffset }

/// One Arca operation in `ns`, which becomes one commit carrying the
/// context's actor, correlation and idempotency key; or every reason Arca
/// refuses it.
let operation (ns: Namespace) (context: OperationContext) (summary: string) (changes: Change list) : Result<Operation, Diagnostic list> =
    let metadata: OperationMetadata =
        { Summary = summary
          Actor = context.Actor
          ProviderIdentity = context.ProviderIdentity
          ExecutionId = None
          CorrelationId = context.CorrelationId
          IdempotencyKey = context.IdempotencyKey }

    Operation.create ns metadata changes
    |> Result.mapError (fun error ->
        [ StorageOperationRefused(
              match error with
              | OperationError.NoChanges -> "nothing to write"
              | OperationError.DuplicatePath path -> $"'{path}' is written twice"
              | OperationError.InvalidPath error -> LocationError.describe error
              | OperationError.InvalidSummary _ -> "the summary is not one short line"
              | OperationError.CredentialInContent field -> $"'{field}' looks like a credential"
              | OperationError.InvalidMetadata field -> $"'{field}' is not a single-line identifier"
          ) ])

/// The record schemas Summa writes, as Arca's manifest lists them.
let private recordSchemas (supports: SchemaSupport list) =
    supports |> List.map (fun s -> RecordType.value s.Type, s.Current) |> Map.ofList

let private arcaManifest (ns: Namespace) (context: OperationContext) (supports: SchemaSupport list) =
    { Scope =
        match ns.Dataset with
        | Some dataset -> ManifestScope.Dataset dataset
        | None -> ManifestScope.Application
      Application = ns.Application
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas = recordSchemas supports
      CreatedBy = context.Actor
      CreatedAt = context.At
      Location = ns.Location
      Migration = None }

let private manifestPath () =
    Layout.manifestPath |> Result.mapError (LocationError.describe >> InvalidDataLocation >> List.singleton)

/// The record types an organization folder holds at this storage version.
let organizationSchemas = [ Organization.schema ]

/// Initializes Summa's namespace: one commit creating Arca's manifest and
/// Summa's application manifest. Nothing is overwritten: an initialized
/// folder makes the commit a conflict.
let initializeApplication
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    : Result<Operation, Diagnostic list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> applicationNamespace binding |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        manifestPath ()
        |> Result.bind (fun arcaPath ->
            let schemas = Application.schema :: organizationSchemas

            Application.encode (Application.current (recordSchemas schemas) context.At)
            |> Result.mapError List.singleton
            |> Result.bind (fun application ->
                operation
                    ns
                    context
                    "initialize Summa storage"
                    [ Change.Create(arcaPath, Manifest.encode (arcaManifest ns context [ Application.schema ]))
                      Change.Create(Application.path, application) ])))

/// Initializes an organization's folder: one commit creating Arca's manifest
/// and the organization manifest. `visibility` is that of the repository the
/// organization's data goes to, which may be its own. Nothing is overwritten.
let initializeOrganization
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    (manifest: Organization.OrganizationManifest)
    : Result<Operation, Diagnostic list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> organizationNamespace config binding manifest.OrganizationId |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        Organization.encode manifest
        |> Result.bind (fun content ->
            manifestPath ()
            |> Result.bind (fun arcaPath ->
                Organization.path manifest.OrganizationId
                |> Result.mapError List.singleton
                |> Result.bind (fun path ->
                    operation
                        ns
                        context
                        $"initialize organization {manifest.OrganizationId}"
                        [ Change.Create(arcaPath, Manifest.encode (arcaManifest ns context organizationSchemas))
                          Change.Create(path, content) ]))))

/// The manifest an organization's configuration produces on first use.
let manifestFor (organization: OrganizationConfig) (at: DateTimeOffset) =
    Organization.create organization.Id organization.DisplayName organization.Slug organization.DefaultCurrency organization.TimeZone at

/// Changes an organization's manifest (its names, currency or settings, never
/// its id) as one commit. `revision` is the manifest's revision as last read:
/// a manifest changed since is a conflict the caller reloads and decides.
let updateOrganization
    (ns: Namespace)
    (context: OperationContext)
    (revision: Revision)
    (previous: Organization.OrganizationManifest)
    (next: Organization.OrganizationManifest)
    : Result<Operation, Diagnostic list> =
    if next.OrganizationId <> previous.OrganizationId || next.CreatedAt <> previous.CreatedAt then
        Error [ InvalidOrganizationManifest "an organization's id and creation time never change" ]
    else
        Organization.encode next
        |> Result.bind (fun content ->
            Organization.path next.OrganizationId
            |> Result.mapError List.singleton
            |> Result.bind (fun path ->
                operation ns context $"update organization {next.OrganizationId}" [ Change.Update(path, content, revision) ]))
