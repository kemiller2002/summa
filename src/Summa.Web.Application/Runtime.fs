/// The composition root the WebAssembly shim calls into.
///
/// The shim cannot thread state between calls, so each page's session lives
/// here, behind one string-in/string-out function per page. Aegis is
/// configured once, for both pages, at first use.
module Summa.Web.Application.Runtime

open Aegis

let private aegis = lazy (Boundary.configure [ Sinks.standardError ])

let mutable private backlog = BacklogWire.initial
let mutable private hub = HubWire.initial

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
