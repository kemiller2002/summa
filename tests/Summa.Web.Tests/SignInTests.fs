/// Sign-in through Fides (WI-0035): the identity comes from GitHub, never
/// typed (SUM0-003, SUM0-004); tokens stay out of everything Summa shows,
/// stores or sends, and signing out clears them (SUM3-012). The engine's
/// sign-in states, and the real Fides client driven through the Limen
/// boundary against a fake browser and a fake exchange that speaks Fides'
/// wire protocol.
module Summa.Web.Tests.SignInTests

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
open Summa.Web.Application

module Wire = Summa.Web.Application.AccountingWire

/// A deployment whose books live on GitHub, signing in through a Fides exchange.
let private gitHubConfiguration =
    """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"summa-data","branch":"main","basePath":"deployments/test"},"identity":{"exchange":"https://fides.test","application":"summa-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://summa.example/app/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","defaultCurrency":"USD","timeZone":"America/New_York","administrators":["583231"]}]}"""

let private localConfiguration = """{"environment":"local","environmentName":"local development"}"""

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.Zero)
let private ctx = { Now = start; Actor = "local-person" }

let private page (hash: string) (query: string) : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = query
      Hash = hash }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | _ -> failwith $"no scalar view value {name}"

let private opened (hash: string) (query: string) =
    run [ Started(page hash query); ConfigurationRead(Ok gitHubConfiguration) ] initial

// ---- The engine's sign-in states ------------------------------------------------------

[<Fact>]
let ``books on GitHub ask who is signed in first, and show nothing to anyone before`` () =
    let model, effects = opened "#/invoices/INV-0001" ""

    match effects with
    | [ LoadConfiguration; BeginIdentity(identity, []); Navigate _ ] -> Assert.Equal("https://fides.test", identity.Exchange)
    | other -> failwith $"%A{other}"

    Assert.Equal("True", value "isRestoringSession" model)
    Assert.Equal("False", value "isReady" model)
    Assert.Equal("False", value "isLoading" model)
    // The link asked for is kept in the sign-in address (SUM-LINK-008).
    Assert.Equal(Some "/sign-in?returnTo=%2Finvoices%2FINV-0001", model.Router.Current)

    let model, _ = run [ IdentityChanged(IdentitySignedOut None) ] model
    Assert.Equal("True", value "needsSignIn" model)
    Assert.Equal("False", value "hasSignInNotice" model)
    // Session-only retention is the default; the person may keep it for the tab.
    Assert.Equal("False", value "keepInTab" model)
    let model, effects = run [ KeepSignInToggled; SignInRequested ] model
    Assert.Equal<AppEffect list>([ StartSignIn ThisTab ], effects)
    Assert.Equal("True", value "isLeavingForProvider" model)
    // A second press while the first is under way does nothing.
    Assert.Empty(run [ SignInRequested ] model |> snd)

[<Fact>]
let ``the provider's callback is completed, and the identity is GitHub's numeric id`` () =
    let model, effects = opened "" "?code=abc&state=xyz"

    match effects with
    | LoadConfiguration :: BeginIdentity(_, query) :: _ -> Assert.Equal<(string * string) list>([ "code", "abc"; "state", "xyz" ], query)
    | other -> failwith $"%A{other}"

    let model, _ = run [ IdentityChanged(IdentitySignedIn("github", "583231", "octocat")) ] model
    Assert.Equal(SignedInAs { ActorId = "github:583231"; Login = "octocat" }, model.SignIn)
    Assert.Equal("True", value "isSignedIn" model)
    Assert.Equal("octocat", value "signedInLogin" model)
    // The GitHub store opens the books (WI-0037); until then the page says so.
    Assert.Equal("True", value "awaitsGitHubStore" model)

[<Fact>]
let ``signing out leaves nothing of the person in the page`` () =
    let model, _ = opened "#/customers" "" |> fst |> run [ IdentityChanged(IdentitySignedIn("github", "583231", "octocat")) ]
    Assert.Equal(Some "/customers", model.Router.Current)
    let model, effects = run [ SignOutRequested ] model
    Assert.Equal<AppEffect list>([ EndSignIn ], effects)

    let model, _ = run [ IdentityChanged(IdentitySignedOut(Some "signed_out")) ] model
    Assert.Equal(SignedOut(Some(signInNotice "signed_out")), model.SignIn)
    Assert.Equal("True", value "needsSignIn" model)
    Assert.Contains("no longer holds your GitHub token", value "signInNotice" model)
    Assert.Equal(None, model.Books)
    Assert.Equal(Some "/sign-in?returnTo=%2Fcustomers", model.Router.Current)

[<Fact>]
let ``sign-in outcomes are told in words, and an unreachable provider keeps a session`` () =
    for code in [ "state_invalid"; "state_expired"; "provider_denied"; "expired"; "revoked"; "code_rejected"; "something_new" ] do
        let model, _ = opened "" "" |> fst |> run [ IdentityChanged(IdentitySignedOut(Some code)) ]
        Assert.Equal("True", value "hasSignInNotice" model)
        Assert.Equal(signInNotice code, value "signInNotice" model)

    Assert.Contains("(something_new)", signInNotice "something_new")

    let signedIn, _ = opened "" "" |> fst |> run [ IdentityChanged(IdentitySignedIn("github", "583231", "octocat")) ]
    let still, _ = run [ IdentityChanged IdentityProviderUnavailable ] signedIn
    Assert.Equal(signedIn.SignIn, still.SignIn)
    let before, _ = opened "" "" |> fst |> run [ IdentityChanged IdentityProviderUnavailable ]
    Assert.Equal("True", value "needsSignIn" before)
    Assert.Contains("not reachable", value "signInNotice" before)

[<Fact>]
let ``local books need no sign-in, and Fides' messages do not apply to them`` () =
    let model, effects = run [ Started(page "" ""); ConfigurationRead(Ok localConfiguration) ] initial
    Assert.DoesNotContain(effects, (function BeginIdentity _ -> true | _ -> false))
    Assert.Equal(NotRequired, model.SignIn)
    let unchanged, _ = run [ IdentityChanged(IdentitySignedIn("github", "1", "someone")) ] model
    Assert.Equal(NotRequired, unchanged.SignIn)
    Assert.Empty(run [ SignInRequested; SignOutRequested ] model |> snd)

[<Fact>]
let ``a location without a way to sign in is not a usable deployment`` () =
    let without = gitHubConfiguration.Replace(""","identity":{"exchange":"https://fides.test","application":"summa-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://summa.example/app/"}""", "")
    let model, effects = run [ Started(page "" ""); ConfigurationRead(Ok without) ] initial
    Assert.Equal("True", value "isMisconfigured" model)
    Assert.DoesNotContain(effects, (function BeginIdentity _ -> true | _ -> false))

// ---- The real Fides client through the Limen boundary ---------------------------------

/// An access token no one could mistake for anything else, to look for.
let private accessToken = "gho_SUMMATESTACCESSTOKEN0123456789"
let private refreshToken = "ghr_SUMMATESTREFRESHTOKEN0123456789"

/// A browser tab and the exchange behind it: answers every request the
/// application makes, recording what it was asked.
type private Browser() =
    member val Tab = Dictionary<string, string>()
    member val Device = Dictionary<string, string>()
    member val Posts = List<string * string>()
    member val Left = List<string>()
    member val Replaced = List<string>()
    member val Broadcasts = List<string>()
    member val Configuration = gitHubConfiguration with get, set

    /// Fides' exchange (EXCHANGE-PROTOCOL.md section 2), for any code.
    member this.Exchange (url: string) (body: string) =
        this.Posts.Add(url, body)
        let at (offset: TimeSpan) = start.UtcDateTime.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssZ")

        match url with
        | "https://fides.test/v1/token" when body.Contains "\"code\":\"good-code\"" ->
            200,
            String.concat
                ""
                [ "{\"accessToken\":\""
                  accessToken
                  "\",\"accessTokenExpiresAt\":\""
                  at (TimeSpan.FromHours 8.0)
                  "\",\"refreshToken\":\""
                  refreshToken
                  "\",\"refreshTokenExpiresAt\":\""
                  at (TimeSpan.FromDays 180.0)
                  "\",\"identity\":{\"provider\":\"github\",\"subject\":\"583231\",\"login\":\"octocat\",\"name\":\"The Octocat\"}}" ]
        | "https://fides.test/v1/token" -> 400, """{"error":"code_rejected"}"""
        | "https://fides.test/v1/revoke" -> 204, ""
        | _ -> 404, """{"error":"not_found"}"""

let private aegis = Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

let private offer =
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{{"id":"summa.host","version":1,"fingerprint":"{Wire.host.Fingerprint}"}}]}}"""

let private initialize (query: string) (hash: string) =
    $"""{{"kind":"Initialize","location":{{"origin":"https://summa.example","path":"/app/","query":"{query}","hash":"{hash}"}},"handshake":{offer}}}"""

let private json (value: string) = Text.Json.JsonSerializer.Serialize value

let private result (id: string) (kind: string) (outcome: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"{kind}","correlationId":"{id}","outcome":{outcome}}}}}"""

let private hostResult (id: string) (value: string option) =
    let result =
        match value with
        | Some v -> $"""{{"kind":"Value","value":{json v}}}"""
        | None -> """{"kind":"Done"}"""

    $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"summa.host","version":1,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""

[<NoComparison; NoEquality>]
type private Pumped = { Session: Wire.Session; Replies: string list }

let private text (node: JsonNode | null) =
    match node with
    | null -> failwith "missing"
    | n -> n.GetValue<string>()

/// Sends a message, then answers every request the replies make, as the
/// browser and the exchange would, until nothing is left to answer.
let private pump (env: Wire.Env) (browser: Browser) (session: Wire.Session) (message: string) =
    let rec loop (session: Wire.Session) (queue: string list) (replies: string list) =
        match queue with
        | [] -> { Session = session; Replies = replies }
        | message :: rest ->
            let session, reply = Wire.handle aegis env session message
            let parsed = JsonNode.Parse reply |> Option.ofObj |> Option.get

            let effects =
                match parsed["effects"] with
                | null -> []
                | effects -> effects.AsArray() |> Seq.choose Option.ofObj |> List.ofSeq

            let answers =
                effects
                |> List.choose (fun effect ->
                    let id = text effect["correlationId"]

                    match text effect["kind"] with
                    | "Http" when text effect["method"] = "GET" ->
                        Some(result id "HttpResult" $"""{{"kind":"Success","status":200,"body":{json browser.Configuration}}}""")
                    | "Http" ->
                        let status, body = browser.Exchange (text effect["url"]) (text effect["body"])
                        Some(result id "HttpResult" $"""{{"kind":"Success","status":{status},"body":{json body}}}""")
                    | "Storage" ->
                        let key = text effect["key"]

                        match text effect["operation"] with
                        | "get" ->
                            match browser.Device.TryGetValue key with
                            | true, v -> Some(result id "StorageResult" $"""{{"kind":"Success","value":{json v}}}""")
                            | _ -> Some(result id "StorageResult" """{"kind":"Success"}""")
                        | "set" ->
                            browser.Device[key] <- text effect["value"]
                            Some(result id "StorageResult" """{"kind":"Success"}""")
                        | _ ->
                            browser.Device.Remove key |> ignore
                            Some(result id "StorageResult" """{"kind":"Success"}""")
                    | "Capability" when text effect["capability"] = "summa.host" ->
                        let request = effect["request"]
                        let field (name: string) = match request with null -> "" | r -> text r.[name]

                        match field "operation" with
                        | "tabGet" ->
                            match browser.Tab.TryGetValue(field "key") with
                            | true, v -> Some(hostResult id (Some v))
                            | _ -> Some(hostResult id None)
                        | "tabSet" ->
                            browser.Tab[field "key"] <- field "value"
                            Some(hostResult id None)
                        | "tabRemove" ->
                            browser.Tab.Remove(field "key") |> ignore
                            Some(hostResult id None)
                        | "leave" ->
                            browser.Left.Add(field "url")
                            Some(hostResult id None)
                        | "replaceAddress" ->
                            browser.Replaced.Add(field "url")
                            Some(hostResult id None)
                        | _ ->
                            browser.Broadcasts.Add(field "message")
                            Some(hostResult id None)
                    | _ -> None)

            loop session (rest @ answers) (replies @ [ reply ])

    loop session [ message ] []

let private viewOf (pumped: Pumped) =
    match (JsonNode.Parse(List.last pumped.Replies) |> Option.ofObj |> Option.get)["view"] with
    | null -> failwith "no view"
    | v -> v

let private viewFlag (key: string) (pumped: Pumped) = match (viewOf pumped)[key] with null -> failwith key | v -> v.GetValue<bool>()
let private viewText (key: string) (pumped: Pumped) = text (viewOf pumped).[key]

let private event (name: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}"}}}}"""

let private envFor () : Wire.Env =
    let counter = ref 0uy
    let bridge = Bridge.Bridge()

    { Now = fun () -> start
      LocalActor = "local-person"
      Bridge = bridge
      // Deterministic, distinct bytes: the test's stand-in for crypto.getRandomValues.
      Identity =
        Identity.create
            bridge
            (fun () -> start)
            (fun count ->
                Array.init count (fun _ ->
                    counter.Value <- counter.Value + 1uy
                    counter.Value)) }

/// Signs in end to end: the page leaves for GitHub, GitHub sends it back with
/// a code, a new page load completes the callback.
let private signIn (browser: Browser) (keepInTab: bool) =
    let first = envFor ()
    let opened = pump first browser Wire.initial (initialize "" "#/invoices")
    Assert.True(viewFlag "needsSignIn" opened)
    let chosen = if keepInTab then pump first browser opened.Session (event "keepSignInToggled") else opened
    let leaving = pump first browser chosen.Session (event "signInRequested")
    Assert.True(viewFlag "isLeavingForProvider" leaving)

    let authorize = Uri(Seq.exactlyOne browser.Left)
    Assert.Equal("github.com", authorize.Host)
    let query = queryPairs authorize.Query |> Map.ofList
    Assert.Equal("Iv23liTEST", query["client_id"])
    Assert.Equal("S256", query["code_challenge_method"])
    Assert.Equal("https://summa.example/app/", query["redirect_uri"])

    // GitHub redirects back; this is a new page load in the same tab.
    let state = Uri.EscapeDataString query["state"]
    let second = envFor ()
    pump second browser Wire.initial (initialize $"?code=good-code&state={state}" ""), second

[<Fact>]
let ``a person signs in with GitHub, and Summa acts as the identity GitHub resolved`` () =
    let browser = Browser()
    let page, env = signIn browser false

    Assert.True(viewFlag "isSignedIn" page)
    Assert.Equal("octocat", viewText "signedInLogin" page)
    Assert.Equal(SignedInAs { ActorId = "github:583231"; Login = "octocat" }, page.Session.Model.SignIn)
    // The code was exchanged once, through the configured exchange only.
    Assert.Equal<string list>([ "https://fides.test/v1/token" ], browser.Posts |> Seq.map fst |> List.ofSeq)
    // The callback's code and state were removed from the address bar.
    Assert.Equal<string list>([ "https://summa.example/app/" ], List.ofSeq browser.Replaced)

    // Arca gets a token provider, not a token.
    match env.Identity.TokenProvider() with
    | Some provider ->
        match provider () |> Async.RunSynchronously with
        | Ok token -> Assert.Equal(("Authorization", $"Bearer {accessToken}"), Arca.AccessToken.authorization token)
        | Error reason -> failwith $"{reason}"
    | None -> failwith "no token provider"

[<Fact>]
let ``the token is never shown, and with this-page retention it is never stored`` () =
    let browser = Browser()
    let page, _ = signIn browser false

    for reply in page.Replies do
        Assert.DoesNotContain(accessToken, reply)
        Assert.DoesNotContain(refreshToken, reply)

    let stored = Seq.append browser.Tab.Values browser.Device.Values |> String.concat "\n"
    Assert.DoesNotContain(accessToken, stored)
    Assert.DoesNotContain(refreshToken, stored)
    Assert.All(browser.Broadcasts, fun message -> Assert.DoesNotContain("gho_", message))

[<Fact>]
let ``signing out clears the token from the tab and revokes it at GitHub`` () =
    let browser = Browser()
    let page, env = signIn browser true

    // Kept for the tab, as chosen.
    Assert.Contains(browser.Tab.Values, fun value -> value.Contains accessToken)

    let out = pump env browser page.Session (event "signOutRequested")
    Assert.True(viewFlag "needsSignIn" out)
    Assert.Contains("no longer holds your GitHub token", viewText "signInNotice" out)
    Assert.Equal("https://fides.test/v1/revoke", fst (Seq.last browser.Posts))
    Assert.DoesNotContain(browser.Tab.Values, fun value -> value.Contains accessToken)

    match env.Identity.TokenProvider() with
    | Some provider -> Assert.True(provider () |> Async.RunSynchronously |> Result.isError)
    | None -> failwith "no token provider"

[<Fact>]
let ``a session kept for the tab is restored on reload; a refused or forged callback is a notice`` () =
    let browser = Browser()
    signIn browser true |> ignore

    let reloaded = pump (envFor ()) browser Wire.initial (initialize "" "")
    Assert.True(viewFlag "isSignedIn" reloaded)
    Assert.Equal("octocat", viewText "signedInLogin" reloaded)

    // A fresh tab, and a callback whose code the provider refuses.
    let other = Browser()
    let otherEnv = envFor ()
    let opened = pump otherEnv other Wire.initial (initialize "" "")
    pump otherEnv other opened.Session (event "signInRequested") |> ignore
    let state = (queryPairs (Uri(Seq.exactlyOne other.Left)).Query |> Map.ofList)["state"]
    let refused = pump (envFor ()) other Wire.initial (initialize $"?code=bad-code&state={state}" "")
    Assert.True(viewFlag "needsSignIn" refused)
    Assert.Equal(signInNotice "code_rejected", viewText "signInNotice" refused)

    // A callback that did not start in this tab is refused without calling the exchange.
    let posts = other.Posts.Count
    let forged = pump (envFor ()) other Wire.initial (initialize "?code=good-code&state=forged" "")
    Assert.Equal(signInNotice "state_invalid", viewText "signInNotice" forged)
    Assert.Equal(posts, other.Posts.Count)

[<Fact>]
let ``without the host pack the page cannot leave for GitHub, and says so instead of failing`` () =
    let browser = Browser()
    let bare = (initialize "" "").Replace($""",{{"id":"summa.host","version":1,"fingerprint":"{Wire.host.Fingerprint}"}}""", "").Replace($"""{{"id":"summa.host","version":1,"fingerprint":"{Wire.host.Fingerprint}"}}""", "")
    let env = envFor ()
    let opened = pump env browser Wire.initial bare
    Assert.True(viewFlag "needsSignIn" opened)
    let pressed = pump env browser opened.Session (event "signInRequested")
    Assert.Empty(browser.Left)
    Assert.False(viewFlag "isSignedIn" pressed)
