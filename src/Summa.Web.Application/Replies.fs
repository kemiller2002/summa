/// Turns what the kernel observed about a request into the typed reply an
/// engine understands.
///
/// The split is the one the shared foundation requirements draw: a refusal
/// from the server, a request with no answer, or one whose outcome is unknown
/// is an *expected* outcome and becomes `Refused` with the message the page
/// shows. A success whose body is not the agreed shape, or a capability the
/// kernel did not execute, is *unexpected* and raises, so Aegis classifies it
/// at the boundary (Boundary.fs).
module Summa.Web.Application.Replies

open System.Text.Json.Nodes
open Summa.Web.Engine.Common
open Summa.Web.Application.Json
open Summa.Web.Application.Limen
open Summa.Web.Application.Boundary

/// The server's `error` field, shown as JavaScript's `String(body.error)` would.
let errorField (body: JsonNode option) =
    match body with
    | Some(:? JsonObject as o) when o.ContainsKey "error" ->
        match Option.ofObj o["error"] with
        | Some value -> Some(display value)
        | None -> Some "null"
    | _ -> None

let private succeeded status = status >= 200 && status < 300

[<Literal>]
let private Unreachable = "request failed (the server could not be reached)"

[<Literal>]
let private Unknown =
    "The request was sent but no answer came back, so it may or may not have been applied. Reload the page to check."

let private notDelivered (reason: string) (status: int option) =
    match status with
    | Some status when succeeded status ->
        // The server said yes but its body would not decode: a broken contract.
        raise (MalformedInput("$.body", $"a readable JSON body ({reason})"))
    | Some status -> Refused(responseError status None)
    | None when reason = "network" -> Refused Unreachable
    | None -> Refused $"request failed ({reason})"

/// The reply to a core Http effect.
let http (decode: JsonNode option -> 'response) (outcome: HttpOutcome) : Reply<'response> =
    match outcome with
    | Responded(status, body) when succeeded status -> Answered(decode body)
    | Responded(status, body) -> Refused(responseError status (errorField body))
    | NotDelivered(reason, status) -> notDelivered reason status
    | Cancelled -> Refused "request cancelled"
    | OutcomeUnknown _ -> Refused Unknown

/// The reply to a transfer-pack upload or a files-pack release.
let capability (decode: JsonNode option -> 'response) (capabilityId: string) (outcome: CapabilityOutcome) : Reply<'response> =
    match outcome with
    | NotExecuted why -> raise (CapabilityFailed(capabilityId, why))
    | Completed _ when capabilityId = files.Id ->
        // Released, or already stale: either way the id is gone.
        Answered(decode None)
    | Completed result when capabilityId = transfer.Id ->
        let path = "$.result"

        match required "kind" path asString result with
        | "Success" -> http decode (Responded(required "status" path asInt result, tryField "body" result))
        | "Failure" ->
            match required "reason" path asString result with
            | "unknown-file"
            | "no-files" as reason -> raise (CapabilityFailed(capabilityId, reason))
            | reason -> notDelivered reason (optional "status" path asInt result)
        | "Cancelled" -> Refused "upload cancelled"
        | "OutcomeUnknown" -> Refused Unknown
        | "InvalidRequest" ->
            raise (CapabilityFailed(capabilityId, optional "problem" path asString result |> Option.defaultValue "invalid request"))
        | other -> raise (MalformedInput($"{path}.kind", $"a transfer result, not '{other}'"))
    | Completed _ -> raise (CapabilityFailed(capabilityId, "a result for a capability the engine never requested"))

/// The body of a successful reply, required to be present.
let body (decode: string -> JsonNode -> 'a) (node: JsonNode option) =
    match node with
    | Some value -> decode "$.body" value
    | None -> raise (MalformedInput("$.body", "a JSON body"))
