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

/// What kind of failure a command met (INV-FAIL-004).
type FailureClass =
    /// It may pass if tried again later, unchanged.
    | Retryable
    /// The command itself is refused: trying it again changes nothing.
    | PermanentOrValidation
    /// The actor may not, or a person must approve it first.
    | Authorization
    /// Someone else changed the same books first.
    | ConcurrencyConflict
    /// GitHub (or another dependency) could not be reached or did not answer.
    | DependencyUnavailable
    /// The stored books cannot be trusted as they are.
    | IntegrityViolation

/// What a person is told about a failed command beside why (INV-FAIL-002):
/// its class, what remains as it was, whether trying again is safe, and
/// what to do next.
type FailureReport =
    { Class: FailureClass
      Unchanged: string
      RetrySafe: bool
      Next: string }

let private nothingWritten = "Nothing was saved; the books are as they were."

/// The report for a command's failure. Every failure leaves the books as
/// they were, except an unknown outcome: GitHub may have saved the change,
/// so it is reconciled before anything is sent again, never retried blindly.
let classify (failure: CommandFailure<'e>) : FailureReport =
    let report cls safe next = { Class = cls; Unchanged = nothingWritten; RetrySafe = safe; Next = next }

    match failure with
    | InvalidActor _ -> report Authorization true "Sign in again."
    | NotAuthorized _ -> report Authorization true "Ask an administrator for the capability, or ask someone who has it."
    | NeedsApproval _ -> report Authorization true "Ask a person who may approve it."
    | Untrustworthy _ -> report IntegrityViolation true "Check the books and correct the records named; nothing can be changed until they pass."
    | Incompatible _ -> report PermanentOrValidation true "Use the Summa version these books were written by, or have an administrator migrate them."
    | Rejected _ -> report PermanentOrValidation true "Correct what is named, then try again."
    | Unstorable _ -> report PermanentOrValidation true "Correct what is named; a posted record is corrected with a new record."
    | StillConflicted _ -> report ConcurrencyConflict true "Try again: it is decided on the books as they then stand."
    | Unknown _ ->
        { Class = DependencyUnavailable
          Unchanged = "GitHub did not say whether the change was saved; nothing else changed."
          RetrySafe = false
          Next = "Reload. Summa finds out whether it was saved before anything is sent again." }
    | StorageFailed failure ->
        match failure with
        | StorageFailure.Conflicted _
        | StorageFailure.StaleChangeToken _
        | StorageFailure.StaleNamespaceToken _ -> report ConcurrencyConflict true "Try again: it is decided on the books as they then stand."
        | StorageFailure.OutcomeUnknown _ ->
            { Class = DependencyUnavailable
              Unchanged = "GitHub did not say whether the change was saved; nothing else changed."
              RetrySafe = false
              Next = "Reload. Summa finds out whether it was saved before anything is sent again." }
        | StorageFailure.Refused _ -> report Authorization true "Ask for write access to the data repository, or to its branch."
        | StorageFailure.RateLimited _ -> report Retryable true "Wait a minute, then try again."
        | StorageFailure.ProviderFailed(_, true, _) -> report Retryable true "Try again shortly."
        | StorageFailure.ProviderFailed(_, false, _) -> report DependencyUnavailable true "Try again later; if it keeps failing, tell your administrator."
        | StorageFailure.ObjectTooLarge _ -> report PermanentOrValidation true "Make the record smaller; a document goes to artifact storage, not the books."
        | StorageFailure.WrongLocation _ -> report IntegrityViolation true "Tell your administrator: the deployment points at the wrong place."
        | StorageFailure.IntegrityRefused _ -> report IntegrityViolation true "Check the books; the record named was refused as it is."

/// The report as sentences, to follow the reason (INV-FAIL-002).
let explain (report: FailureReport) =
    let retry = if report.RetrySafe then "Trying again is safe." else "Do not try again until it is settled."
    $"{report.Unchanged} {retry} {report.Next}"

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
            // What is written is scanned first: never a card, an account number or a credential.
            let sensitive = Sensitive.scan content |> List.tryHead

            match seen.TryFind p, sensitive with
            | Some found, _ when found.Content = content -> None
            | _, Some finding -> Some(Error(SensitiveData(p, Sensitive.describe finding)))
            | None, None -> Some(parse p |> Result.map (fun path -> Change.Create(path, content)))
            | Some _, None when not (List.exists (fun (t, m, _) -> t = key.Type && m = Mutability.Mutable) types) ->
                Some(Error(InvalidStoredRecord(p, "a posted record cannot be rewritten; correct it with a new record")))
            | Some found, None -> Some(parse p |> Result.map (fun path -> Change.Update(path, content, found.Revision))))

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

/// Where a fork-join resumes the computation that waits on it. F#'s
/// `Async.FromContinuations` hands a continuation called from inside a
/// running async to the thread pool, which in the browser runs only after
/// the page has replied, so the work would wait for the next message. A
/// host with one thread defers the resumption to its own turn instead,
/// outside any running async: the page's bridge runs it right after the
/// answer that finished the last step. Elsewhere it resumes at once.
[<NoEquality; NoComparison>]
type Turn = { Defer: (unit -> unit) -> unit }

module Turn =
    let now: Turn = { Defer = fun resume -> resume () }

/// At most this many reads in flight at once (WI-0029, SUM0-034; the same
/// bound as Chrona's WI-0077).
[<Literal>]
let ReadWidth = 6

/// Runs the steps at most `width` at a time and gives their results in the
/// order given. Each step is started in turn on the caller's thread, never on
/// the thread pool, so a single-threaded host (the browser, whose bridge
/// parks every request until the kernel answers it) sees each step continue
/// within the message that answers it. A step that fails stops new steps
/// from starting; the first failure in order is the result once the running
/// ones have settled. This is fork-join machinery at the edge, so it holds
/// its bookkeeping in local mutable state; everything it runs stays pure.
let private bounded (turn: Turn) (width: int) (steps: Async<Result<'a list, 'e>> list) : Async<Result<'a list, 'e>> =
    let all = Array.ofList steps

    if all.Length = 0 then
        async.Return(Ok [])
    else
        Async.FromContinuations(fun (complete, fail, _) ->
            let gate = obj ()
            let results: Choice<Result<'a list, 'e>, exn> option array = Array.create all.Length None
            let mutable next = 0
            let mutable running = 0
            let mutable stopped = false
            let mutable launching = false
            let mutable pending = false
            let mutable finished = false

            let outcome () =
                let settled = results |> Array.choose id

                match settled |> Array.tryPick (function Choice2Of2 ex -> Some ex | _ -> None) with
                | Some ex -> Choice2Of2 ex
                | None ->
                    match settled |> Array.tryPick (function Choice1Of2(Error e) -> Some e | _ -> None) with
                    | Some e -> Choice1Of2(Error e)
                    | None -> Choice1Of2(Ok(settled |> Array.toList |> List.collect (function Choice1Of2(Ok v) -> v | _ -> [])))

            let rec settle (i: int) (result: Choice<Result<'a list, 'e>, exn>) =
                let done' =
                    lock gate (fun () ->
                        results[i] <- Some result
                        running <- running - 1

                        match result with
                        | Choice1Of2(Ok _) -> ()
                        | _ -> stopped <- true

                        if not finished && running = 0 && (stopped || next >= all.Length) then
                            finished <- true
                            true
                        else
                            false)

                if done' then
                    turn.Defer(fun () ->
                        match outcome () with
                        | Choice1Of2 value -> complete value
                        | Choice2Of2 ex -> fail ex)
                else
                    launch ()

            and launch () =
                // Trampolined: a step that completes at once does not recurse.
                let enter = lock gate (fun () -> if launching then (pending <- true; false) else (launching <- true; true))

                if enter then
                    let mutable again = true

                    while again do
                        let start =
                            lock gate (fun () ->
                                pending <- false

                                if not stopped && running < width && next < all.Length then
                                    let i = next
                                    next <- next + 1
                                    running <- running + 1
                                    Some i
                                else
                                    None)

                        match start with
                        | Some i -> Async.StartWithContinuations(all[i], (fun r -> settle i (Choice1Of2 r)), (fun ex -> settle i (Choice2Of2 ex)), (fun c -> settle i (Choice2Of2(c :> exn))))
                        | None -> again <- lock gate (fun () -> if pending then true else (launching <- false; false))

            launch ())

/// Every object under the folders, recursively, read at most `ReadWidth` at a
/// time. A file whose listing shows the same revision as an object in `known`
/// is that object (a revision names one content), so only what changed is
/// read (SUM0-034). A listing the provider cut short is a failure: what was
/// not read was not verified.
let readAllKnowing (turn: Turn) (known: Map<string, StoredObject>) (provider: StorageProvider) (ns: Namespace) : Async<Result<StoredObject list, CommandFailure<'e>>> =
    let read (path: RelativePath) =
        async {
            match! provider.Read ns path with
            | Ok(ReadOutcome.Found stored) -> return Ok [ stored ]
            | Ok ReadOutcome.Absent -> return Ok []
            // A member erased (ARCA-INT-005) is no longer a member.
            // An erased financial record would change history:
            // the books are not kept on without it.
            | Ok(ReadOutcome.Erased erased) when RelativePath.render(erased.Path).Contains "/members/" -> return Ok []
            | Ok(ReadOutcome.Erased erased) ->
                return
                    Error(
                        Untrustworthy
                            [ InvalidStoredRecord(RelativePath.render erased.Path, "it was erased; Summa does not keep books with an erased financial record") ]
                    )
            | Error failure -> return Error(StorageFailed failure)
        }

    let list (folder: RelativePath) : Async<Result<ListEntry list, CommandFailure<'e>>> =
        async {
            match! provider.List ns folder with
            | Error failure -> return Error(StorageFailed failure)
            | Ok listing when not listing.Complete -> return Error(Untrustworthy [ InvalidStoredRecord(RelativePath.render folder, "the listing was incomplete") ])
            | Ok listing -> return Ok listing.Entries
        }

    // Folder by folder, one level at a time, so no more than `ReadWidth`
    // requests are ever in flight; the files in the order they were listed.
    let rec files (level: RelativePath list) : Async<Result<ListEntry list, CommandFailure<'e>>> =
        async {
            if level.IsEmpty then
                return Ok []
            else
                match! level |> List.map list |> bounded turn ReadWidth with
                | Error failure -> return Error failure
                | Ok entries ->
                    match! files (entries |> List.filter _.IsFolder |> List.map _.Path) with
                    | Error failure -> return Error failure
                    | Ok deeper -> return Ok((entries |> List.filter (fun e -> not e.IsFolder)) @ deeper)
        }

    async {
        match! files folders with
        | Error failure -> return Error failure
        | Ok entries ->
            return!
                entries
                |> List.map (fun entry ->
                    match known.TryFind(RelativePath.render entry.Path) with
                    | Some stored when stored.Revision = entry.Revision -> async.Return(Ok [ stored ])
                    | _ -> read entry.Path)
                |> bounded turn ReadWidth
    }

/// The provider with its reads and listings pinned to one change token
/// (Arca ARCA-API-006, arca#39): each goes to that commit with one request
/// and no ref fetch, and together they are one consistent snapshot. Commits
/// and the namespace's current state still go to the provider.
let pinnedTo (pin: PinnedReads) (provider: StorageProvider) =
    { provider with
        Read = pin.Read
        List = pin.List }

/// The reads for content observed at `state`: pinned to its commit where the
/// host offers pins, otherwise the provider's own (which resolve the branch).
let readsAt (pins: ChangeToken -> PinnedReads option) (state: NamespaceState) (provider: StorageProvider) =
    match pins state.RepositoryToken with
    | Some pin -> pinnedTo pin provider
    | None -> provider

/// Every object under the folders, read afresh.
let readAll (provider: StorageProvider) (ns: Namespace) : Async<Result<StoredObject list, CommandFailure<'e>>> =
    readAllKnowing Turn.now Map.empty provider ns

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

/// Who may deliver invoices in the organization: the roster's members
/// holding `DeliverInvoice` (INV-DEL-005). The delivery commands check it
/// against the context's actor, whatever the interface showed.
let deliveryAuthority (roster: Roster) =
    Summa.Ledger.Lifecycle.DeliveryAuthority(fun who -> permits roster who DeliverInvoice)

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
    (state: NamespaceState)
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
                                |> Result.map (fun operation -> next, Some(Operation.requireNamespaceToken state.NamespaceToken operation))))

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
            | Ok _, Ok(ReadOutcome.Erased _) -> return Error(Untrustworthy [ NamespaceUnusable(RelativePath.render ns.Root, "its organization manifest was erased") ])
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
            match! provider.NamespaceState ns with
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
                        | Error(StorageFailure.StaleChangeToken _)
                        | Error(StorageFailure.StaleNamespaceToken _) -> return! retry ()
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
