/// The composition root the WebAssembly shim calls into.
///
/// The shim cannot thread state between calls, so each page's session lives
/// here, behind one string-in/string-out function per page. Aegis is
/// configured once, for every page, at first use. The accounting
/// application's clock is read here, the one place with effects.
module Summa.Web.Application.Runtime

open Aegis

let private aegis = lazy (Boundary.configure [ Sinks.standardError ])

let mutable private backlog = BacklogWire.initial
let mutable private hub = HubWire.initial
let mutable private accounting = AccountingWire.initial

/// Who acts in a deployment without sign-in: one person, in this browser.
/// Where sign-in is required, the signed-in person acts (WI-0035).
[<Literal>]
let LocalActor = "local-person"

/// One kernel message for the work-backlog page (web/).
let dispatchBacklog (messageJson: string) =
    let next, reply = BacklogWire.handle aegis.Value backlog messageJson
    backlog <- next
    reply

/// One kernel message for the project-administration hub page (web-hub/).
let dispatchHub (messageJson: string) =
    let next, reply = HubWire.handle aegis.Value hub messageJson
    hub <- next
    reply

let private bridge = Bridge.Bridge()

let private now () = System.DateTimeOffset.UtcNow

/// The wire's world: the clock, the browser's cryptographic random source
/// for Fides' client, and the page's bridge.
let private identity = Identity.create bridge now System.Security.Cryptography.RandomNumberGenerator.GetBytes

let private newKey () = $"summa-{System.Guid.NewGuid():N}"

let private accountingEnv: AccountingWire.Env =
    { Now = now
      LocalActor = LocalActor
      Bridge = bridge
      Identity = identity
      // Arca's GitHub adapter through the bridge, with Fides' token provider;
      // used only when the deployment names a location on GitHub (WI-0037).
      Store = Store.arca bridge (Store.gitHub bridge identity.TokenProvider) now newKey }

/// One kernel message for the accounting application (app/).
let dispatchAccounting (messageJson: string) =
    let next, reply = AccountingWire.handle aegis.Value accountingEnv accounting messageJson
    accounting <- next
    reply
