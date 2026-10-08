/// Who may set an organization up and administer it first (Chrona WI-0053's
/// rule, applied to Summa).
///
/// The deployment's configuration is the root of trust: each organization
/// lists its bootstrap administrators by GitHub numeric account id. Only a
/// listed account initializes an organization or becomes its first
/// administrator; opening an organization first grants nothing. An
/// organization whose roster has no administrator who is also listed is held
/// until a listed account confirms itself. First-opener founding remains
/// only in an explicitly local environment that lists no one.
///
/// Pure.
module Summa.Storage.Governance

open Arca
open Summa.Access.Access
open Summa.Storage.Deployment

type Founding =
    /// The roster has a listed administrator: work as the roster says.
    | Proceed
    /// Set the organization up (when new) and make this person its first administrator.
    | Found
    /// No listed administrator in the roster. Nothing is granted; a listed account may confirm itself.
    | NeedsConfirmation of canConfirm: bool
    | Refused of reason: string

/// First-opener founding: only an explicitly local environment that lists no administrators.
let firstOpener (environment: EnvironmentKind) (organization: OrganizationConfig) =
    environment = EnvironmentKind.Local && organization.Administrators.IsEmpty

/// The roster's administrators who are also listed in the configuration.
let listedAdministrators (organization: OrganizationConfig) (roster: Roster) =
    roster.Members
    |> Map.toList
    |> List.filter (fun (id, membership) -> membership.Capabilities.Contains ManageUsers && isBootstrapAdministrator organization id)
    |> List.map fst

/// What opening the organization may do for `actorId`. `isNew`: its folder is not set up yet.
let decide (environment: EnvironmentKind) (organization: OrganizationConfig) (isNew: bool) (roster: Roster) (actorId: string) : Founding =
    let listed = isBootstrapAdministrator organization actorId

    match isNew with
    | true when listed || firstOpener environment organization -> Found
    | true when organization.Administrators.IsEmpty && environment = EnvironmentKind.Production ->
        Refused
            $"{organization.DisplayName} has no administrators in this deployment's configuration, so it cannot be set up. Production organizations are set up only by the GitHub accounts the configuration lists."
    | true when organization.Administrators.IsEmpty ->
        Refused $"{organization.DisplayName} has no administrators in this deployment's configuration, so it cannot be set up."
    | true -> Refused $"Only {organization.DisplayName}'s listed administrators can set it up."
    | false when firstOpener environment organization -> if roster.Members.IsEmpty then Found else Proceed
    | false when not (listedAdministrators organization roster).IsEmpty -> Proceed
    | false -> NeedsConfirmation listed

/// The membership a founding or confirmed administrator holds: everything a
/// person may hold, at the next revision of what was stored.
let administrator (principal: Principal) (roster: Roster) : Membership =
    { Principal = principal
      Capabilities = Grants.forKind principal.Kind Grants.administrator
      Revision =
        roster.Members.TryFind principal.PrincipalId
        |> Option.map (fun found -> found.Revision + 1)
        |> Option.defaultValue 1 }

/// Sets a new organization up for its founder: one commit creating Arca's
/// manifest, the organization manifest and the founder's membership. Only
/// when `decide` said `Found`.
let found
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: Storage.OperationContext)
    (manifest: Organization.OrganizationManifest)
    (founder: Principal)
    : Result<Operation, Diagnostics.Diagnostic list> =
    Storage.initializeOrganization config binding visibility overrideDecision context manifest
    |> Result.bind (fun initial ->
        let membership = administrator founder (empty manifest.OrganizationId)

        MemberRecord.path founder.PrincipalId
        |> Result.bind (fun p -> MemberRecord.encode membership |> Result.map (fun content -> Change.Create(p, content)))
        |> Result.mapError List.singleton
        |> Result.bind (fun change ->
            Storage.operation initial.Namespace context $"found organization {manifest.OrganizationId}" (initial.Changes @ [ change ])))
