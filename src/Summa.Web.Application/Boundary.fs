/// The Aegis boundary of the Summa web engines.
///
/// Every kernel message crosses here: it is parsed, routed into a page's
/// engine, and the reply is written. *Unexpected operational* failure on that
/// path (a message or a server body that is not the shape the engine relies
/// on, a capability the kernel did not execute) is classified once, here,
/// into a stable SUMMA.BOUNDARY.* fault and presented through Forma's fault
/// component. *Expected* outcomes are not faults: a server refusal, a request
/// whose outcome is unknown, or a network failure stay typed `Refused` replies
/// that the page shows as an ordinary error. Programming defects keep Aegis's
/// fail-loud semantics (they are re-raised and surface as a Limen bridge
/// error), and are never disguised as recoverable faults.
///
/// The declared boundaries are in aegis-boundaries.json.
module Summa.Web.Application.Boundary

open System.Text.Json
open Aegis
open Summa.Web.Engine.Common
open Summa.Web.Application.Json

/// The kernel asked the engine to rely on something it did not do: a
/// capability request it did not execute, or a result for a request the
/// engine never made.
exception CapabilityFailed of capability: string * problem: string

[<Literal>]
let MessageInvalid = "SUMMA.BOUNDARY.MESSAGE_INVALID"

[<Literal>]
let ResponseInvalid = "SUMMA.BOUNDARY.RESPONSE_INVALID"

[<Literal>]
let CapabilityUnavailable = "SUMMA.BOUNDARY.CAPABILITY_UNAVAILABLE"

[<Literal>]
let Unexpected = "SUMMA.BOUNDARY.UNEXPECTED"

/// Aegis configured once for the application, and validated before it is
/// trusted. The sink is standard error, which the browser runtime routes to
/// the console; nothing is persisted (see aegis-boundaries.json).
let configure (sinks: Sinks.Sink list) =
    match Bootstrap.validate None (Aegis.configure "Summa" None sinks) with
    | Ok valid -> valid
    | Result.Error problems -> invalidOp $"Invalid Summa Aegis configuration: {problems}"

/// One central translation from an exception at the boundary to a fault.
let classify (aegis: AegisConfig) (scope: Scope) (ex: exn) =
    let code, category, message =
        match ex with
        | :? JsonException ->
            MessageInvalid,
            DataFailure,
            "Summa could not read a message from the page. Your current work is unchanged."
        | MalformedInput(path, _) when path.StartsWith "$.body" ->
            ResponseInvalid,
            IntegrationFailure,
            "The Summa server sent a response this page could not read. Nothing on this page was changed by it."
        | MalformedInput _ ->
            MessageInvalid,
            DataFailure,
            "Summa could not read a message from the page. Your current work is unchanged."
        | CapabilityFailed _ ->
            CapabilityUnavailable,
            IntegrationFailure,
            "This browser could not perform a file operation Summa needs. Your current work is unchanged."
        | _ ->
            Unexpected,
            IntegrationFailure,
            "Summa encountered an unexpected problem. Your current work is unchanged."

    Aegis.faultOf aegis scope (FaultCode code) category FaultSeverity.Error OperationOnly Transient Continue message ex

/// The fault as the page shows it: safe presentation only (title, message,
/// reference), never the exception or its details.
type FaultView =
    { Title: string
      Message: string
      Reference: string }

let present (fault: Fault) =
    let presentation = Presentation.present "Summa could not complete that operation" fault

    { Title = presentation.Title
      Message = presentation.Message
      Reference = presentation.Reference }

/// The fault's named values, bound by the Forma fault-inline component.
let faultView (fault: FaultView option) : View =
    [ "hasOperationalFault", Value(Flag fault.IsSome)
      "operationalFaultTitle", Value(Text(fault |> Option.map _.Title |> Option.defaultValue ""))
      "operationalFaultMessage", Value(Text(fault |> Option.map _.Message |> Option.defaultValue ""))
      "operationalFaultReference", Value(Text(fault |> Option.map _.Reference |> Option.defaultValue "")) ]

/// Runs one step of a page at the boundary: its result, or the presented
/// fault when it failed operationally. Programming defects and cancellation
/// are re-raised by Aegis, not returned.
let capture (aegis: AegisConfig) (operation: string) (step: unit -> 'result) : Result<'result, FaultView> =
    let scope = Aegis.scope aegis operation Map.empty

    Aegis.capture aegis scope (classify aegis) step |> Result.mapError present
