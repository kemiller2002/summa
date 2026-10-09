/// One financial command, one commit (SUM0-016..021, SUM3-013, Chrona's
/// command-to-commit pattern).
///
/// A command runs against what the organization's folder holds now: read
/// everything, check the actor and authorize the capability against the
/// stored roster, load and verify the books, run the domain transition, and
/// write only the records it changed, as one Arca operation that also
/// requires the change token read at the start. If anything moved meanwhile
/// (a conflict or a stale token) the command is decided again from a fresh
/// read: a clean Git merge is never proof that the financial meaning still
/// holds (SUM0-021). An unknown outcome is reconciled before anything is
/// sent again.
module Summa.Storage.Commands

open System
open Arca
open Summa.Ledger.Payments
open Summa.Access
open Summa.Access.Access
open Summa.Storage.Diagnostics
open Summa.Storage.FinancialRecords

/// The capability issuing an invoice needs: choosing the number instead
/// of the next one is its own capability (INV-NUM-009).
let issueCapability (request: Summa.Ledger.Invoicing.IssueRequest) =
    if request.NumberOverride.IsSome then OverrideInvoiceNumber else IssueInvoice

/// Why a command did not commit.
type CommandFailure<'e> =
    /// The actor cannot carry a command (SUM3-012, SUM0-004).
    | InvalidActor of Actor.ActorProblem list
    | NotAuthorized of Refusal
    /// The actor may prepare it; a person must approve it (SUM3-015).
    | NeedsApproval of Capability
    /// The stored books have integrity problems; nothing runs on them.
    | Untrustworthy of Diagnostic list
    /// The organization's data is older or newer than this Summa writes (SUM0-031).
    | Incompatible of Compatibility.Access
    /// The domain refused the command.
    | Rejected of 'e
    /// The change cannot be stored (for example it would rewrite a posted record).
    | Unstorable of Diagnostic list
    /// Others kept changing the folder; the command was decided this many times.
    | StillConflicted of attempts: int
    | Unknown of PendingReconciliation
    | StorageFailed of StorageFailure

/// A command: who, which capability it needs, its one-line summary, its
/// idempotency key, and the pure transition.
[<NoEquality; NoComparison>]
type Request<'e> =
    { Actor: Actor.Actor
      Capability: Capability
      Summary: string
      IdempotencyKey: string
      Transition: Receivables -> Result<Receivables, 'e> }

type Outcome =
    { State: Receivables
      /// None when the command changed nothing (for example a retry).
      Receipt: CommitReceipt option
      Attempts: int }

/// The changes that take the stored objects to the wanted records. Mutable
/// records are updated under the revision read; immutable ones are never
/// rewritten; only deletable types are deleted; nothing else is touched.
let changes (seen: Map<string, Seen>) (wanted: Map<string, RecordKey * string>) : Result<Change list, Diagnostic list> =
    let parse (p: string) =
        RelativePath.parse p |> Result.mapError (LocationError.describe >> InvalidDataLocation)

    let writes =
        wanted
        |> Map.toList
        |> List.choose (fun (p, (key, content)) ->
            match seen.TryFind p with
            | None -> Some(parse p |> Result.map (fun path -> Change.Create(path, content)))
            | Some found when found.Content = content -> None
            | Some _ when not (List.exists (fun (t, m, _) -> t = key.Type && m = Mutability.Mutable) types) ->
                Some(Error(InvalidStoredRecord(p, "a posted record cannot be rewritten; correct it with a new record")))
            | Some found -> Some(parse p |> Result.map (fun path -> Change.Update(path, content, found.Revision))))

    let deletes =
        seen
        |> Map.toList
        |> List.filter (fun (p, _) -> not (wanted.ContainsKey p))
        |> List.map (fun (p, found) ->
            parse p
            |> Result.bind (fun path ->
                match Layout.keyOf path with
                | Some key when deletable key.Type -> Ok(Change.Delete(path, found.Revision))
                | _ -> Error(InvalidStoredRecord(p, "an authoritative financial record is never deleted"))))

    let all = writes @ deletes

    match all |> List.choose (function Error d -> Some d | Ok _ -> None) with
    | [] -> Ok(all |> List.choose Result.toOption)
    | problems -> Error problems

/// The folders a command reads: every financial record type and the members.
let folders: RelativePath list =
    (types |> List.map (fun (t, _, _) -> Layout.RecordsFolder + "/" + RecordType.value t))
    @ [ RelativePath.render MemberRecord.folder ]
    |> List.choose (RelativePath.parse >> Result.toOption)

/// Runs the steps in order, one after another, on the caller's thread: no
/// thread pool, so a single-threaded host (the browser) sees each step
/// finish within the message that started it. The first failure stops it.
let private inOrder (steps: Async<Result<'a list, 'e>> list) : Async<Result<'a list, 'e>> =
    let rec go (remaining: Async<Result<'a list, 'e>> list) (acc: 'a list) =
        async {
            match remaining with
            | [] -> return Ok acc
            | step :: rest ->
                match! step with
                | Error failure -> return Error failure
                | Ok found -> return! go rest (acc @ found)
        }

    go steps []

/// Every object under the folders, recursively. A listing the provider cut
/// short is a failure: what was not read was not verified.
let readAll (provider: StorageProvider) (ns: Namespace) : Async<Result<StoredObject list, CommandFailure<'e>>> =
    let rec walk (folder: RelativePath) : Async<Result<StoredObject list, CommandFailure<'e>>> =
        async {
            match! provider.List ns folder with
            | Error failure -> return Error(StorageFailed failure)
            | Ok listing when not listing.Complete -> return Error(Untrustworthy [ InvalidStoredRecord(RelativePath.render folder, "the listing was incomplete") ])
            | Ok listing ->
                return!
                    listing.Entries
                    |> List.map (fun entry ->
                        if entry.IsFolder then
                            walk entry.Path
                        else
                            async {
                                match! provider.Read ns entry.Path with
                                | Ok(ReadOutcome.Found stored) -> return Ok [ stored ]
                                | Ok ReadOutcome.Absent -> return Ok []
                                | Error failure -> return Error(StorageFailed failure)
                            })
                    |> inOrder
        }

    folders |> List.map walk |> inOrder

let private arcaKind =
    function
    | Human -> ActorKind.Human
    | Agent -> ActorKind.Agent
    | Service
    | ScheduledProcess -> ActorKind.Service
    | Integration -> ActorKind.Integration

/// The ledger context a command's transition runs under, with where it came
/// from (INV-AUD-002, INV-PROV-001, INV-PROV-002): the actor's Praxis kind
/// (`human`, `agent`, or `automation` for services, integrations and
/// scheduled processes; never `human` for anything but a person), the
/// agent's identity, the execution id as supplied, and the source system.
let contextFor (actor: Actor.Actor) (at: DateTimeOffset) (source: string) (sourceId: string option) (reason: string option) : Summa.Ledger.Ledger.Context =
    { Who = actor.ActorId
      When = at
      Source = source
      CorrelationId = Some actor.CorrelationId
      Provenance =
        Some
            { ActorKind =
                match actor.Kind with
                | Human -> "human"
                | Agent -> "agent"
                | Service
                | Integration
                | ScheduledProcess -> "automation"
              Agent =
                actor.Agent
                |> Option.map (fun a ->
                    { Provider = a.Provider
                      Model = a.Model
                      Runtime = a.Runtime })
              ExecutionId = actor.ExecutionId
              SourceSystem = actor.SourceSystem |> Option.orElse (Some source)
              SourceId = sourceId
              Reason = reason } }

/// The Arca operation metadata of a command: the actor, its execution and
/// correlation, and the idempotency key, never a token.
/// The authorized agent interface (INV-AGENT-001): a request in words,
/// from an agent the organization's roster lets propose invoices, becomes a
/// proposal and nothing more. It needs `ProposeInvoice` only, so it never
/// issues, voids, applies, writes off, refunds or changes a rate
/// (INV-AGENT-007); what it cannot settle comes back as questions
/// (INV-AGENT-003). Retrying the same request with the same proposal id
/// changes nothing.
let agentRequest
    (actor: Actor.Actor)
    (at: DateTimeOffset)
    (accounts: Summa.Ledger.Billing.BillingAccounts)
    (proposalId: string)
    (request: string)
    : Request<Summa.Ledger.AgentRequests.Question list> =
    { Actor = actor
      Capability = ProposeInvoice
      Summary = $"Propose {proposalId} from a request in words"
      IdempotencyKey = $"agent-request-{proposalId}"
      Transition = Summa.Ledger.AgentRequests.propose (contextFor actor at "summa-agent-interface" None None) accounts proposalId request }

let metadata (actor: Actor.Actor) (summary: string) (key: string) : Result<OperationMetadata, Diagnostic list> =
    match ActorId.create actor.ActorId, CorrelationId.create actor.CorrelationId, IdempotencyKey.create key with
    | Ok id, Ok correlation, Ok idempotency ->
        Ok
            { Summary = summary
              Actor = { Kind = arcaKind actor.Kind; Id = id }
              ProviderIdentity = actor.GitHubLogin
              ExecutionId = actor.ExecutionId
              CorrelationId = correlation
              IdempotencyKey = idempotency }
    | id, correlation, idempotency ->
        Error
            [ match id with
              | Error text -> StorageOperationRefused $"the actor id '{text}' is not a storable identifier"
              | Ok _ -> ()
              match correlation with
              | Error text -> StorageOperationRefused $"the correlation id '{text}' is not a storable identifier"
              | Ok _ -> ()
              match idempotency with
              | Error text -> StorageOperationRefused $"the idempotency key '{text}' is not 8 to 128 identifier characters"
              | Ok _ -> () ]

let private isMember (o: StoredObject) =
    match Layout.keyOf o.Path with
    | Some key -> key.Type = MemberRecord.recordType
    | None -> false

/// Decides a command against what was read: the operation to commit, or
/// nothing to do, or why not. Pure.
let decide
    (gates: Set<Capability>)
    (ns: Namespace)
    (token: ChangeToken)
    (objects: StoredObject list)
    (request: Request<'e>)
    : Result<Receivables * Operation option, CommandFailure<'e>> =
    let organizationId = ns.Dataset |> Option.map DatasetId.value |> Option.defaultValue ""

    match Actor.problems request.Actor with
    | _ :: _ as problems -> Error(InvalidActor problems)
    | [] ->
        match MemberRecord.roster organizationId (objects |> List.filter isMember) with
        | Error problems -> Error(Untrustworthy problems)
        | Ok(roster, _) ->
            match Access.decide gates roster organizationId request.Actor.ActorId request.Capability with
            | Refused refusal -> Error(NotAuthorized refusal)
            | NeedsHumanApproval -> Error(NeedsApproval request.Capability)
            | Allowed ->
                let loaded = load (objects |> List.filter (isMember >> not))

                if not loaded.Problems.IsEmpty then
                    Error(Untrustworthy loaded.Problems)
                else
                    request.Transition loaded.State
                    |> Result.mapError Rejected
                    |> Result.bind (fun next ->
                        toRecords next
                        |> Result.bind contents
                        |> Result.bind (changes loaded.Seen)
                        |> Result.mapError Unstorable
                        |> Result.bind (function
                            | [] -> Ok(next, None)
                            | found ->
                                metadata request.Actor request.Summary request.IdempotencyKey
                                |> Result.bind (fun meta ->
                                    Operation.create ns meta found
                                    |> Result.mapError (fun error -> [ StorageOperationRefused $"%A{error}" ]))
                                |> Result.mapError Unstorable
                                |> Result.map (fun operation -> next, Some(Operation.requireChangeToken token operation))))

/// How this Summa may use the organization's folder, from its Arca manifest
/// and organization manifest; a folder without them is not usable.
let compatibility (provider: StorageProvider) (ns: Namespace) : Async<Result<Compatibility.Access, CommandFailure<'e>>> =
    let organizationId = ns.Dataset |> Option.map DatasetId.value |> Option.defaultValue ""

    async {
        match Layout.manifestPath, Organization.path organizationId with
        | Ok arcaPath, Ok organizationPath ->
            let! arca = provider.Read ns arcaPath
            let! organization = provider.Read ns organizationPath

            match arca, organization with
            | Error failure, _
            | _, Error failure -> return Error(StorageFailed failure)
            | Ok arcaFound, Ok(ReadOutcome.Found organizationFound) ->
                match Storage.openNamespace ns arcaFound, Organization.decodeStored organizationId organizationFound with
                | Ok manifest, Ok organization ->
                    return Ok(Compatibility.storedAccess (Organization.schema :: MemberRecord.schema :: FinancialRecords.schemas) organization manifest)
                | Error problems, _ -> return Error(Untrustworthy problems)
                | _, Error problem -> return Error(Untrustworthy [ problem ])
            | Ok _, Ok ReadOutcome.Absent -> return Error(Untrustworthy [ NamespaceNotInitialized(RelativePath.render ns.Root) ])
        | _ -> return Error(Untrustworthy [ InvalidOrganizationId organizationId ])
    }

/// Runs a command through a provider: check the folder's schema
/// compatibility, then read, decide and commit; on a conflict or a stale
/// change token read again and decide again, up to `attempts` times.
let execute
    (provider: StorageProvider)
    (gates: Set<Capability>)
    (ns: Namespace)
    (attempts: int)
    (request: Request<'e>)
    : Async<Result<Outcome, CommandFailure<'e>>> =
    let rec attempt (n: int) =
        async {
            match! provider.ChangeToken ns with
            | Error failure -> return Error(StorageFailed failure)
            | Ok token ->
                match! readAll provider ns with
                | Error failure -> return Error failure
                | Ok objects ->
                    match decide gates ns token objects request with
                    | Error failure -> return Error failure
                    | Ok(state, None) -> return Ok { State = state; Receipt = None; Attempts = n }
                    | Ok(state, Some operation) ->
                        let retry () =
                            if n < attempts then attempt (n + 1) else async.Return(Error(StillConflicted n))

                        match! provider.Commit operation with
                        | Ok receipt -> return Ok { State = state; Receipt = Some receipt; Attempts = n }
                        | Error(StorageFailure.Conflicted _)
                        | Error(StorageFailure.StaleChangeToken _) -> return! retry ()
                        | Error(StorageFailure.OutcomeUnknown pending) ->
                            match! provider.Reconcile ns pending with
                            | Ok(ReconcileOutcome.Landed receipt) -> return Ok { State = state; Receipt = Some receipt; Attempts = n }
                            | Ok ReconcileOutcome.NotLanded -> return! retry ()
                            | Ok(ReconcileOutcome.StillUnknown still) -> return Error(Unknown still)
                            | Error failure -> return Error(StorageFailed failure)
                        | Error failure -> return Error(StorageFailed failure)
        }

    async {
        match! compatibility provider ns with
        | Error failure -> return Error failure
        | Ok Compatibility.ReadWrite -> return! attempt 1
        | Ok other -> return Error(Incompatible other)
    }
