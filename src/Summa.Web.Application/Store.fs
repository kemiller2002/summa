/// The books on GitHub (WI-0037, DF-SUMMA-2026-0014): what the engine asks
/// of the store, carried out through Arca.
///
/// The engine asks to open the organization's books, to set them up, to
/// confirm an administrator, to migrate them, and to commit a command. This
/// module answers with engine messages (`BooksOpened`, `BooksNotOpened`,
/// `BooksCommitted`, `StoreRefused`, ...). It runs Arca's provider-neutral
/// `StorageProvider`: in the browser, Arca's GitHub adapter, whose every
/// request is a Limen Http request through the `Bridge` and whose token
/// comes from Fides' token provider, never from Summa; in tests, Arca's
/// in-memory provider. The rules are the domain's (`Workspace`, `Commands`,
/// `Migrations`, `Governance`); this module only sequences them, one job at
/// a time.
module Summa.Web.Application.Store

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Summa.Access
open Summa.Storage
open Summa.Storage.Deployment
open Summa.Web.Engine.Accounting
open Summa.Web.Application.Bridge

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The provider serving a location.
      Provider: DataLocation -> StorageProvider
      /// What the signed-in credential can do at a location: whether it may
      /// read and write, and the repository's visibility. A refusal says why,
      /// for the person.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, string>> }

/// A commit the engine asked for: the command, and its transition, which
/// runs the message again on the books as they stand (`replay`).
[<NoComparison; NoEquality>]
type Commit =
    { Command: BooksCommand
      Transition: Summa.Ledger.Payments.Receivables -> Result<Summa.Ledger.Payments.Receivables, string> }

/// The store port the application drives.
[<NoComparison; NoEquality>]
type StorePort =
    { Open: DeploymentConfig -> SignedInPerson -> unit
      Found: unit -> unit
      Confirm: unit -> unit
      Migrate: unit -> unit
      Commit: Commit -> unit
      CommitManifest: Organization.OrganizationManifest -> unit
      /// Try now to send the changes GitHub does not have yet.
      SendUnsent: unit -> unit
      /// Give up the blocked change at this place in line.
      Abandon: int64 -> unit
      /// Check every financial record against its history.
      Check: unit -> unit }

/// A store for books kept in the browser: nothing to open on GitHub.
let none: StorePort =
    { Open = fun _ _ -> ()
      Found = ignore
      Confirm = ignore
      Migrate = ignore
      Commit = ignore
      CommitManifest = ignore
      SendUnsent = ignore
      Abandon = ignore
      Check = ignore }

let private methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

let private describeResolve =
    function
    | ResolveError.CredentialUnavailable _ -> "You are not signed in to GitHub any more. Sign in again."
    | ResolveError.CredentialRejected -> "GitHub refused your sign-in. Sign in again."
    | ResolveError.RepositoryNotFound repository -> $"Your GitHub account cannot see {repository}."
    | ResolveError.Call _ -> "GitHub could not be reached."

/// Arca's GitHub adapter, its requests sent as Limen Http requests, its waits
/// as `limen.schedule` timeouts, its tokens from Fides' token provider.
let gitHub (bridge: Bridge) (tokens: unit -> TokenProvider option) : Backend =
    let host: Host =
        { Send =
            fun authorized ->
                async {
                    let request = authorized.Request
                    let credential = authorized.Credential |> Option.map AccessToken.authorization |> Option.toList

                    match! bridge.Call(Request(methodName request.Method, request.Url, request.Headers @ credential, request.Body, request.TimeoutMs, request.ResponseHeaders)) with
                    | Http(Responded(status, headers, body)) -> return HttpOutcome.Response(status, Http.normalizeHeaders headers, body)
                    | Http(Unknown reason) ->
                        return HttpOutcome.OutcomeUnknown(if reason = "connection-lost" then UnknownReason.ConnectionLost else UnknownReason.TimeoutAfterDispatch)
                    | Http(Unreachable reason) ->
                        return
                            HttpOutcome.Failed(
                                match reason with
                                | "aborted" -> HttpFailure.Aborted
                                | "invalid-response" -> HttpFailure.InvalidResponse
                                | "too-large" -> HttpFailure.TooLarge
                                | _ -> HttpFailure.Network
                            )
                    | Read _
                    | Done
                    | Refused _
                    | Locked _
                    | Missing -> return HttpOutcome.Failed HttpFailure.InvalidResponse
                }
          Wait = fun delay -> bridge.Call(Sleep(int delay.TotalMilliseconds)) |> Async.Ignore
          Tokens =
            fun () ->
                match tokens () with
                | Some provider -> provider ()
                | None -> async.Return(Error TokenUnavailable.NoToken) }

    { Provider = fun location -> GitHubStorage.provider host (GitHubConfig.create location)
      Resolve =
        fun location ->
            async {
                match! Conversation.run host (Arca.GitHub.Identity.resolve (GitHubConfig.create location)) with
                | Ok snapshot -> return Ok snapshot
                | Error error -> return Error(describeResolve error)
            } }

/// Browser localStorage through Limen's Storage effect, as Arca's queue
/// store asks for it.
let localStorage (bridge: Bridge) (request: LocalStorageRequest) : Async<LocalStorageOutcome> =
    async {
        let call =
            match request with
            | LocalStorageRequest.Get key -> DeviceGet key
            | LocalStorageRequest.Set(key, value) -> DeviceSet(key, value)
            | LocalStorageRequest.Remove key -> DeviceRemove key

        match! bridge.Call call with
        | Read value -> return LocalStorageOutcome.Success value
        | Done -> return LocalStorageOutcome.Success None
        | Refused "quota-exceeded" -> return LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded
        | _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
    }

/// The Web Lock that makes this tab the holder of the unsent changes,
/// through `limen.coordination`.
let lock (bridge: Bridge) (request: QueueLockRequest) : Async<QueueLockOutcome> =
    async {
        let (QueueLockRequest.Acquire name) = request

        match! bridge.Call(Lock name) with
        | Locked "Acquired" -> return QueueLockOutcome.Acquired
        | Locked "Busy" -> return QueueLockOutcome.Busy
        | _ -> return QueueLockOutcome.Unsupported
    }

/// Why a store operation did not complete, for the person.
let describeFailure (failure: Commands.CommandFailure<string>) =
    let all (diagnostics: Diagnostics.Diagnostic list) = diagnostics |> List.map Diagnostics.describe |> String.concat " "

    match failure with
    | Commands.InvalidActor _ -> "Summa cannot act for this sign-in."
    | Commands.NotAuthorized _ -> "Your role in these books does not include this."
    | Commands.NeedsApproval _ -> "This needs a person's approval."
    | Commands.Untrustworthy problems -> "The books failed their checks: " + all problems
    | Commands.Incompatible _ -> "These books were written by another version of Summa; this one cannot change them."
    | Commands.Rejected why -> why
    | Commands.Unstorable problems -> "The change cannot be stored: " + all problems
    | Commands.StillConflicted attempts -> $"Others kept changing the books; the change was decided {attempts} times and not saved. Try again."
    | Commands.Unknown _ -> "GitHub did not say whether the change was saved. Reload to see the books as they stand."
    | Commands.StorageFailed(StorageFailure.Conflicted _) -> "Someone else changed the same records first. The books show their change; try yours again."
    | Commands.StorageFailed(StorageFailure.Refused _) -> "GitHub refused the write: your account cannot write to this repository or branch."
    | Commands.StorageFailed(StorageFailure.RateLimited _) -> "GitHub asked Summa to slow down. Try again in a minute."
    | Commands.StorageFailed _ -> "GitHub could not be reached, or refused the request."

let private widen (failure: Commands.CommandFailure<unit>) : Commands.CommandFailure<string> =
    match failure with
    | Commands.InvalidActor problems -> Commands.InvalidActor problems
    | Commands.NotAuthorized refusal -> Commands.NotAuthorized refusal
    | Commands.NeedsApproval capability -> Commands.NeedsApproval capability
    | Commands.Untrustworthy problems -> Commands.Untrustworthy problems
    | Commands.Incompatible access -> Commands.Incompatible access
    | Commands.Rejected() -> Commands.Rejected "The change was refused."
    | Commands.Unstorable problems -> Commands.Unstorable problems
    | Commands.StillConflicted attempts -> Commands.StillConflicted attempts
    | Commands.Unknown pending -> Commands.Unknown pending
    | Commands.StorageFailed failure -> Commands.StorageFailed failure

/// The open books' context: where they live and who works in them.
[<NoComparison; NoEquality>]
type private Context =
    { Config: DeploymentConfig
      Binding: ApplicationBinding
      Organization: OrganizationConfig
      Namespace: Namespace
      Person: SignedInPerson
      Snapshot: CapabilitySnapshot }

/// How many times a command is decided again when others changed the books.
[<Literal>]
let Attempts = 3

/// The Arca store over a backend. `now` is the clock; `newKey` a fresh,
/// unique idempotency key.
let arca (bridge: Bridge) (backend: Backend) (now: unit -> DateTimeOffset) (newKey: unit -> string) : StorePort =
    let mutable context: Context option = None
    let jobs = Queue<unit -> Async<Msg list>>()
    let mutable busy = false

    /// Runs the queued jobs one at a time; each job's messages go to the
    /// engine as it finishes.
    let rec drain () =
        async {
            if jobs.Count = 0 then
                busy <- false
            else
                let job = jobs.Dequeue()
                let! messages = job ()
                bridge.Emit messages
                return! drain ()
        }

    let serial (job: unit -> Async<Msg list>) =
        jobs.Enqueue job

        if not busy then
            busy <- true

            bridge.Start(
                async {
                    do! drain ()
                    return []
                }
            )

    let subjectOf (person: SignedInPerson) =
        match person.ActorId.Split(':', 2) with
        | [| _; subject |] -> subject
        | _ -> person.ActorId

    let actorOf (person: SignedInPerson) =
        Actor.person (subjectOf person) person.Login (newKey ())

    let operationContext (person: SignedInPerson) : Result<Storage.OperationContext, string> =
        match ActorId.create person.ActorId, CorrelationId.create (newKey ()), IdempotencyKey.create (newKey ()) with
        | Ok id, Ok correlation, Ok key ->
            Ok
                { Actor = { Kind = ActorKind.Human; Id = id }
                  ProviderIdentity = Some person.Login
                  CorrelationId = correlation
                  IdempotencyKey = key
                  At = now () }
        | _ -> Error "Summa cannot act for this sign-in."

    let providerOf (ns: Namespace) = backend.Provider ns.Location

    let describeAll (diagnostics: Diagnostics.Diagnostic list) =
        diagnostics |> List.map Diagnostics.describe

    /// The records as last read from GitHub, and the change token then.
    let mutable lastRead: (ChangeToken * StoredObject list) option = None

    /// Opens the books for the context's person, as the engine's message.
    let opening (ctx: Context) : Async<Msg> =
        async {
            match! Workspace.openBooks (providerOf ctx.Namespace) ctx.Config ctx.Binding ctx.Organization ctx.Person.ActorId with
            | Error failure -> return BooksNotOpened(BooksUnreachable(describeFailure (widen failure)))
            | Ok(Workspace.Opened opened) ->
                match opened.Read with
                | Some token, objects -> lastRead <- Some(token, objects)
                | None, _ -> ()

                let readOnly =
                    (match opened.Access with
                     | Compatibility.ReadOnly reasons -> describeAll reasons
                     | _ -> [])
                    @ (if ctx.Snapshot.CanWrite then [] else [ "your GitHub account cannot write to the data repository" ])

                return
                    BooksOpened
                        { Manifest = opened.Manifest
                          Books = opened.Books
                          Capabilities = opened.Capabilities
                          ReadOnly = readOnly }
            | Ok(Workspace.NotSetUp Governance.Found) -> return BooksNotOpened(BooksNotSetUp(true, None))
            | Ok(Workspace.NotSetUp(Governance.Refused reason)) -> return BooksNotOpened(BooksNotSetUp(false, Some reason))
            | Ok(Workspace.NotSetUp _) -> return BooksNotOpened(BooksNotSetUp(false, None))
            | Ok(Workspace.Held canConfirm) -> return BooksNotOpened(BooksAwaitAdministrator canConfirm)
            | Ok(Workspace.NeedsMigration mayMigrate) -> return BooksNotOpened(BooksOutdated mayMigrate)
            | Ok Workspace.NotAMember -> return BooksNotOpened BooksNotForYou
            | Ok(Workspace.Unusable problems) -> return BooksNotOpened(BooksUnusable(describeAll problems))
        }

    /// The books as they stand now, for a refused change.
    let latest (ctx: Context) =
        async {
            match! opening ctx with
            | BooksOpened opened -> return Some(opened.Manifest, opened.Books)
            | _ -> return None
        }

    let withContext (work: Context -> Async<Msg list>) =
        match context with
        | Some ctx -> serial (fun () -> work ctx)
        | None -> ()

    // ---- The unsent changes (Arca's offline queue) ----------------------------

    /// This tab's queue of operations GitHub does not have yet, oldest first.
    let mutable queue = OfflineQueue.create OfflinePolicy.QueueWrites
    /// Where the queue is kept: this browser's localStorage when this tab
    /// holds it, else this tab's memory only.
    let mutable queueStore: QueueStore option = None
    let mutable queueNote: string option = None
    /// The commands queued in this tab, by place in line: when GitHub moved
    /// under one, it is decided again on the newer books and revised.
    let mutable replays: Map<int64, Commit> = Map.empty

    let inMemory: QueueStore =
        { Load = fun () -> async.Return(Ok(Some queue))
          Save = fun _ -> async.Return(Ok()) }

    let storeOf () = queueStore |> Option.defaultValue inMemory

    let summaryOf (entry: QueueEntry) = entry.Operation.Summary

    let unsentView () : Unsent =
        let status = OfflineQueue.status queue
        let waiting = status.Pending + status.InFlight + status.OutcomeUnknown + status.Conflicted + status.Refused

        let blocked =
            OfflineQueue.blocked queue
            |> Option.map (fun entry ->
                let why =
                    match entry.State with
                    | EntryState.Conflicted _ -> "Someone else changed the same records on GitHub first."
                    | EntryState.Refused reason -> $"GitHub refused it ({reason})."
                    | EntryState.OutcomeUnknown _ -> "GitHub did not say whether it was saved; Summa checks before sending it again."
                    | _ -> "It is being sent."

                entry.Sequence, summaryOf entry, why)

        { Waiting = waiting
          Blocked = blocked
          Note = if waiting > 0 then queueNote else None }

    /// Takes this browser's unsent changes for this tab when no other tab
    /// holds them, and loads what an earlier page left (an entry that was
    /// being sent may have landed: it is reconciled, never resent blindly).
    let own (ns: Namespace) =
        async {
            match! LocalStorageQueue.own (lock bridge) (localStorage bridge) LocalStorageQueue.DefaultBudget ns with
            | QueueOwnership.Owned store ->
                queueStore <- Some store
                queueNote <- None

                match! store.Load() with
                | Ok(Some loaded) -> queue <- OfflineQueue.recover loaded
                | Ok None -> ()
                | Error _ -> queueNote <- Some "The unsent changes kept in this browser could not be read; changes made now are kept in this tab until sent."
            | QueueOwnership.OwnedElsewhere ->
                queueStore <- None
                queueNote <- Some "Another Summa tab holds this browser's unsent changes; changes made in this tab are kept here until sent."
            | QueueOwnership.OwnershipUnsupported ->
                queueStore <- None
                queueNote <- Some "This browser cannot keep unsent changes across a reload; they are kept in this tab until sent."
        }

    /// The organization's records and the change token they were read at.
    let readState (ctx: Context) =
        async {
            let provider = providerOf ctx.Namespace

            match! provider.ChangeToken ctx.Namespace with
            | Error failure -> return Error(Commands.StorageFailed failure)
            | Ok token ->
                match! Commands.readAll provider ctx.Namespace with
                | Error failure -> return Error failure
                | Ok objects ->
                    lastRead <- Some(token, objects)
                    return Ok(token, objects)
        }

    let requestOf (ctx: Context) (commit: Commit) : Commands.Request<string> =
        { Actor = actorOf ctx.Person
          Capability = commit.Command.Capability
          Summary = commit.Command.Summary
          IdempotencyKey = newKey ()
          Transition = commit.Transition }

    /// Sends the queue, oldest first, until it is empty, blocked or GitHub
    /// cannot be reached. A command of this tab that GitHub moved under is
    /// decided again on the newer books and sent again; one that no longer
    /// applies is given up and said. Answers the books as GitHub then holds
    /// them when everything was sent, and what the engine needs to hear.
    let rec synchronize (ctx: Context) (attempts: int) : Async<Summa.Ledger.Payments.Receivables option * Msg list> =
        async {
            let! synced, step = OfflineSync.run (providerOf ctx.Namespace) (storeOf ()) ctx.Namespace 50 queue
            queue <- synced

            match step with
            | SyncStep.Blocked entry when attempts > 0 && replays.ContainsKey entry.Sequence && (match entry.State with EntryState.Conflicted _ -> true | _ -> false) ->
                let commit = replays[entry.Sequence]

                match! readState ctx with
                | Error _ -> return None, [ UnsentChanged(unsentView ()) ]
                | Ok(token, objects) ->
                    match Commands.decide Access.defaultApprovalGates ctx.Namespace token objects (requestOf ctx commit) with
                    | Ok(_, Some operation) ->
                        match OfflineQueue.revise entry.Sequence operation queue with
                        | Ok revised ->
                            queue <- revised
                            return! synchronize ctx (attempts - 1)
                        | Error _ -> return None, [ UnsentChanged(unsentView ()) ]
                    | Ok(_, None) ->
                        // Already as it would make them: nothing to send.
                        queue <- OfflineQueue.abandon entry.Sequence "already applied" queue |> Result.defaultValue queue
                        replays <- replays.Remove entry.Sequence
                        return! synchronize ctx attempts
                    | Error failure ->
                        queue <- OfflineQueue.abandon entry.Sequence "no longer applies" queue |> Result.defaultValue queue
                        replays <- replays.Remove entry.Sequence
                        let! sent, more = synchronize ctx attempts
                        return sent, StoreRefused($"{commit.Command.Summary}: {describeFailure failure}", None) :: more
            | SyncStep.Idle ->
                queue <- OfflineQueue.prune queue
                replays <- Map.empty
                do! (storeOf ()).Save queue |> Async.Ignore

                match! readState ctx with
                | Ok(_, objects) ->
                    let loaded = FinancialRecords.load (objects |> List.filter (fun o -> match Layout.keyOf o.Path with Some k -> k.Type <> MemberRecord.recordType | None -> true))
                    return Some loaded.State, [ UnsentChanged(unsentView ()) ]
                | Error _ -> return None, [ UnsentChanged(unsentView ()) ]
            | _ -> return None, [ UnsentChanged(unsentView ()) ]
        }

    /// Sends what is waiting, for the page as a whole (not one command).
    let synchronizeAll (ctx: Context) =
        async {
            let! sent, messages = synchronize ctx Attempts
            return (sent |> Option.map BooksSynchronized |> Option.toList) @ messages
        }

    let rec commitAll (operations: Operation list) =
        async {
            match operations with
            | [] -> return Ok()
            | operation :: rest ->
                match! (providerOf operation.Namespace).Commit operation with
                | Ok _ -> return! commitAll rest
                | Error failure -> return Error(Commands.StorageFailed failure)
        }

    { Open =
        fun config person ->
            serial (fun () ->
                async {
                    match Storage.binding config, config.Organizations with
                    | Error problem, _ -> return [ BooksNotOpened(BooksUnusable [ Diagnostics.describe problem ]) ]
                    | _, [] -> return [ BooksNotOpened(BooksUnusable [ "This deployment names no organization." ]) ]
                    // One organization per deployment for now: the first configured.
                    | Ok binding, organization :: _ ->
                        match Storage.organizationNamespace config binding organization.Id with
                        | Error problem -> return [ BooksNotOpened(BooksUnusable [ Diagnostics.describe problem ]) ]
                        | Ok ns ->
                            match! backend.Resolve ns.Location with
                            | Error reason -> return [ BooksNotOpened(BooksUnreachable reason) ]
                            | Ok snapshot when not snapshot.CanRead -> return [ BooksNotOpened(BooksUnreachable "Your GitHub account cannot read the data repository.") ]
                            | Ok snapshot ->
                                let ctx =
                                    { Config = config
                                      Binding = binding
                                      Organization = organization
                                      Namespace = ns
                                      Person = person
                                      Snapshot = snapshot }

                                context <- Some ctx

                                match! Storage.applicationNamespace binding |> Result.map (fun a -> Workspace.checkApplication (providerOf a) binding) |> Result.defaultValue (async.Return(Ok())) with
                                | Error failure -> return [ BooksNotOpened(BooksUnusable [ describeFailure (widen failure) ]) ]
                                | Ok() ->
                                    do! own ns
                                    // What an earlier page left is sent first, so the books shown include it.
                                    let! _, sent = if queue.Entries.IsEmpty then async.Return(None, []) else synchronize ctx 0
                                    let! opened = opening ctx
                                    return opened :: sent
                })
      Found =
        fun () ->
            withContext (fun ctx ->
                async {
                    match operationContext ctx.Person with
                    | Error why -> return [ BooksNotOpened(BooksUnreachable why) ]
                    | Ok operation ->
                        let founder: Access.Principal =
                            { PrincipalId = ctx.Person.ActorId
                              Kind = Access.Human
                              DisplayName = ctx.Person.Login }

                        let application = Storage.applicationNamespace ctx.Binding

                        match application with
                        | Error problem -> return [ BooksNotOpened(BooksUnusable [ Diagnostics.describe problem ]) ]
                        | Ok application ->
                            match! Workspace.foundingOperations (providerOf application) ctx.Config ctx.Binding ctx.Snapshot.Visibility operation ctx.Organization founder with
                            | Error failure -> return [ BooksNotOpened(BooksUnreachable(describeFailure (widen failure))) ]
                            | Ok operations ->
                                match! commitAll operations with
                                | Error failure -> return [ BooksNotOpened(BooksUnreachable(describeFailure (widen failure))) ]
                                | Ok() ->
                                    let! opened = opening ctx
                                    return [ opened ]
                })
      Confirm =
        fun () ->
            withContext (fun ctx ->
                async {
                    match operationContext ctx.Person with
                    | Error why -> return [ BooksNotOpened(BooksUnreachable why) ]
                    | Ok operation ->
                        match! Commands.readAll (providerOf ctx.Namespace) ctx.Namespace with
                        | Error failure -> return [ BooksNotOpened(BooksUnreachable(describeFailure (widen failure))) ]
                        | Ok objects ->
                            let principal: Access.Principal =
                                { PrincipalId = ctx.Person.ActorId
                                  Kind = Access.Human
                                  DisplayName = ctx.Person.Login }

                            match Workspace.confirmation ctx.Namespace operation ctx.Organization objects principal with
                            | Error problems -> return [ BooksNotOpened(BooksUnusable(describeAll problems)) ]
                            | Ok confirmation ->
                                match! commitAll [ confirmation ] with
                                | Error failure -> return [ BooksNotOpened(BooksUnreachable(describeFailure (widen failure))) ]
                                | Ok() ->
                                    let! opened = opening ctx
                                    return [ opened ]
                })
      Migrate =
        fun () ->
            withContext (fun ctx ->
                async {
                    match! Migrations.migrate (providerOf ctx.Namespace) Access.defaultApprovalGates ctx.Namespace (actorOf ctx.Person) (newKey ()) with
                    | Error failure -> return [ BooksNotOpened(BooksUnreachable(describeFailure (widen failure))) ]
                    | Ok _ ->
                        let! opened = opening ctx
                        return [ opened ]
                })
      Commit =
        fun commit ->
            withContext (fun ctx ->
                async {
                    // Decided on the books as GitHub holds them now; when GitHub
                    // cannot be reached, on the books as last read, and kept.
                    let! fresh = readState ctx

                    match (match fresh with Ok read -> Some read | Error _ -> lastRead) with
                    | None -> return [ StoreRefused("GitHub could not be reached, and these books were never read here.", None) ]
                    | Some(token, objects) ->
                        match Commands.decide Access.defaultApprovalGates ctx.Namespace token objects (requestOf ctx commit) with
                        | Error failure ->
                            let! now = latest ctx
                            return [ StoreRefused(describeFailure failure, now) ]
                        | Ok(state, None) -> return [ BooksCommitted state ]
                        | Ok(_, Some operation) ->
                            match OfflineQueue.enqueue (now ()) operation queue with
                            | Error error -> return [ StoreRefused($"The change could not be kept to send: %A{error}", None) ]
                            | Ok(queued, sequence) ->
                                queue <- queued
                                replays <- replays.Add(sequence, commit)
                                do! (storeOf ()).Save queue |> Async.Ignore

                                match! synchronize ctx Attempts with
                                | Some books, messages -> return BooksCommitted books :: messages
                                | None, messages -> return ChangeKept :: messages
                })
      SendUnsent = fun () -> withContext synchronizeAll
      Check =
        fun () ->
            withContext (fun ctx ->
                async {
                    match! Verification.audit (providerOf ctx.Namespace) ctx.Namespace with
                    | Ok findings -> return [ BooksChecked(describeAll findings) ]
                    | Error failure -> return [ BooksChecked [ "The books could not be checked: " + describeFailure (Commands.StorageFailed failure) ] ]
                })
      Abandon =
        fun sequence ->
            withContext (fun ctx ->
                async {
                    match OfflineQueue.abandon sequence "given up by the person" queue with
                    | Error _ -> return [ UnsentChanged(unsentView ()) ]
                    | Ok abandoned ->
                        queue <- abandoned
                        replays <- replays.Remove sequence
                        do! (storeOf ()).Save queue |> Async.Ignore
                        let! sent = synchronizeAll ctx
                        let! opened = opening ctx
                        return opened :: sent
                })
      CommitManifest =
        fun next ->
            withContext (fun ctx ->
                async {
                    let provider = providerOf ctx.Namespace

                    match Organization.path ctx.Organization.Id, operationContext ctx.Person with
                    | Error problem, _ -> return [ StoreRefused(Diagnostics.describe problem, None) ]
                    | _, Error why -> return [ StoreRefused(why, None) ]
                    | Ok path, Ok operation ->
                        match! provider.Read ctx.Namespace path with
                        | Ok(ReadOutcome.Found stored) ->
                            match Organization.decode ctx.Organization.Id stored with
                            | Error problem -> return [ StoreRefused(Diagnostics.describe problem, None) ]
                            | Ok previous ->
                                match Storage.updateOrganization ctx.Namespace operation stored.Revision previous next with
                                | Error problems -> return [ StoreRefused(describeAll problems |> String.concat " ", None) ]
                                | Ok change ->
                                    match! provider.Commit change with
                                    | Ok _ -> return [ ManifestCommitted next ]
                                    | Error failure ->
                                        let! now = latest ctx
                                        return [ StoreRefused(describeFailure (Commands.StorageFailed failure), now) ]
                        | _ -> return [ StoreRefused("The organization's settings could not be read.", None) ]
                }) }
