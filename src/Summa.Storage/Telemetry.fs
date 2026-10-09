/// Operational telemetry for commands (SUM3-016, SUM3-017, SUM3-018): one
/// structured event per command run, with its correlation id, command,
/// actor, entities, source system, outcome and time. Financial correctness
/// problems (a refused transition, books that fail their checks) are told
/// apart from technical ones (storage, conflicts, unknown outcomes). The
/// event carries identifiers and outcomes only: no amounts, names,
/// addresses or free text the person typed.
///
/// Pure, apart from `observe`, which hands each event to a sink.
module Summa.Storage.Telemetry

open System
open Arca
open Summa.Storage.Commands

/// Which kind of problem, if any.
type Category =
    | Succeeded
    /// The books said no, or cannot be trusted: a financial correctness matter.
    | Financial
    /// The actor may not, or must ask a person first.
    | Access
    /// Storage, a conflict that would not settle, or an unknown outcome.
    | Technical

type CommandEvent =
    { At: DateTimeOffset
      CorrelationId: string
      Command: string
      Capability: string
      Actor: string
      ActorKind: string
      ExecutionId: string option
      SourceSystem: string option
      /// The records the command changed, by path.
      Entities: string list
      /// `committed`, `unchanged` (a duplicate or a retry), or why not.
      Outcome: string
      Category: Category
      /// How many times the command was decided.
      Attempts: int
      /// True when the same command had already been applied.
      Duplicate: bool }

let private failureOutcome (failure: CommandFailure<'e>) =
    match failure with
    | InvalidActor _ -> "invalid-actor", Access
    | NotAuthorized _ -> "not-authorized", Access
    | NeedsApproval _ -> "needs-approval", Access
    | Untrustworthy _ -> "untrustworthy-books", Financial
    | Incompatible _ -> "incompatible-data", Technical
    | Rejected _ -> "rejected-transition", Financial
    | Unstorable _ -> "unstorable", Financial
    | StillConflicted _ -> "still-conflicted", Technical
    | Unknown _ -> "outcome-unknown", Technical
    | StorageFailed _ -> "storage-failed", Technical

/// The event for one command run.
let eventOf (at: DateTimeOffset) (request: Request<'e>) (result: Result<Outcome, CommandFailure<'e>>) : CommandEvent =
    let actor = request.Actor

    let outcome, category, attempts, entities, duplicate =
        match result with
        | Ok o ->
            match o.Receipt with
            | Some receipt -> "committed", Succeeded, o.Attempts, receipt.Revisions |> Map.keys |> List.ofSeq |> List.sort, false
            | None -> "unchanged", Succeeded, o.Attempts, [], true
        | Error(StillConflicted n) -> "still-conflicted", Technical, n, [], false
        | Error failure ->
            let text, category = failureOutcome failure
            text, category, 1, [], false

    { At = at
      CorrelationId = actor.CorrelationId
      Command = request.Summary
      Capability = Summa.Access.Access.capabilityName request.Capability
      Actor = actor.ActorId
      ActorKind = Summa.Access.Access.kindName actor.Kind
      ExecutionId = actor.ExecutionId
      SourceSystem = actor.SourceSystem
      Entities = entities
      Outcome = outcome
      Category = category
      Attempts = attempts
      Duplicate = duplicate }

/// The event as one structured log line (JSON).
let toJson (e: CommandEvent) =
    let optional (v: string option) = v |> Option.map Json.String |> Option.defaultValue Json.Null

    Json.objectOf
        [ "timestamp", Json.String(Codec.preciseTimestamp e.At)
          "correlationId", Json.String e.CorrelationId
          "command", Json.String e.Command
          "capability", Json.String e.Capability
          "actor", Json.String e.Actor
          "actorKind", Json.String e.ActorKind
          "executionId", optional e.ExecutionId
          "sourceSystem", optional e.SourceSystem
          "entityIds", Json.Array(e.Entities |> List.map Json.String)
          "outcome", Json.String e.Outcome
          "category", Json.String((sprintf "%A" e.Category).ToLowerInvariant())
          "attempts", Codec.number e.Attempts
          "duplicate", Json.Bool e.Duplicate ]
    |> Json.canonicalText

/// Runs a command through `execute` and hands its event to `sink`.
let observe
    (sink: CommandEvent -> unit)
    (now: unit -> DateTimeOffset)
    (provider: StorageProvider)
    (gates: Set<Summa.Access.Access.Capability>)
    (ns: Namespace)
    (attempts: int)
    (request: Request<'e>)
    : Async<Result<Outcome, CommandFailure<'e>>> =
    async {
        let! result = execute provider gates ns attempts request
        sink (eventOf (now ()) request result)
        return result
    }
