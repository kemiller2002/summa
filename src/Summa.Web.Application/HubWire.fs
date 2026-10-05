/// The project-administration hub page's side of the Limen boundary, in the
/// same shape as BacklogWire.
module Summa.Web.Application.HubWire

open System.Text.Json.Nodes
open Aegis
open Summa.Web.Engine.Common
open Summa.Web.Engine.Hub
open Summa.Web.Engine.HubPage
open Summa.Web.Application.Json
open Summa.Web.Application.Limen
open Summa.Web.Application.Boundary

let repoEntry (path: string) (node: JsonNode) =
    let o = asObject path node

    { Id = required "id" path asString o
      Name = required "name" path asString o
      Path = required "path" path asString o
      RegisteredAt = optional "registeredAt" path asString o |> Option.defaultValue "" }

/// One aggregated row: a spoke's item, or that spoke's error.
let aggregatedRow (path: string) (node: JsonNode) =
    let o = asObject path node

    { RepoId = required "repoId" path asString o
      RepoName = required "repoName" path asString o
      Id = optional "id" path asString o
      Title = optional "title" path asString o
      Status = optional "status" path asString o
      Tags = optional "tags" path strings o |> Option.defaultValue []
      Priority = optional "priority" path asString o
      Error = optional "error" path asString o }

let private listOf read (path: string) (node: JsonNode) =
    asArray path node |> List.mapi (fun index item -> read $"{path}[{index}]" item)

let decoder (expect: Expect) (body: JsonNode option) =
    match expect with
    | ExpectRepos -> Repos(Replies.body (listOf repoEntry) body)
    | ExpectWork -> Work(Replies.body (listOf aggregatedRow) body)
    | ExpectAcknowledgement -> Acknowledged

/// Every event the page may send, so a test can hold index.html to it.
let events: Map<string, string -> string -> Msg> =
    Map.ofList
        [ "registerPathChanged", (fun _ value -> RegisterPathChanged value)
          "registerNameChanged", (fun _ value -> RegisterNameChanged value)
          "registerSubmitted", (fun _ _ -> RegisterSubmitted)
          "unregisterRequested", (fun key _ -> UnregisterRequested key)
          "unregisterConfirmed", (fun _ _ -> UnregisterConfirmed)
          "createRepoChanged", (fun _ value -> CreateRepoChanged value)
          "createTitleChanged", (fun _ value -> CreateTitleChanged value)
          "createTagsChanged", (fun _ value -> CreateTagsChanged value)
          "createPriorityChanged", (fun _ value -> CreatePriorityChanged value)
          "createDescriptionChanged", (fun _ value -> CreateDescriptionChanged value)
          "createFileRowAdded", (fun _ _ -> CreateFileRowAdded)
          "createFileNameChanged", (fun key value -> CreateFileNameChanged(key, value))
          "createSubmitted", (fun _ _ -> CreateSubmitted)
          "repoFilterChanged", (fun _ value -> RepoFilterChanged value)
          "tagFilterChanged", (fun _ value -> TagFilterChanged value)
          "statusFilterChanged", (fun _ value -> StatusFilterChanged value)
          "filtersCleared", (fun _ _ -> FiltersCleared) ]

let private message (name: string) (key: string option) (value: string option) =
    match events |> Map.tryFind name with
    | Some make -> make (defaultArg key "") (defaultArg value "")
    | None -> invalidOp $"The hub page sent an event the engine does not know: '{name}'"

type Session = { Page: Page; Fault: FaultView option }

let initial = { Page = initial; Fault = None }

let render (session: Session) (effects: Effect list) (handshake: Handshake option) =
    encode (view session.Page @ faultView session.Fault) effects handshake

let private replied (page: Page) correlation (reply: Expect -> Reply<Response>) =
    match expectation page correlation with
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

let handle (aegis: AegisConfig) (session: Session) (messageJson: string) =
    let cleared = { session with Fault = None }

    match capture aegis "Summa.Web.Hub.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { session with Fault = Some fault }
        faulted, render faulted [] None
