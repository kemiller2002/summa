/// The project-administration hub page as a state machine, in the same shape
/// as BacklogPage: `update` takes the page and one input and returns the next
/// page plus the effects to perform; `view` projects the page into the named
/// values web-hub/index.html binds to. Pure.
module Summa.Web.Engine.HubPage

open System
open Summa.Web.Engine.Common
open Summa.Web.Engine.Hub

type Pending =
    | ListingRepos
    | ListingWork
    | Registering
    /// An unregister; on success both lists are reloaded.
    | Unregistering
    /// A create; on success the form is cleared and the work list reloaded.
    | Creating
    | Releasing

type Expect =
    | ExpectRepos
    | ExpectWork
    | ExpectAcknowledgement

type Response =
    | Repos of RepoEntry list
    | Work of AggregatedRow list
    | Acknowledged

type CreateDraft =
    { Repo: string
      Title: string
      Tags: string
      Priority: string
      Description: string }

type Page =
    { Hub: State
      RegisterPath: string
      RegisterName: string
      Create: CreateDraft
      CreateFiles: FileRow list
      /// The repository the open unregister dialog asks about.
      Unregister: RepoEntry option
      Awaiting: Map<Correlation, Pending>
      LatestWork: Correlation option
      Counter: int }

type Msg =
    | Started
    | RegisterPathChanged of string
    | RegisterNameChanged of string
    | RegisterSubmitted
    | UnregisterRequested of id: string
    | UnregisterConfirmed
    | CreateRepoChanged of string
    | CreateTitleChanged of string
    | CreateTagsChanged of string
    | CreatePriorityChanged of string
    | CreateDescriptionChanged of string
    | CreateFileRowAdded
    | CreateFileNameChanged of key: string * string
    | FilePicked of input: string * key: string * SelectedFile option
    | CreateSubmitted
    | RepoFilterChanged of string
    | TagFilterChanged of string
    | StatusFilterChanged of string
    | FiltersCleared
    | Replied of Correlation * Reply<Response>

[<Literal>]
let CreateFileInput = "create-file"

let private emptyCreate repo =
    { Repo = repo
      Title = ""
      Tags = ""
      Priority = "medium"
      Description = "" }

let initial =
    { Hub = initialState
      RegisterPath = ""
      RegisterName = ""
      Create = emptyCreate ""
      CreateFiles = [ emptyFileRow "file-0" ]
      Unregister = None
      Awaiting = Map.empty
      LatestWork = None
      Counter = 1 }

let expectation (page: Page) (correlation: Correlation) =
    page.Awaiting
    |> Map.tryFind correlation
    |> Option.map (function
        | ListingRepos -> ExpectRepos
        | ListingWork -> ExpectWork
        | Registering
        | Unregistering
        | Creating
        | Releasing -> ExpectAcknowledgement)

/// The repository the create form submits to: the user's choice while it is
/// still registered, else the first registered repository (what a native
/// select shows when its chosen option disappears).
let createRepo (page: Page) =
    retainedSelection page.Hub.Repos page.Create.Repo
    |> Option.orElse (page.Hub.Repos |> List.tryHead |> Option.map _.Id)
    |> Option.defaultValue ""

let private correlate pending (page: Page) =
    let correlation = $"c{page.Counter}"

    correlation,
    { page with
        Awaiting = page.Awaiting |> Map.add correlation pending
        Counter = page.Counter + 1 }

let private send pending request page =
    let correlation, next = correlate pending page
    next, [ Send(correlation, request) ]

let private refreshRepos page = send ListingRepos Requests.repos page

let private refreshWork (page: Page) =
    let correlation, next = correlate ListingWork page
    { next with LatestWork = Some correlation }, [ Send(correlation, Requests.work page.Hub.Filter) ]

let private andThen step (page, effects) =
    let next, more = step page
    next, effects @ more

let private release files page =
    files
    |> List.fold
        (fun (current, effects) file ->
            let correlation, next = correlate Releasing current
            next, effects @ [ Release(correlation, file) ])
        (page, [])

/// Clears the create form and gives its picked files back. The repository
/// choice is kept, so several items can be created in one repository in a row.
let private resetCreate (page: Page) =
    let key, counter = nextKey "file" page.Counter
    let picked = pickedFiles page.CreateFiles

    { page with
        Create = emptyCreate page.Create.Repo
        CreateFiles = [ emptyFileRow key ]
        Counter = counter }
    |> release picked

let private submitCreate (page: Page) =
    let raw: RawCreate =
        { RepoId = createRepo page
          Title = page.Create.Title
          Tags = page.Create.Tags
          Priority = page.Create.Priority
          Description = page.Create.Description }

    match createInput raw with
    | None -> page, []
    | Some input ->
        match uploads page.CreateFiles with
        | [] -> send Creating (Requests.createWork input) page
        | files ->
            let correlation, next = correlate Creating page

            let parts =
                (Requests.createFields input |> List.map Field)
                @ (files |> List.map (fun (file, name) -> FilePart("file", file.Id, name)))

            next, [ Upload(correlation, createPath input.RepoId, parts) ]

let private reposArrived (repos: RepoEntry list) (page: Page) =
    let hub = reposLoaded repos page.Hub

    let filter =
        { hub.Filter with Repo = retainedSelection repos hub.Filter.Repo |> Option.defaultValue "" }

    { page with Hub = { hub with Filter = filter } }, []

let private replied correlation reply (page: Page) =
    match page.Awaiting |> Map.tryFind correlation with
    | None -> page, []
    | Some pending ->
        let page = { page with Awaiting = page.Awaiting |> Map.remove correlation }

        match pending, reply with
        | ListingRepos, Answered(Repos repos) -> reposArrived repos page
        | ListingRepos, Refused message -> { page with Hub = reposFailed message page.Hub }, []
        | ListingWork, _ when page.LatestWork <> Some correlation -> page, []
        | ListingWork, Answered(Work rows) -> { page with Hub = workLoaded rows page.Hub }, []
        | ListingWork, Refused message -> { page with Hub = workFailed message page.Hub }, []
        | Registering, Answered _ ->
            { page with
                Hub = registered page.Hub
                RegisterPath = ""
                RegisterName = "" }
            |> refreshRepos
            |> andThen refreshWork
        | Registering, Refused message
        | Unregistering, Refused message -> { page with Hub = reposFailed message page.Hub }, []
        | Unregistering, Answered _ -> refreshRepos page |> andThen refreshWork
        | Creating, Answered _ ->
            resetCreate { page with Hub = created page.Hub } |> andThen refreshWork
        | Creating, Refused message -> { page with Hub = createFailed message page.Hub }, []
        | Releasing, _ -> page, []
        | _, Answered _ -> page, []

let update (msg: Msg) (page: Page) : Page * Effect list =
    match msg with
    | Started -> refreshRepos page |> andThen refreshWork
    | RegisterPathChanged value -> { page with RegisterPath = value }, []
    | RegisterNameChanged value -> { page with RegisterName = value }, []
    | RegisterSubmitted ->
        match registerInput page.RegisterPath page.RegisterName with
        | None -> page, []
        | Some input -> send Registering (Requests.register input.Path input.Name) page
    | UnregisterRequested id -> { page with Unregister = page.Hub.Repos |> List.tryFind (fun repo -> repo.Id = id) }, []
    | UnregisterConfirmed ->
        match page.Unregister with
        | None -> page, []
        | Some repo -> send Unregistering (Requests.unregister repo.Id) { page with Unregister = None }
    | CreateRepoChanged value -> { page with Create.Repo = value }, []
    | CreateTitleChanged value -> { page with Create.Title = value }, []
    | CreateTagsChanged value -> { page with Create.Tags = value }, []
    | CreatePriorityChanged value -> { page with Create.Priority = value }, []
    | CreateDescriptionChanged value -> { page with Create.Description = value }, []
    | CreateFileRowAdded ->
        let key, counter = nextKey "file" page.Counter

        { page with
            CreateFiles = page.CreateFiles @ [ emptyFileRow key ]
            Counter = counter },
        []
    | CreateFileNameChanged(key, name) -> { page with CreateFiles = fileNamed key name page.CreateFiles }, []
    | FilePicked(CreateFileInput, key, file) -> { page with CreateFiles = filePicked key file page.CreateFiles }, []
    | FilePicked _ -> page, []
    | CreateSubmitted -> submitCreate page
    | RepoFilterChanged repo -> refreshWork { page with Hub = repoFilterChanged repo page.Hub }
    | TagFilterChanged tag -> refreshWork { page with Hub = tagFilterChanged tag page.Hub }
    | StatusFilterChanged status -> refreshWork { page with Hub = statusFilterChanged status page.Hub }
    | FiltersCleared -> refreshWork { page with Hub = filtersCleared page.Hub }
    | Replied(correlation, reply) -> replied correlation reply page

// ---------------------------------------------------------------------------
// View: the named values web-hub/index.html binds to.
// ---------------------------------------------------------------------------

let private workItem (row: AggregatedRow) =
    let status = row.Status |> Option.defaultValue ""

    [ "key", Text(row.RepoId + "/" + (row.Id |> Option.defaultValue "!error"))
      "repoName", Text row.RepoName
      "id", Text(row.Id |> Option.defaultValue "")
      "title", Text(row.Title |> Option.defaultValue "")
      "status", Text status
      "statusTone", Text(statusTone status)
      "hasNoStatus", Flag(status = "")
      "tags", Text(String.Join(", ", row.Tags))
      "priority", Text(row.Priority |> Option.defaultValue "")
      "isError", Flag row.Error.IsSome
      "isWork", Flag row.Error.IsNone
      "error", Text(row.Error |> Option.defaultValue "") ]

let view (page: Page) : View =
    let hub = page.Hub
    let createRepo = createRepo page
    let text (value: string option) = value |> Option.defaultValue ""

    [ "repos",
      Items(
          hub.Repos
          |> List.map (fun repo -> [ "id", Text repo.Id; "name", Text repo.Name; "path", Text repo.Path ])
      )
      "hasRepos", Value(Flag(not hub.Repos.IsEmpty))
      "noRepos", Value(Flag hub.Repos.IsEmpty)
      "createOptions",
      Items(
          hub.Repos
          |> List.map (fun repo -> [ "id", Text repo.Id; "name", Text repo.Name; "selected", Flag(repo.Id = createRepo) ])
      )
      "filterOptions",
      Items(
          hub.Repos
          |> List.map (fun repo ->
              [ "id", Text repo.Id; "name", Text repo.Name; "selected", Flag(repo.Id = hub.Filter.Repo) ])
      )
      "filterAllRepos", Value(Flag(hub.Filter.Repo = ""))
      "filterTag", Value(Text hub.Filter.Tag)
      "filterStatus", Value(Text hub.Filter.Status)
      "rows", Items(hub.Rows |> List.map workItem)
      "hasRows", Value(Flag(not hub.Rows.IsEmpty))
      "isEmpty", Value(Flag hub.Rows.IsEmpty)
      "rowCount", Value(Text(if hub.Rows.Length = 1 then "1 item" else $"{hub.Rows.Length} items"))
      "hasReposError", Value(Flag hub.ReposError.IsSome)
      "reposError", Value(Text(text hub.ReposError))
      "hasCreateError", Value(Flag hub.CreateError.IsSome)
      "createError", Value(Text(text hub.CreateError))
      "hasListError", Value(Flag hub.ListError.IsSome)
      "listError", Value(Text(text hub.ListError))
      "registerPath", Value(Text page.RegisterPath)
      "registerName", Value(Text page.RegisterName)
      "createTitle", Value(Text page.Create.Title)
      "createTags", Value(Text page.Create.Tags)
      "createPriority", Value(Text page.Create.Priority)
      "createDescription", Value(Text page.Create.Description)
      "createFiles",
      Items(
          page.CreateFiles
          |> List.map (fun row ->
              [ "key", Text row.Key
                "name", Text row.Name
                "picked", Text(row.File |> Option.map _.Name |> Option.defaultValue "") ])
      )
      "unregisterText", Value(Text(page.Unregister |> Option.map unregisterConfirmation |> Option.defaultValue "")) ]
