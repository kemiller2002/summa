/// Principals, organization rosters and capability-based authorization
/// (SUM0-022, SUM2-029, SUM3-013, SUM3-015).
///
/// Authentication says who someone is (GitHub, through Fides). This module
/// says what they may do in an organization, and it is separate: a valid
/// GitHub token grants nothing here (SUM0-003). Every check is "does this
/// principal hold this capability in this organization", never a role name;
/// roles are only templates that produce capability sets (`Grants`).
///
/// Pure.
module Summa.Access.Access

/// What kind of principal acts (SUM0-023, SUM3-012). An agent is never
/// recorded as a person.
type PrincipalKind =
    | Human
    | Agent
    | Service
    | Integration
    | ScheduledProcess

type Principal =
    { PrincipalId: string
      Kind: PrincipalKind
      DisplayName: string }

/// What a principal may do in an organization (SUM0-022, SUM2-029, SUM3-013).
type Capability =
    | ViewFinancials
    | CreateDraftInvoice
    | IssueInvoice
    | VoidInvoice
    | CreateCreditMemo
    | ApplyCreditMemo
    | RecordPayment
    | AllocatePayment
    | ReversePayment
    | RefundCustomer
    | WriteOffReceivable
    | PostJournalEntry
    | ReverseJournalEntry
    | PostManualAdjustment
    | ClosePeriod
    | ReopenPeriod
    | ExportData
    | ManageOrganization
    | ManageUsers
    | ManageSettings
    /// Take in approved time another application published (v0.1 §17).
    | ImportSourceTime
    | RecordExpense
    /// Turn sources into an invoice proposal for review (v0.2 §15-17).
    | ProposeInvoice
    /// Change a proposed rate, with a reason (INV-RATE-004).
    | OverrideRate
    /// Maintain rate cards and engagements: fees, milestones and terms.
    | ManageBilling
    /// Choose an invoice's number instead of the next one (INV-NUM-009).
    | OverrideInvoiceNumber

let allCapabilities =
    [ ViewFinancials
      CreateDraftInvoice
      IssueInvoice
      VoidInvoice
      CreateCreditMemo
      ApplyCreditMemo
      RecordPayment
      AllocatePayment
      ReversePayment
      RefundCustomer
      WriteOffReceivable
      PostJournalEntry
      ReverseJournalEntry
      PostManualAdjustment
      ClosePeriod
      ReopenPeriod
      ExportData
      ManageOrganization
      ManageUsers
      ManageSettings
      ImportSourceTime
      RecordExpense
      ProposeInvoice
      OverrideRate
      ManageBilling
      OverrideInvoiceNumber ]

/// The capability's stable name, as records and diagnostics state it.
let capabilityName (capability: Capability) = $"%A{capability}"

let capabilityOf (name: string) =
    allCapabilities |> List.tryFind (fun capability -> capabilityName capability = name)

let allKinds = [ Human; Agent; Service; Integration; ScheduledProcess ]

let kindName (kind: PrincipalKind) = $"%A{kind}"

let kindOf (name: string) = allKinds |> List.tryFind (fun kind -> kindName kind = name)

/// Managing who may do what is something only a person does: an agent,
/// service, integration or scheduled process never grants capabilities.
let personOnly = set [ ManageUsers; ManageOrganization ]

/// Whether a principal of this kind may hold the capability at all.
let permitsKind (kind: PrincipalKind) (capability: Capability) =
    kind = Human || not (personOnly.Contains capability)

/// Templates that produce capability sets: conveniences for granting, never
/// checked by name.
module Grants =
    let viewer = set [ ViewFinancials ]

    /// Day-to-day receivables: drafts, payments and allocation.
    let bookkeeper =
        viewer
        + set [ CreateDraftInvoice; RecordPayment; AllocatePayment; ExportData; ImportSourceTime; RecordExpense; ProposeInvoice ]

    /// Everything that changes the books.
    let accountant =
        bookkeeper
        + set
            [ IssueInvoice
              VoidInvoice
              CreateCreditMemo
              ApplyCreditMemo
              ReversePayment
              RefundCustomer
              WriteOffReceivable
              PostJournalEntry
              ReverseJournalEntry
              PostManualAdjustment
              ClosePeriod
              ReopenPeriod
              OverrideRate
              ManageBilling
              OverrideInvoiceNumber ]

    let administrator = Set.ofList allCapabilities

    /// What the template allows a principal of this kind.
    let forKind (kind: PrincipalKind) (grant: Set<Capability>) = grant |> Set.filter (permitsKind kind)

/// One principal's membership of one organization.
type Membership =
    { Principal: Principal
      Capabilities: Set<Capability>
      /// Optimistic-concurrency revision, starting at 1.
      Revision: int }

/// An organization's members, by principal id.
type Roster =
    { OrganizationId: string
      Members: Map<string, Membership> }

let empty (organizationId: string) =
    { OrganizationId = organizationId
      Members = Map.empty }

/// Why something is not allowed. Stable, so the interface and the audit can
/// name it.
type Refusal =
    | OrganizationMismatch of expected: string * found: string
    | NotAMember of principalId: string * organizationId: string
    | Unauthorized of principalId: string * capability: Capability
    | CapabilityNotForKind of capability: Capability * kind: PrincipalKind
    | AlreadyAMember of principalId: string
    | LastAdministrator of organizationId: string
    | Anonymous

let refusalCode =
    function
    | OrganizationMismatch _ -> "SUMMA.ACCESS.ORGANIZATION_MISMATCH"
    | NotAMember _ -> "SUMMA.ACCESS.NOT_A_MEMBER"
    | Unauthorized _ -> "SUMMA.ACCESS.UNAUTHORIZED"
    | CapabilityNotForKind _ -> "SUMMA.ACCESS.CAPABILITY_NOT_FOR_KIND"
    | AlreadyAMember _ -> "SUMMA.ACCESS.ALREADY_A_MEMBER"
    | LastAdministrator _ -> "SUMMA.ACCESS.LAST_ADMINISTRATOR"
    | Anonymous -> "SUMMA.ACCESS.ANONYMOUS"

/// What the principal may do in the roster's organization; empty for a non-member.
let capabilitiesOf (roster: Roster) (principalId: string) =
    roster.Members.TryFind principalId
    |> Option.map _.Capabilities
    |> Option.defaultValue Set.empty

/// Ok when the principal holds the capability in the organization; otherwise
/// the stable reason. The backend calls this for every command, whatever the
/// interface showed (SUM3-013).
let authorize (roster: Roster) (organizationId: string) (principalId: string) (capability: Capability) : Result<unit, Refusal> =
    if roster.OrganizationId <> organizationId then
        Error(OrganizationMismatch(organizationId, roster.OrganizationId))
    else
        match roster.Members.TryFind principalId with
        | None -> Error(NotAMember(principalId, organizationId))
        | Some membership when membership.Capabilities.Contains capability -> Ok()
        | Some _ -> Error(Unauthorized(principalId, capability))

let permits (roster: Roster) (principalId: string) (capability: Capability) =
    authorize roster roster.OrganizationId principalId capability |> Result.isOk

/// The organizations a principal belongs to.
let organizationsOf (rosters: Roster list) (principalId: string) =
    rosters |> List.filter (fun roster -> roster.Members.ContainsKey principalId) |> List.map _.OrganizationId

// ---- Approval gates (SUM3-015, INV-PROV-006) -------------------------------

/// Operations that need a person's approval when anyone else prepares them.
/// Configurable per organization; this is the default.
let defaultApprovalGates =
    set
        [ IssueInvoice
          OverrideInvoiceNumber
          WriteOffReceivable
          PostManualAdjustment
          ClosePeriod
          RefundCustomer
          // No autonomous financial surprise (INV-AGENT-007, SUM4-045): voiding,
          // applying credit, reversing a payment, and changing customer terms
          // or a contracted rate are prepared by an agent, never done by one.
          VoidInvoice
          ApplyCreditMemo
          ReversePayment
          ManageBilling
          OverrideRate ]

/// What a principal holding the capability may do with it.
type Decision =
    /// Execute now.
    | Allowed
    /// Prepare it; a person holding the same capability must approve it.
    | NeedsHumanApproval
    | Refused of Refusal

/// The decision for a command: authorization first, then the approval gate.
/// A person holding the capability executes; anyone else holding it may only
/// prepare a gated operation. Self-reported agent identity never counts as a
/// person's approval.
let decide (gates: Set<Capability>) (roster: Roster) (organizationId: string) (principalId: string) (capability: Capability) =
    match authorize roster organizationId principalId capability with
    | Error refusal -> Refused refusal
    | Ok() ->
        match roster.Members[principalId].Principal.Kind with
        | Human -> Allowed
        | _ when gates.Contains capability -> NeedsHumanApproval
        | _ -> Allowed

/// Whether `approver` may approve what `preparer` prepared: a different
/// person who holds the capability.
let canApprove (roster: Roster) (organizationId: string) (approver: string) (preparer: string) (capability: Capability) =
    match authorize roster organizationId approver capability with
    | Error refusal -> Error refusal
    | Ok() when roster.Members[approver].Principal.Kind <> Human ->
        Error(CapabilityNotForKind(capability, roster.Members[approver].Principal.Kind))
    | Ok() when approver = preparer -> Error(Unauthorized(approver, capability))
    | Ok() -> Ok()

// ---- Roster changes ---------------------------------------------------------

/// Changes to a roster. Each needs ManageUsers.
type RosterCommand =
    | Admit of Principal * Set<Capability>
    | Grant of principalId: string * Capability
    | Revoke of principalId: string * Capability
    | Remove of principalId: string

let private administrators (roster: Roster) =
    roster.Members |> Map.filter (fun _ m -> m.Capabilities.Contains ManageUsers) |> Map.count

let private kindProblems (principal: Principal) (capabilities: Set<Capability>) =
    capabilities
    |> Set.toList
    |> List.filter (permitsKind principal.Kind >> not)
    |> List.map (fun capability -> CapabilityNotForKind(capability, principal.Kind))

let private memberOf (roster: Roster) (principalId: string) =
    match roster.Members.TryFind principalId with
    | Some found -> Ok found
    | None -> Error [ NotAMember(principalId, roster.OrganizationId) ]

let private bump (membership: Membership) capabilities =
    { membership with
        Capabilities = capabilities
        Revision = membership.Revision + 1 }

/// Applies a roster change made by `performer`: the next roster, or every
/// reason it is refused. The organization always keeps someone who can
/// manage its users, and person-only capabilities go only to people.
let execute (performer: string) (command: RosterCommand) (roster: Roster) : Result<Roster, Refusal list> =
    let keepsAnAdministrator (next: Roster) =
        if administrators next = 0 then Error [ LastAdministrator roster.OrganizationId ] else Ok next

    authorize roster roster.OrganizationId performer ManageUsers
    |> Result.mapError List.singleton
    |> Result.bind (fun () ->
        match command with
        | Admit(principal, _) when roster.Members.ContainsKey principal.PrincipalId -> Error [ AlreadyAMember principal.PrincipalId ]
        | Admit(principal, capabilities) ->
            match kindProblems principal capabilities with
            | [] ->
                Ok
                    { roster with
                        Members =
                            roster.Members.Add(
                                principal.PrincipalId,
                                { Principal = principal
                                  Capabilities = capabilities
                                  Revision = 1 }
                            ) }
            | problems -> Error problems
        | Grant(principalId, capability) ->
            memberOf roster principalId
            |> Result.bind (fun found ->
                match kindProblems found.Principal (set [ capability ]) with
                | [] -> Ok { roster with Members = roster.Members.Add(principalId, bump found (found.Capabilities.Add capability)) }
                | problems -> Error problems)
        | Revoke(principalId, capability) ->
            memberOf roster principalId
            |> Result.map (fun found -> { roster with Members = roster.Members.Add(principalId, bump found (found.Capabilities.Remove capability)) })
            |> Result.bind keepsAnAdministrator
        | Remove principalId ->
            memberOf roster principalId
            |> Result.map (fun _ -> { roster with Members = roster.Members.Remove principalId })
            |> Result.bind keepsAnAdministrator)

/// Applies roster changes in order, all or none.
let executeAll (performer: string) (commands: RosterCommand list) (roster: Roster) =
    commands |> List.fold (fun state command -> state |> Result.bind (execute performer command)) (Ok roster)

/// The members whose records a roster change touched: changed or added
/// (to write) and removed (to delete), so one command is one commit of only
/// what it changed.
let changed (before: Roster) (after: Roster) =
    let written =
        after.Members
        |> Map.toList
        |> List.filter (fun (id, m) -> before.Members.TryFind id <> Some m)
        |> List.map snd

    let removed =
        before.Members |> Map.toList |> List.filter (fun (id, _) -> not (after.Members.ContainsKey id)) |> List.map fst

    written, removed
