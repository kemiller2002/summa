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
/// Sign-in through Fides replaces it (WI-0035).
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

/// One kernel message for the accounting application (app/).
let dispatchAccounting (messageJson: string) =
    let next, reply = AccountingWire.handle aegis.Value (fun () -> System.DateTimeOffset.UtcNow) LocalActor accounting messageJson
    accounting <- next
    reply
