/// Engine for the project-administration hub page (web-hub/): the page's
/// state, its transitions, and every decision about what to request and what
/// to show.
///
/// The hub decides nothing about work-item legality: every create/list
/// request is a pass-through to a spoke repository's own `./ros`, run by the
/// server. Errors shown are exactly what that repository's CLI said.
module Summa.Web.Engine.Hub

open Summa.Web.Engine.Common

type RepoEntry =
    { Id: string
      Name: string
      Path: string
      RegisteredAt: string }

/// One row of `GET /api/work` on the hub: a spoke's work item, or that
/// spoke's error when its `./ros` could not answer.
type AggregatedRow =
    { RepoId: string
      RepoName: string
      Id: string option
      Title: string option
      Status: string option
      Tags: string list
      Priority: string option
      Error: string option }

type Filter = { Repo: string; Tag: string; Status: string }

type State =
    { Repos: RepoEntry list
      Rows: AggregatedRow list
      Filter: Filter
      ReposError: string option
      CreateError: string option
      ListError: string option }

let initialState =
    { Repos = []
      Rows = []
      Filter = { Repo = ""; Tag = ""; Status = "" }
      ReposError = None
      CreateError = None
      ListError = None }

// ---------------------------------------------------------------------------
// Transitions. Each error belongs to one panel and is cleared by its own success.
// ---------------------------------------------------------------------------

let reposLoaded (repos: RepoEntry list) (state: State) = { state with Repos = repos; ReposError = None }
let reposFailed (message: string) (state: State) = { state with ReposError = Some message }
let registered (state: State) = { state with ReposError = None }

let workLoaded (rows: AggregatedRow list) (state: State) = { state with Rows = rows; ListError = None }
let workFailed (message: string) (state: State) = { state with ListError = Some message }

let created (state: State) = { state with CreateError = None }
let createFailed (message: string) (state: State) = { state with CreateError = Some message }

let repoFilterChanged (repo: string) (state: State) = { state with Filter = { state.Filter with Repo = repo } }
let tagFilterChanged (tag: string) (state: State) = { state with Filter = { state.Filter with Tag = tag } }

let statusFilterChanged (status: string) (state: State) =
    { state with Filter = { state.Filter with Status = status } }

let filtersCleared (state: State) = { state with Filter = { Repo = ""; Tag = ""; Status = "" } }

// ---------------------------------------------------------------------------
// Requests, described as data.
// ---------------------------------------------------------------------------

type CreateInput =
    { RepoId: string
      Title: string
      Tags: string list
      Priority: string
      Description: string }

let buildWorkQuery (filter: Filter) =
    queryString (
        (if filter.Repo <> "" then [ "repo", filter.Repo ] else [])
        @ tagPairs filter.Tag
        @ (if filter.Status <> "" then [ "status", filter.Status ] else [])
    )

let createPath (repoId: string) = $"/api/repos/{encodeComponent repoId}/work"

module Requests =
    let repos = { Method = Get; Path = "/api/repos"; Json = None }

    /// An empty display name is left out, as the page always sent it.
    let register (repoPath: string) (name: string) =
        { Method = Post
          Path = "/api/repos"
          Json =
            Some(
                JObject(
                    [ "path", JString repoPath ]
                    @ (if name <> "" then [ "name", JString name ] else [])
                )
            ) }

    let unregister (id: string) =
        { Method = Delete; Path = $"/api/repos/{encodeComponent id}"; Json = None }

    let work (filter: Filter) = { Method = Get; Path = "/api/work" + buildWorkQuery filter; Json = None }

    /// JSON when nothing is attached.
    let createWork (input: CreateInput) =
        { Method = Post
          Path = createPath input.RepoId
          Json =
            Some(
                JObject
                    [ "title", JString input.Title
                      "tags", input.Tags |> List.map JString |> JArray
                      "priority", JString input.Priority
                      "description", JString input.Description ]
            ) }

    /// Multipart text fields when files are attached; the files follow them.
    let createFields (input: CreateInput) =
        [ "title", input.Title
          "tags", String.concat "," input.Tags
          "priority", input.Priority
          "description", input.Description ]

// ---------------------------------------------------------------------------
// Decisions about input.
// ---------------------------------------------------------------------------

type RegisterInput = { Path: string; Name: string }

/// A registration, or None when no path was given.
let registerInput (path: string) (name: string) =
    match path.Trim() with
    | "" -> None
    | trimmed -> Some { Path = trimmed; Name = name.Trim() }

type RawCreate =
    { RepoId: string
      Title: string
      Tags: string
      Priority: string
      Description: string }

/// A new work item, or None when no repository or title was given.
let createInput (raw: RawCreate) : CreateInput option =
    match raw.RepoId, raw.Title.Trim() with
    | "", _
    | _, "" -> None
    | repoId, title ->
        Some
            { RepoId = repoId
              Title = title
              Tags = parseTags raw.Tags
              Priority = raw.Priority
              Description = raw.Description.Trim() }

let unregisterConfirmation (repo: RepoEntry) =
    $"Unregister {repo.Name} ({repo.Path})? This only removes it from the hub -- the repository itself is unaffected."

/// The selection to keep after the repository list changes, or None if it no longer exists.
let retainedSelection (repos: RepoEntry list) (current: string) =
    if repos |> List.exists (fun repo -> repo.Id = current) then Some current else None
