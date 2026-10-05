/// Engine for the work-backlog page (web/): the page's state, its
/// transitions, and every decision about what to request and what to show.
///
/// Legality of work-item transitions is owned by the server (and the
/// ros_cli.mjs it calls); this engine only maps the actions the server says
/// are allowed onto the controls the page offers, and shows whatever error
/// the server returns rather than re-implementing its rules.
module Summa.Web.Engine.Backlog

open System
open System.Globalization
open Summa.Web.Engine.Common

type LiveWorkItem =
    { State: string
      SemanticState: string
      AllowedActions: string list }

type Attachment =
    { Id: string
      Name: string
      Size: int64
      ContentType: string option
      UploadedAt: string }

/// One row of `GET /api/work`, as the server sent it. `Raw` is that row's
/// JSON, indented, exactly as the detail panel's "Raw record" shows it.
type WorkRow =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string option
      Status: string
      BacklogActions: string list
      Attachments: Attachment list
      LiveWorkItem: LiveWorkItem option
      Detail: string option
      Raw: string }

type Filter = { Tag: string; Status: string }

/// The queue's state: what was loaded, how it is filtered, which row is
/// open, and the last request error.
type State =
    { Rows: WorkRow list
      Filter: Filter
      SelectedId: string option
      Error: string option }

let initialState =
    { Rows = []
      Filter = { Tag = ""; Status = "" }
      SelectedId = None
      Error = None }

// ---------------------------------------------------------------------------
// Transitions: state -> input -> state.
// ---------------------------------------------------------------------------

let rowsLoaded (rows: WorkRow list) (state: State) = { state with Rows = rows; Error = None }

let requestFailed (message: string) (state: State) = { state with Error = Some message }

let rowToggled (id: string) (state: State) =
    { state with SelectedId = if state.SelectedId = Some id then None else Some id }

let tagFilterChanged (tag: string) (state: State) = { state with Filter = { state.Filter with Tag = tag } }

let statusFilterChanged (status: string) (state: State) =
    { state with Filter = { state.Filter with Status = status } }

let filtersCleared (state: State) = { state with Filter = { Tag = ""; Status = "" } }

// ---------------------------------------------------------------------------
// Requests, described as data. The kernel performs them.
// ---------------------------------------------------------------------------

type Evidence = { Type: string; Path: string }

type UpdateInput =
    { Title: string
      Description: string
      Tags: string list
      Priority: string }

type AddInput = UpdateInput

type CompletionInput =
    { Evidence: Evidence list
      Conclusion: string option }

let buildQuery (filter: Filter) =
    queryString (
        tagPairs filter.Tag
        @ (if filter.Status <> "" then [ "status", filter.Status ] else [])
    )

let private item (id: string) (suffix: string) = $"/api/work/{encodeComponent id}/{suffix}"

let private tagsJson (tags: string list) = tags |> List.map JString |> JArray

module Requests =
    let list (filter: Filter) = { Method = Get; Path = "/api/work" + buildQuery filter; Json = None }

    let status = { Method = Get; Path = "/api/status"; Json = None }

    let add (input: AddInput) =
        { Method = Post
          Path = "/api/work"
          Json =
            Some(
                JObject
                    [ "title", JString input.Title
                      "tags", tagsJson input.Tags
                      "priority", JString input.Priority
                      "description", JString input.Description ]
            ) }

    let update (id: string) (input: UpdateInput) =
        { Method = Post
          Path = item id "update"
          Json =
            Some(
                JObject
                    [ "title", JString input.Title
                      "description", JString input.Description
                      "tags", tagsJson input.Tags
                      "priority", JString input.Priority ]
            ) }

    /// The files themselves travel as multipart parts (see `Upload`).
    let uploadAttachmentsPath (id: string) = item id "attachments"

    let ready (id: string) = { Method = Post; Path = item id "ready"; Json = None }

    let block (id: string) (reason: string) =
        { Method = Post; Path = item id "block"; Json = Some(JObject [ "reason", JString reason ]) }

    let abandon (id: string) (reason: string) =
        { Method = Post; Path = item id "abandon"; Json = Some(JObject [ "reason", JString reason ]) }

    let start (id: string) (workType: string) =
        { Method = Post; Path = item id "start"; Json = Some(JObject [ "type", JString workType ]) }

    let resume (id: string) = { Method = Post; Path = item id "resume"; Json = None }

    let complete (id: string) (input: CompletionInput) =
        { Method = Post
          Path = item id "complete"
          Json =
            Some(
                JObject
                    [ "evidence",
                      input.Evidence
                      |> List.map (fun e -> JObject [ "type", JString e.Type; "path", JString e.Path ])
                      |> JArray
                      "conclusion", (input.Conclusion |> Option.map JString |> Option.defaultValue JNull) ]
            ) }

// ---------------------------------------------------------------------------
// Decisions about input: what the page submits, given what the user typed.
// ---------------------------------------------------------------------------

type RawInput =
    { Title: string
      Description: string
      Tags: string
      Priority: string }

/// A new item, or None when there is nothing to submit (no title).
let addInput (raw: RawInput) : AddInput option =
    match raw.Title.Trim() with
    | "" -> None
    | title ->
        Some
            { Title = title
              Description = raw.Description.Trim()
              Tags = parseTags raw.Tags
              Priority = raw.Priority }

let updateDefaults (row: WorkRow) : RawInput =
    { Title = row.Title
      Description = row.Description |> Option.defaultValue ""
      Tags = String.Join(", ", row.Tags)
      Priority = row.Priority |> Option.defaultValue "medium" }

/// An edit of `row`; a title the page could not read keeps the row's own title.
let updateInput (title: string option) (raw: RawInput) (row: WorkRow) : UpdateInput =
    { Title = title |> Option.map _.Trim() |> Option.defaultValue row.Title
      Description = raw.Description.Trim()
      Tags = parseTags raw.Tags
      Priority = raw.Priority }

let completionInput (rows: Evidence list) (conclusion: string) =
    { Evidence =
        rows
        |> List.map (fun row -> { Type = row.Type.Trim(); Path = row.Path.Trim() })
        |> List.filter (fun row -> row.Type <> "" && row.Path <> "")
      Conclusion =
        match conclusion.Trim() with
        | "" -> None
        | trimmed -> Some trimmed }

// ---------------------------------------------------------------------------
// Projection: what the page shows.
// ---------------------------------------------------------------------------

type RowActionKind =
    | Show
    | Ready
    | Block
    | Start
    | Abandon
    | Resume
    | Complete
    | Edit
    | Attach

type RowAction = { Kind: RowActionKind; Label: string }

/// The controls a row offers, in order: live-work actions when it is in
/// flight, backlog actions otherwise, between Show and Edit/Attach.
let rowActions (row: WorkRow) =
    let offered =
        match row.LiveWorkItem with
        | Some live ->
            live.AllowedActions
            |> List.choose (function
                | "block" -> Some { Kind = Block; Label = "Block" }
                | "resume" -> Some { Kind = Resume; Label = "Resume" }
                | "complete" -> Some { Kind = Complete; Label = "Complete" }
                | _ -> None)
        | None ->
            row.BacklogActions
            |> List.choose (function
                | "ready" -> Some { Kind = Ready; Label = "Mark ready" }
                | "block" -> Some { Kind = Block; Label = "Block" }
                | "start" -> Some { Kind = Start; Label = "Start" }
                | "abandon" -> Some { Kind = Abandon; Label = "Abandon" }
                | _ -> None)

    [ { Kind = Show; Label = "Show" } ]
    @ offered
    @ [ { Kind = Edit; Label = "Edit" }; { Kind = Attach; Label = "Attach" } ]

/// The most actions any row can offer: Show, four backlog actions, Edit, Attach.
let actionSlots = 7

let formatSize (bytes: int64) =
    let fixed1 (value: float) = value.ToString("F1", CultureInfo.InvariantCulture)

    if bytes < 1024L then $"{bytes} B"
    elif bytes < 1024L * 1024L then fixed1 (float bytes / 1024.0) + " KB"
    else fixed1 (float bytes / (1024.0 * 1024.0)) + " MB"

type AttachmentView =
    { Key: string
      Href: string
      Name: string
      SizeLabel: string }

type DetailView =
    { Title: string
      Description: string
      Attachments: AttachmentView list
      Raw: string }

/// The detail panel for the selected row, or None when nothing is selected.
let detailView (state: State) =
    state.SelectedId
    |> Option.bind (fun id -> state.Rows |> List.tryFind (fun row -> row.Id = id))
    |> Option.map (fun row ->
        { Title = $"{row.Id} · {row.Title}"
          Description =
            match row.Description with
            | Some text when text <> "" -> text
            | _ -> "No description."
          Attachments =
            row.Attachments
            |> List.map (fun attachment ->
                { Key = attachment.Id
                  Href = $"/api/work/{encodeComponent row.Id}/attachments/{encodeComponent attachment.Id}"
                  Name = attachment.Name
                  SizeLabel = $" ({formatSize attachment.Size})" })
          Raw =
            match row.Detail with
            | Some detail when detail <> "" -> $"{row.Raw}\n\n{detail}"
            | _ -> row.Raw })

type RepositoryStatus =
    { Repository: string
      ProtocolVersion: string
      Validation: string }

let repositoryLabel (status: RepositoryStatus) =
    $"{status.Repository} · protocol {status.ProtocolVersion} · validation {status.Validation}"
