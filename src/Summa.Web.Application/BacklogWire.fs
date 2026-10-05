/// The work-backlog page's side of the Limen boundary: decodes the server's
/// bodies into the engine's types, maps kernel events onto engine messages,
/// and runs each step under the Aegis boundary.
module Summa.Web.Application.BacklogWire

open System.Text.Json.Nodes
open Aegis
open Summa.Web.Engine.Common
open Summa.Web.Engine.Backlog
open Summa.Web.Engine.BacklogPage
open Summa.Web.Application.Json
open Summa.Web.Application.Limen
open Summa.Web.Application.Boundary

// ---------------------------------------------------------------------------
// Response bodies.
// ---------------------------------------------------------------------------

let private attachment (path: string) (node: JsonNode) =
    let o = asObject path node

    { Id = required "id" path asString o
      Name = required "name" path asString o
      Size = optional "size" path asInt64 o |> Option.defaultValue 0L
      ContentType = optional "contentType" path asString o
      UploadedAt = optional "uploadedAt" path asString o |> Option.defaultValue "" }

let private liveWorkItem (path: string) (node: JsonNode) =
    let o = asObject path node

    { State = optional "state" path asString o |> Option.defaultValue ""
      SemanticState = optional "semanticState" path asString o |> Option.defaultValue ""
      AllowedActions = optional "allowedActions" path strings o |> Option.defaultValue [] }

/// One work row. `id`, `title` and `status` are the contract; the rest is
/// optional because a freshly created item does not carry it yet.
let workRow (path: string) (node: JsonNode) : WorkRow =
    let o = asObject path node

    { Id = required "id" path asString o
      Title = required "title" path asString o
      Description = optional "description" path asString o
      Tags = optional "tags" path strings o |> Option.defaultValue []
      Priority = optional "priority" path asString o
      Status = required "status" path asString o
      BacklogActions = optional "backlogActions" path strings o |> Option.defaultValue []
      Attachments =
        optional "attachments" path asArray o
        |> Option.defaultValue []
        |> List.mapi (fun index item -> attachment $"{path}.attachments[{index}]" item)
      LiveWorkItem = optional "liveWorkItem" path liveWorkItem o
      Detail = optional "detail" path asString o
      Raw = indented o }

let workRows (path: string) (node: JsonNode) =
    asArray path node |> List.mapi (fun index item -> workRow $"{path}[{index}]" item)

let repositoryStatus (path: string) (node: JsonNode) =
    let o = asObject path node
    let shown name = tryField name o |> Option.map display |> Option.defaultValue "unknown"

    { Repository = shown "repository"
      ProtocolVersion = shown "protocolVersion"
      Validation = shown "validation" }

let decoder (expect: Expect) (body: JsonNode option) =
    match expect with
    | ExpectRows -> Rows(Replies.body workRows body)
    | ExpectRow -> Row(Replies.body workRow body)
    | ExpectStatus -> Status(Replies.body repositoryStatus body)
    | ExpectAcknowledgement -> Acknowledged

// ---------------------------------------------------------------------------
// Kernel events: the `data-event` names web/index.html uses.
// ---------------------------------------------------------------------------

/// Every event the page may send, so a test can hold index.html to it.
let events: Map<string, string -> string -> Msg> =
    Map.ofList
        [ "tagFilterChanged", (fun _ value -> TagFilterChanged value)
          "statusFilterChanged", (fun _ value -> StatusFilterChanged value)
          "filtersCleared", (fun _ _ -> FiltersCleared)
          "addTitleChanged", (fun _ value -> AddTitleChanged value)
          "addTagsChanged", (fun _ value -> AddTagsChanged value)
          "addPriorityChanged", (fun _ value -> AddPriorityChanged value)
          "addDescriptionChanged", (fun _ value -> AddDescriptionChanged value)
          "addFileRowAdded", (fun _ _ -> AddFileRowAdded)
          "addFileNameChanged", (fun key value -> AddFileNameChanged(key, value))
          "addSubmitted", (fun _ _ -> AddSubmitted)
          "reasonChanged", (fun _ value -> ReasonChanged value)
          "reasonConfirmed", (fun _ _ -> ReasonConfirmed)
          "startTypeChanged", (fun _ value -> StartTypeChanged value)
          "startConfirmed", (fun _ _ -> StartConfirmed)
          "evidenceRowAdded", (fun _ _ -> EvidenceRowAdded)
          "evidenceTypeChanged", (fun key value -> EvidenceTypeChanged(key, value))
          "evidencePathChanged", (fun key value -> EvidencePathChanged(key, value))
          "conclusionChanged", (fun _ value -> ConclusionChanged value)
          "completeConfirmed", (fun _ _ -> CompleteConfirmed)
          "updateTitleChanged", (fun _ value -> UpdateTitleChanged value)
          "updateDescriptionChanged", (fun _ value -> UpdateDescriptionChanged value)
          "updateTagsChanged", (fun _ value -> UpdateTagsChanged value)
          "updatePriorityChanged", (fun _ value -> UpdatePriorityChanged value)
          "updateConfirmed", (fun _ _ -> UpdateConfirmed)
          "attachFileRowAdded", (fun _ _ -> AttachFileRowAdded)
          "attachFileNameChanged", (fun key value -> AttachFileNameChanged(key, value))
          "attachConfirmed", (fun _ _ -> AttachConfirmed) ]
    |> fun named ->
        List.init actionSlots id
        |> List.fold (fun map slot -> map |> Map.add $"rowAction{slot}" (fun key _ -> RowAction(slot, key))) named

let private message (name: string) (key: string option) (value: string option) =
    match events |> Map.tryFind name with
    | Some make -> make (defaultArg key "") (defaultArg value "")
    // index.html and the engine disagree: a defect, not an operational failure.
    | None -> invalidOp $"The backlog page sent an event the engine does not know: '{name}'"

// ---------------------------------------------------------------------------
// One kernel message in, one reply out.
// ---------------------------------------------------------------------------

type Session = { Page: Page; Fault: FaultView option }

let initial = { Page = initial; Fault = None }

let render (session: Session) (effects: Effect list) (handshake: Handshake option) =
    encode (view session.Page @ faultView session.Fault) effects handshake

let private replied (page: Page) correlation (reply: Expect -> Reply<Response>) =
    match expectation page correlation with
    // An answer to nothing still awaited (already answered or superseded).
    | None -> page, []
    | Some expect -> update (Replied(correlation, reply expect)) page

let private step (session: Session) (inbound: Inbound) =
    let page = session.Page

    let next, effects, handshake =
        match inbound with
        | Initialize offer ->
            let next, effects = update Started page
            next, effects, offer |> Option.map answer
        | Event(name, key, value) ->
            let next, effects = update (message name key value) page
            next, effects, None
        | HttpResult(correlation, outcome) ->
            let next, effects = replied page correlation (fun expect -> Replies.http (decoder expect) outcome)
            next, effects, None
        | CapabilityResult(correlation, capability, outcome) ->
            let next, effects =
                replied page correlation (fun expect -> Replies.capability (decoder expect) capability outcome)

            next, effects, None
        | CapabilityFact(capability, fact) when capability = files.Id ->
            match picked fact with
            | Some pick ->
                let next, effects = update (FilePicked(pick.Input, pick.Key, pick.File)) page
                next, effects, None
            | None -> page, [], None
        | CapabilityFact _
        | LocationChanged -> page, [], None

    let session = { session with Page = next }
    session, render session effects handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// page as it was and is shown until the next message.
let handle (aegis: AegisConfig) (session: Session) (messageJson: string) =
    let cleared = { session with Fault = None }

    match capture aegis "Summa.Web.Backlog.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { session with Fault = Some fault }
        faulted, render faulted [] None
