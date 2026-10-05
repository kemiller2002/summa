/// The work-backlog page as a state machine: `update` takes the page and one
/// input (a user event, a picked file, or the reply to an earlier request)
/// and returns the next page plus the effects to perform. `view` projects the
/// page into the named values index.html binds to.
///
/// Pure: the clock, the network and the DOM are all on the other side of
/// Limen's boundary. Correlation ids and row keys come from a counter the page
/// threads, so a given sequence of inputs always produces the same output.
module Summa.Web.Engine.BacklogPage

open System
open Summa.Web.Engine.Common
open Summa.Web.Engine.Backlog

type ReasonKind =
    | Blocking
    | Abandoning

/// Which dialog the user last opened, and for which item. The dialog itself
/// is a native `<dialog>` the browser opens and closes; this is what a
/// confirmation in it applies to.
type Dialog =
    | NoDialog
    | ReasonFor of ReasonKind * id: string
    | StartFor of id: string
    | CompleteFor of id: string
    | UpdateFor of WorkRow
    | AttachFor of id: string

type EvidenceRow = { Key: string; Type: string; Path: string }

/// What an outstanding request was for. Results arrive asynchronously and
/// carry only their correlation id, so this is how a reply finds its meaning.
type Pending =
    | Listing
    | LoadingStatus
    /// A transition or edit; the queue is reloaded once it is accepted.
    | Mutating
    /// The capture form's create; its picked files follow once the id is known.
    | Creating of uploads: (SelectedFile * string) list
    /// The capture form's attachments, uploaded after its create.
    | UploadingCreated
    | Attaching
    | Releasing

/// The shape a reply's body must have. The application tier decodes the
/// server's JSON into it; a body that does not decode is an operational
/// failure (Aegis), never a silent empty list.
type Expect =
    | ExpectRows
    | ExpectRow
    | ExpectStatus
    | ExpectAcknowledgement

type Response =
    | Rows of WorkRow list
    | Row of WorkRow
    | Status of RepositoryStatus
    | Acknowledged

type Page =
    { Work: State
      RepositoryLabel: string
      Add: RawInput
      AddFiles: FileRow list
      Dialog: Dialog
      ReasonTitle: string
      Reason: string
      StartType: string
      Evidence: EvidenceRow list
      Conclusion: string
      Update: RawInput
      AttachFiles: FileRow list
      Awaiting: Map<Correlation, Pending>
      /// Only the newest list request may replace the rows: an older one
      /// answering late would otherwise show a filter the user has left.
      LatestList: Correlation option
      Counter: int }

type Msg =
    | Started
    | TagFilterChanged of string
    | StatusFilterChanged of string
    | FiltersCleared
    | AddTitleChanged of string
    | AddTagsChanged of string
    | AddPriorityChanged of string
    | AddDescriptionChanged of string
    | AddFileRowAdded
    | AddFileNameChanged of key: string * string
    | AddSubmitted
    | FilePicked of input: string * key: string * SelectedFile option
    | RowAction of slot: int * id: string
    | ReasonChanged of string
    | ReasonConfirmed
    | StartTypeChanged of string
    | StartConfirmed
    | EvidenceRowAdded
    | EvidenceTypeChanged of key: string * string
    | EvidencePathChanged of key: string * string
    | ConclusionChanged of string
    | CompleteConfirmed
    | UpdateTitleChanged of string
    | UpdateDescriptionChanged of string
    | UpdateTagsChanged of string
    | UpdatePriorityChanged of string
    | UpdateConfirmed
    | AttachFileRowAdded
    | AttachFileNameChanged of key: string * string
    | AttachConfirmed
    | Replied of Correlation * Reply<Response>

/// The `data-files-input` names of the page's two kinds of upload row.
[<Literal>]
let AddFileInput = "add-file"

[<Literal>]
let AttachFileInput = "attach-file"

let private emptyAdd =
    { Title = ""
      Description = ""
      Tags = ""
      Priority = "medium" }

let initial =
    { Work = initialState
      RepositoryLabel = ""
      Add = emptyAdd
      AddFiles = [ emptyFileRow "file-0" ]
      Dialog = NoDialog
      ReasonTitle = "Reason"
      Reason = ""
      StartType = "feature"
      Evidence = []
      Conclusion = ""
      Update = emptyAdd
      AttachFiles = []
      Awaiting = Map.empty
      LatestList = None
      Counter = 1 }

/// What the reply to `correlation` must decode to, if it is still awaited.
let expectation (page: Page) (correlation: Correlation) =
    page.Awaiting
    |> Map.tryFind correlation
    |> Option.map (function
        | Listing -> ExpectRows
        | LoadingStatus -> ExpectStatus
        | Creating _ -> ExpectRow
        | Mutating
        | UploadingCreated
        | Attaching
        | Releasing -> ExpectAcknowledgement)

// ---------------------------------------------------------------------------
// Effect helpers: each allocates a correlation id and remembers its purpose.
// ---------------------------------------------------------------------------

let private correlate (pending: Pending) (page: Page) =
    let correlation = $"c{page.Counter}"

    correlation,
    { page with
        Awaiting = page.Awaiting |> Map.add correlation pending
        Counter = page.Counter + 1 }

let private send pending request page =
    let correlation, next = correlate pending page
    next, [ Send(correlation, request) ]

let private refresh (page: Page) =
    let correlation, next = correlate Listing page
    { next with LatestList = Some correlation }, [ Send(correlation, Requests.list page.Work.Filter) ]

let private upload pending path (files: (SelectedFile * string) list) page =
    let correlation, next = correlate pending page
    let parts = files |> List.map (fun (file, name) -> FilePart("file", file.Id, name))
    next, [ Upload(correlation, path, parts) ]

let private release (files: FileId list) (page: Page) =
    files
    |> List.fold
        (fun (current, effects) file ->
            let correlation, next = correlate Releasing current
            next, effects @ [ Release(correlation, file) ])
        (page, [])

let private failed message page =
    { page with Work = requestFailed message page.Work }, []

let private freshRows prefix count (page: Page) =
    List.init count id
    |> List.fold
        (fun (rows, counter) _ ->
            let key, next = nextKey prefix counter
            rows @ [ key ], next)
        ([], page.Counter)

/// Clears the capture form and gives its picked files back.
let private resetAdd (page: Page) =
    let keys, counter = freshRows "file" 1 page
    let picked = pickedFiles page.AddFiles

    { page with
        Add = emptyAdd
        AddFiles = keys |> List.map emptyFileRow
        Counter = counter }
    |> release picked

let private andThen (step: Page -> Page * Effect list) (page: Page, effects: Effect list) =
    let next, more = step page
    next, effects @ more

// ---------------------------------------------------------------------------
// Row actions and dialogs.
// ---------------------------------------------------------------------------

let private openReason kind id title (page: Page) =
    { page with
        Dialog = ReasonFor(kind, id)
        ReasonTitle = title
        Reason = "" },
    []

let private openComplete id (page: Page) =
    let keys, counter = freshRows "evidence" 2 page

    { page with
        Dialog = CompleteFor id
        Evidence = keys |> List.map (fun key -> { Key = key; Type = ""; Path = "" })
        Conclusion = ""
        Counter = counter },
    []

let private openAttach id (page: Page) =
    let keys, counter = freshRows "attach" 1 page
    let previous = pickedFiles page.AttachFiles

    { page with
        Dialog = AttachFor id
        AttachFiles = keys |> List.map emptyFileRow
        Counter = counter }
    |> release previous

let private rowAction slot id (page: Page) =
    match page.Work.Rows |> List.tryFind (fun row -> row.Id = id) with
    | None -> page, []
    | Some row ->
        match rowActions row |> List.tryItem slot with
        | None -> page, []
        | Some action ->
            match action.Kind with
            | Show -> { page with Work = rowToggled id page.Work }, []
            | Ready -> send Mutating (Requests.ready id) page
            | Resume -> send Mutating (Requests.resume id) page
            | Block -> openReason Blocking id "Reason for blocking" page
            | Abandon -> openReason Abandoning id "Reason for abandoning" page
            | Start -> { page with Dialog = StartFor id }, []
            | Complete -> openComplete id page
            | Edit -> { page with Dialog = UpdateFor row; Update = updateDefaults row }, []
            | Attach -> openAttach id page

let private confirmReason (page: Page) =
    match page.Dialog with
    | ReasonFor(Blocking, id) -> send Mutating (Requests.block id page.Reason) page
    | ReasonFor(Abandoning, id) -> send Mutating (Requests.abandon id page.Reason) page
    | _ -> page, []

let private confirmStart (page: Page) =
    match page.Dialog with
    | StartFor id -> send Mutating (Requests.start id page.StartType) page
    | _ -> page, []

let private confirmComplete (page: Page) =
    match page.Dialog with
    | CompleteFor id ->
        let entered = page.Evidence |> List.map (fun row -> { Type = row.Type; Path = row.Path })
        send Mutating (Requests.complete id (completionInput entered page.Conclusion)) page
    | _ -> page, []

let private confirmUpdate (page: Page) =
    match page.Dialog with
    | UpdateFor row -> send Mutating (Requests.update row.Id (updateInput (Some page.Update.Title) page.Update row)) page
    | _ -> page, []

let private confirmAttach (page: Page) =
    match page.Dialog, uploads page.AttachFiles with
    | AttachFor _, [] -> page, []
    | AttachFor id, files -> upload Attaching (Requests.uploadAttachmentsPath id) files page
    | _ -> page, []

let private submitAdd (page: Page) =
    match addInput page.Add with
    | None -> page, []
    | Some input -> send (Creating(uploads page.AddFiles)) (Requests.add input) page

let private addRow prefix (rows: FileRow list) (page: Page) =
    let key, counter = nextKey prefix page.Counter
    rows @ [ emptyFileRow key ], { page with Counter = counter }

let private picked input key file (page: Page) =
    match input with
    | AddFileInput -> { page with AddFiles = filePicked key file page.AddFiles }, []
    | AttachFileInput -> { page with AttachFiles = filePicked key file page.AttachFiles }, []
    | _ -> page, []

// ---------------------------------------------------------------------------
// Replies.
// ---------------------------------------------------------------------------

let private replied correlation reply (page: Page) =
    match page.Awaiting |> Map.tryFind correlation with
    | None -> page, []
    | Some pending ->
        let page = { page with Awaiting = page.Awaiting |> Map.remove correlation }

        match pending, reply with
        | Listing, _ when page.LatestList <> Some correlation -> page, []
        | Listing, Answered(Rows rows) -> { page with Work = rowsLoaded rows page.Work }, []
        | Listing, Refused message -> failed message page
        | LoadingStatus, Answered(Status status) -> { page with RepositoryLabel = repositoryLabel status }, []
        | LoadingStatus, _ -> { page with RepositoryLabel = "" }, []
        | Mutating, Answered _
        | Attaching, Answered _ -> refresh page
        | Mutating, Refused message
        | Attaching, Refused message -> failed message page
        | Creating [], Answered(Row _) -> refresh page |> andThen resetAdd
        | Creating files, Answered(Row created) ->
            upload UploadingCreated (Requests.uploadAttachmentsPath created.Id) files page
        | UploadingCreated, Answered _ -> refresh page |> andThen resetAdd
        | Creating _, Refused message
        | UploadingCreated, Refused message -> failed message page |> andThen resetAdd
        | Releasing, _ -> page, []
        | _, Answered _ -> page, []

let update (msg: Msg) (page: Page) : Page * Effect list =
    match msg with
    | Started ->
        let listed, effects = refresh page
        let next, status = send LoadingStatus Requests.status listed
        next, effects @ status
    | TagFilterChanged tag -> refresh { page with Work = tagFilterChanged tag page.Work }
    | StatusFilterChanged status -> refresh { page with Work = statusFilterChanged status page.Work }
    | FiltersCleared -> refresh { page with Work = filtersCleared page.Work }
    | AddTitleChanged value -> { page with Add.Title = value }, []
    | AddTagsChanged value -> { page with Add.Tags = value }, []
    | AddPriorityChanged value -> { page with Add.Priority = value }, []
    | AddDescriptionChanged value -> { page with Add.Description = value }, []
    | AddFileRowAdded ->
        let rows, next = addRow "file" page.AddFiles page
        { next with AddFiles = rows }, []
    | AddFileNameChanged(key, name) -> { page with AddFiles = fileNamed key name page.AddFiles }, []
    | AddSubmitted -> submitAdd page
    | FilePicked(input, key, file) -> picked input key file page
    | RowAction(slot, id) -> rowAction slot id page
    | ReasonChanged value -> { page with Reason = value }, []
    | ReasonConfirmed -> confirmReason page
    | StartTypeChanged value -> { page with StartType = value }, []
    | StartConfirmed -> confirmStart page
    | EvidenceRowAdded ->
        let key, counter = nextKey "evidence" page.Counter

        { page with
            Evidence = page.Evidence @ [ { Key = key; Type = ""; Path = "" } ]
            Counter = counter },
        []
    | EvidenceTypeChanged(key, value) ->
        { page with Evidence = page.Evidence |> List.map (fun row -> if row.Key = key then { row with Type = value } else row) },
        []
    | EvidencePathChanged(key, value) ->
        { page with Evidence = page.Evidence |> List.map (fun row -> if row.Key = key then { row with Path = value } else row) },
        []
    | ConclusionChanged value -> { page with Conclusion = value }, []
    | CompleteConfirmed -> confirmComplete page
    | UpdateTitleChanged value -> { page with Update.Title = value }, []
    | UpdateDescriptionChanged value -> { page with Update.Description = value }, []
    | UpdateTagsChanged value -> { page with Update.Tags = value }, []
    | UpdatePriorityChanged value -> { page with Update.Priority = value }, []
    | UpdateConfirmed -> confirmUpdate page
    | AttachFileRowAdded ->
        let rows, next = addRow "attach" page.AttachFiles page
        { next with AttachFiles = rows }, []
    | AttachFileNameChanged(key, name) -> { page with AttachFiles = fileNamed key name page.AttachFiles }, []
    | AttachConfirmed -> confirmAttach page
    | Replied(correlation, reply) -> replied correlation reply page

// ---------------------------------------------------------------------------
// View: the named values web/index.html binds to.
// ---------------------------------------------------------------------------

/// The native dialog a row action opens, if any (`commandfor`).
let private dialogFor kind =
    match kind with
    | Block
    | Abandon -> "reason-dialog"
    | Start -> "start-dialog"
    | Complete -> "complete-dialog"
    | Edit -> "update-dialog"
    | Attach -> "attach-dialog"
    | Show
    | Ready
    | Resume -> ""

let private rowItem (selectedId: string option) (row: WorkRow) =
    let actions = rowActions row

    let slots =
        List.init actionSlots (fun slot ->
            match actions |> List.tryItem slot with
            | Some action ->
                let dialog = dialogFor action.Kind

                [ $"a{slot}Label", Text action.Label
                  $"a{slot}Hidden", Flag false
                  $"a{slot}Dialog", Text dialog
                  $"a{slot}Command", Text(if dialog = "" then "" else "show-modal")
                  $"a{slot}Pressed", Text(if action.Kind = Show && selectedId = Some row.Id then "true" else "false") ]
            | None ->
                [ $"a{slot}Label", Text ""
                  $"a{slot}Hidden", Flag true
                  $"a{slot}Dialog", Text ""
                  $"a{slot}Command", Text ""
                  $"a{slot}Pressed", Text "false" ])
        |> List.concat

    [ "id", Text row.Id
      "title", Text row.Title
      "status", Text row.Status
      "statusTone", Text(statusTone row.Status)
      "tags", Text(String.Join(", ", row.Tags))
      "priority", Text(row.Priority |> Option.defaultValue "")
      "selected", Text(if selectedId = Some row.Id then "true" else "false") ]
    @ slots

let private fileItems (rows: FileRow list) =
    rows
    |> List.map (fun row ->
        [ "key", Text row.Key
          "name", Text row.Name
          "picked", Text(row.File |> Option.map _.Name |> Option.defaultValue "") ])

let view (page: Page) : View =
    let work = page.Work
    let detail = detailView work
    let error = work.Error |> Option.defaultValue ""

    [ "repositoryLabel", Value(Text page.RepositoryLabel)
      "hasError", Value(Flag work.Error.IsSome)
      "error", Value(Text error)
      "rows", Items(work.Rows |> List.map (rowItem work.SelectedId))
      "hasRows", Value(Flag(not work.Rows.IsEmpty))
      "isEmpty", Value(Flag work.Rows.IsEmpty)
      "rowCount", Value(Text(if work.Rows.Length = 1 then "1 item" else $"{work.Rows.Length} items"))
      "filterTag", Value(Text work.Filter.Tag)
      "filterStatus", Value(Text work.Filter.Status)
      "hasDetail", Value(Flag detail.IsSome)
      "detailTitle", Value(Text(detail |> Option.map _.Title |> Option.defaultValue ""))
      "detailDescription", Value(Text(detail |> Option.map _.Description |> Option.defaultValue ""))
      "detailAttachments",
      Items(
          detail
          |> Option.map _.Attachments
          |> Option.defaultValue []
          |> List.map (fun a ->
              [ "key", Text a.Key
                "href", Text a.Href
                "name", Text a.Name
                "sizeLabel", Text a.SizeLabel ])
      )
      "hasAttachments", Value(Flag(detail |> Option.exists (fun d -> not d.Attachments.IsEmpty)))
      "noAttachments", Value(Flag(detail |> Option.exists (fun d -> d.Attachments.IsEmpty)))
      "detailRaw", Value(Text(detail |> Option.map _.Raw |> Option.defaultValue ""))
      "addTitle", Value(Text page.Add.Title)
      "addTags", Value(Text page.Add.Tags)
      "addPriority", Value(Text page.Add.Priority)
      "addDescription", Value(Text page.Add.Description)
      "addFiles", Items(fileItems page.AddFiles)
      "reasonTitle", Value(Text page.ReasonTitle)
      "reason", Value(Text page.Reason)
      "startType", Value(Text page.StartType)
      "evidenceRows",
      Items(
          page.Evidence
          |> List.map (fun row -> [ "key", Text row.Key; "type", Text row.Type; "path", Text row.Path ])
      )
      "conclusion", Value(Text page.Conclusion)
      "updateTitle", Value(Text page.Update.Title)
      "updateDescription", Value(Text page.Update.Description)
      "updateTags", Value(Text page.Update.Tags)
      "updatePriority", Value(Text page.Update.Priority)
      "attachFiles", Items(fileItems page.AttachFiles) ]
