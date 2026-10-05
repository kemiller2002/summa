/// The hub page as a state machine. Pure; no browser.
module Summa.Web.Tests.HubPageTests

open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Hub
open Summa.Web.Engine.HubPage

let private repo id =
    { Id = id
      Name = id.ToUpperInvariant()
      Path = $"/src/{id}"
      RegisteredAt = "" }

let private run (messages: Msg list) =
    messages |> List.fold (fun (page, _) msg -> update msg page) (initial, [])

let private requestOf effects =
    match effects with
    | [ Send(_, request) ] -> request
    | other -> failwith $"expected one request, got {other}"

[<Fact>]
let ``starting loads the repositories and the aggregated work`` () =
    let page, effects = update Started initial
    Assert.Equal<Effect list>([ Send("c1", Requests.repos); Send("c2", Requests.work initialState.Filter) ], effects)
    Assert.Equal(Some ExpectRepos, expectation page "c1")
    Assert.Equal(Some ExpectWork, expectation page "c2")

[<Fact>]
let ``the create form targets the chosen repository, else the first one`` () =
    let page, _ = run [ Started; Replied("c1", Answered(Repos [ repo "a"; repo "b" ])) ]
    Assert.Equal("a", createRepo page)
    let chosen, _ = update (CreateRepoChanged "b") page
    Assert.Equal("b", createRepo chosen)
    // Once "b" is unregistered the form falls back to the first repository.
    let reloading, _ = update Started chosen
    let gone, _ = update (Replied("c3", Answered(Repos [ repo "a" ]))) reloading
    Assert.Equal("a", createRepo gone)

[<Fact>]
let ``a repository filter that no longer exists is dropped`` () =
    let page, _ = run [ Started; Replied("c1", Answered(Repos [ repo "a" ])); RepoFilterChanged "a" ]
    let reloaded, _ = update Started page
    let gone, _ = update (Replied("c4", Answered(Repos []))) reloaded
    Assert.Equal("", gone.Hub.Filter.Repo)

[<Fact>]
let ``create is JSON without files and multipart with them`` () =
    let ready, _ = run [ Started; Replied("c1", Answered(Repos [ repo "a" ])); CreateTitleChanged " t "; CreateTagsChanged "x, y" ]

    let _, plain = update CreateSubmitted ready

    Assert.Equal(
        Requests.createWork
            { RepoId = "a"
              Title = "t"
              Tags = [ "x"; "y" ]
              Priority = "medium"
              Description = "" },
        requestOf plain
    )

    let withFile, _ =
        update (FilePicked(CreateFileInput, "file-0", Some { Id = "f1"; Name = "a.txt" })) ready

    let _, multipart = update CreateSubmitted withFile

    Assert.Equal<Effect list>(
        [ Upload(
              "c3",
              "/api/repos/a/work",
              [ Field("title", "t")
                Field("tags", "x,y")
                Field("priority", "medium")
                Field("description", "")
                FilePart("file", "f1", "a.txt") ]
          ) ],
        multipart
    )

[<Fact>]
let ``unregister happens only on confirmation, for the repository that was asked about`` () =
    let page, _ = run [ Started; Replied("c1", Answered(Repos [ repo "a"; repo "b" ])) ]
    Assert.Equal<Effect list>([], update UnregisterConfirmed page |> snd)
    let asked, none = update (UnregisterRequested "b") page
    Assert.Empty none
    Assert.Equal(Some(repo "b"), asked.Unregister)
    let confirmed, effects = update UnregisterConfirmed asked
    Assert.Equal(Requests.unregister "b", requestOf effects)
    Assert.Equal(None, confirmed.Unregister)
    let _, reload = update (Replied("c3", Answered Acknowledged)) confirmed
    Assert.Equal<Effect list>([ Send("c4", Requests.repos); Send("c5", Requests.work initialState.Filter) ], reload)

[<Fact>]
let ``a refused create keeps the form and shows the spoke's message`` () =
    let ready, _ = run [ Started; Replied("c1", Answered(Repos [ repo "a" ])); CreateTitleChanged "t"; CreateSubmitted ]
    let refused, effects = update (Replied("c3", Refused "a: invalid priority")) ready
    Assert.Empty effects
    Assert.Equal(Some "a: invalid priority", refused.Hub.CreateError)
    Assert.Equal("t", refused.Create.Title)
