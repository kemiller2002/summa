/// Sign-in through Fides (WI-0035, SUM0-003, SUM0-004, SUM3-012): Fides' own
/// client, unmodified, running inside the Limen request/reply loop.
///
/// The client asks its host for browser services (`ClientPorts`). Each
/// becomes a kernel request through the `Bridge`: the exchange is Limen's
/// Http effect, device storage its Storage effect, and this tab's session
/// storage, leaving for the provider, tidying the address bar and telling
/// the other tabs are `summa.host` requests (web-kernel/host.js). A finished
/// operation becomes an engine message (`IdentityChanged`).
///
/// Tokens stay inside the Fides client. Summa sees the identity the provider
/// resolved (subject and login), never a token or a typed name.
module Summa.Web.Application.Identity

open System
open Fides
open Fides.Client
open Summa.Storage.Deployment
open Summa.Web.Engine.Accounting
open Summa.Web.Application.Bridge

/// The identity port the application drives.
[<NoComparison; NoEquality>]
type IdentityPort =
    { /// Sets the client up from the deployment's configuration and either
      /// completes the provider's callback or restores a kept session.
      Begin: IdentityConfig -> (string * string) list -> unit
      SignIn: KeepSignIn -> unit
      SignOut: unit -> unit
      /// A message another tab broadcast.
      Receive: string -> unit
      /// Arca's token provider, once sign-in is configured: the GitHub store
      /// asks it for a token; Summa never holds one (Fides.Arca).
      TokenProvider: unit -> Arca.TokenProvider option }

/// The engine's view of an identity the provider resolved: the stable
/// subject is the actor, the login only a display name (SUM0-004).
let changeOfIdentity (identity: Fides.Identity) =
    let (ProviderId provider) = identity.Provider
    IdentitySignedIn(provider, identity.Subject, identity.Login)

/// The engine's view of a session state.
let changeOf (state: SessionState) : IdentityChange =
    match state with
    | SessionState.SignedIn identity -> changeOfIdentity identity
    | SessionState.SigningIn -> IdentitySigningIn
    | SessionState.SignedOut -> IdentitySignedOut None
    | SessionState.Expired -> IdentitySignedOut(Some "expired")
    | SessionState.Revoked -> IdentitySignedOut(Some "revoked")
    | SessionState.ProviderUnavailable -> IdentityProviderUnavailable

/// The engine's view of how a callback ended.
let callbackChange (outcome: CallbackOutcome) : IdentityChange =
    match outcome with
    | CompletedSignIn identity -> changeOfIdentity identity
    | other -> IdentitySignedOut(Some(CallbackOutcome.code other))

let private retention =
    function
    | ThisPage -> MemoryOnly
    | ThisTab -> SessionScoped

/// The client configuration for a deployment's identity settings.
let configuration (config: IdentityConfig) : ClientConfiguration =
    { Application = config.Application
      Provider = ProviderId config.Provider
      ClientId = config.ClientId
      RedirectUri = config.RedirectUri }

/// The providers Summa signs in with.
let catalog = ProviderCatalog.ofList [ GitHub.provider GitHub.githubDotCom ]

/// How long the exchange may take to answer.
[<Literal>]
let ExchangeTimeoutMs = 15000

/// A new identity port over the page's bridge. `now` and `randomBytes` are
/// the clock and the browser's cryptographic random source.
let create (bridge: Bridge) (now: unit -> DateTimeOffset) (randomBytes: int -> byte array) : IdentityPort =
    let mutable client: FidesClient option = None

    let read request =
        async {
            match! bridge.Call request with
            | Read value -> return value
            | _ -> return None
        }

    let ports (exchange: string) =
        { PostToExchange =
            fun path body ->
                async {
                    match! bridge.Call(Post(exchange + path, body, ExchangeTimeoutMs)) with
                    | Http(Responded(status, _, body)) -> return HttpOutcome.Responded { Status = status; Body = body }
                    | Http(Unknown _) -> return HttpOutcome.Failed TransportFailure.TimedOut
                    | _ -> return HttpOutcome.Failed TransportFailure.Unreachable
                }
          TabStorage =
            { Read = fun key -> read (TabGet key)
              Write = fun key value -> bridge.Call(TabSet(key, value)) |> Async.Ignore
              Remove = fun key -> bridge.Call(TabRemove key) |> Async.Ignore }
          DeviceStorage =
            { Read = fun key -> read (DeviceGet key)
              Write = fun key value -> bridge.Call(DeviceSet(key, value)) |> Async.Ignore
              Remove = fun key -> bridge.Call(DeviceRemove key) |> Async.Ignore }
          Navigate = fun url -> bridge.Call(Leave url) |> Async.Ignore
          ReplaceAddress = fun url -> bridge.Call(ReplaceAddress url) |> Async.Ignore
          Broadcast =
            fun message ->
                bridge.Start(
                    async {
                        let! _ = bridge.Call(Announce message)
                        return []
                    }
                )
          Now = now
          RandomBytes = randomBytes }

    let changed change = [ IdentityChanged change ]

    let withClient (work: FidesClient -> Async<Msg list>) =
        match client with
        | Some fides -> bridge.Start(work fides)
        | None -> ()

    { Begin =
        fun config query ->
            let fides = FidesClient.create (configuration config) catalog (ports config.Exchange)
            client <- Some fides

            if isCallback query then
                bridge.Start(
                    async {
                        let! outcome = fides.CompleteCallback query
                        return changed (callbackChange outcome)
                    }
                )
            else
                bridge.Start(
                    async {
                        let! state = fides.Restore()
                        return changed (changeOf state)
                    }
                )
      SignIn =
        fun kept ->
            withClient (fun fides ->
                async {
                    match! fides.SignIn(retention kept) with
                    // The page is leaving for the provider; it returns with the callback.
                    | Ok() -> return changed IdentitySigningIn
                    | Error _ -> return changed (IdentitySignedOut(Some "sign_in_failed"))
                })
      SignOut =
        fun () ->
            withClient (fun fides ->
                async {
                    let! _ = fides.SignOut()
                    return changed (IdentitySignedOut(Some "signed_out"))
                })
      Receive =
        fun message ->
            withClient (fun fides ->
                async {
                    do! fides.Receive message
                    return changed (changeOf (fides.State()))
                })
      TokenProvider = fun () -> client |> Option.map Fides.Arca.TokenBridge.ofClient }

/// The port of a deployment without sign-in: nothing to do.
let none: IdentityPort =
    { Begin = fun _ _ -> ()
      SignIn = ignore
      SignOut = ignore
      Receive = ignore
      TokenProvider = fun () -> None }
