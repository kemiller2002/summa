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
      CommitManifest: Organization.OrganizationManifest -> unit }

/// A store for books kept in the browser: nothing to open on GitHub.
let none: StorePort =
    { Open = fun _ _ -> ()
      Found = ignore
      Confirm = ignore
      Migrate = ignore
      Commit = ignore
      CommitManifest = ignore }

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

    /// Opens the books for the context's person, as the engine's message.
    let opening (ctx: Context) : Async<Msg> =
        async {
            match! Workspace.openBooks (providerOf ctx.Namespace) ctx.Config ctx.Binding ctx.Organization ctx.Person.ActorId with
            | Error failure -> return BooksNotOpened(BooksUnreachable(describeFailure (widen failure)))
            | Ok(Workspace.Opened opened) ->
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
                                let! opened = opening ctx
                                return [ opened ]
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
                    let request: Commands.Request<string> =
                        { Actor = actorOf ctx.Person
                          Capability = commit.Command.Capability
                          Summary = commit.Command.Summary
                          IdempotencyKey = newKey ()
                          Transition = commit.Transition }

                    match! Commands.execute (providerOf ctx.Namespace) Access.defaultApprovalGates ctx.Namespace Attempts request with
                    | Ok outcome -> return [ BooksCommitted outcome.State ]
                    | Error failure ->
                        let! now = latest ctx
                        return [ StoreRefused(describeFailure failure, now) ]
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
