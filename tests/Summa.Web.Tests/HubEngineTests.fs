/// Pure tests of the hub engine's decisions, carrying over every assertion of
/// the TypeScript engine tests this engine replaced (tests/web/hub-engine.test.ts).
module Summa.Web.Tests.HubEngineTests

open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Hub

let repo =
    { Id = "r1"
      Name = "Summa"
      Path = "/src/summa"
      RegisteredAt = "2026-01-01" }

[<Fact>]
let ``each error belongs to one panel and is cleared by its own success`` () =
    let failing = initialState |> reposFailed "a" |> workFailed "b" |> createFailed "c"
    Assert.Equal<string option list>([ Some "a"; Some "b"; Some "c" ], [ failing.ReposError; failing.ListError; failing.CreateError ])
    Assert.Equal(None, (reposLoaded [ repo ] failing).ReposError)
    Assert.Equal(None, (registered failing).ReposError)
    Assert.Equal(None, (workLoaded [] failing).ListError)
    Assert.Equal(None, (created failing).CreateError)
    Assert.Equal(Some "b", (created failing).ListError)

[<Fact>]
let ``filters change independently and clear together`` () =
    let filtered = initialState |> repoFilterChanged "r1" |> tagFilterChanged "x" |> statusFilterChanged "ready"
    Assert.Equal({ Repo = "r1"; Tag = "x"; Status = "ready" }, filtered.Filter)
    Assert.Equal({ Repo = ""; Tag = ""; Status = "" }, (filtersCleared filtered).Filter)

[<Fact>]
let ``the work query carries repo, each trimmed tag and status`` () =
    Assert.Equal("", buildWorkQuery { Repo = ""; Tag = ""; Status = "" })
    Assert.Equal("?repo=r1&tag=a&tag=b&status=ready", buildWorkQuery { Repo = "r1"; Tag = " a, ,b"; Status = "ready" })
    Assert.Equal("/api/work?status=active", (Requests.work { Repo = ""; Tag = ""; Status = "active" }).Path)

[<Fact>]
let ``requests carry the same bodies the page sent`` () =
    Assert.Equal(Some(JObject [ "path", JString "/p" ]), (Requests.register "/p" "").Json)
    Assert.Equal(Some(JObject [ "path", JString "/p"; "name", JString "n" ]), (Requests.register "/p" "n").Json)
    Assert.Equal({ Method = Delete; Path = "/api/repos/r%201"; Json = None }, Requests.unregister "r 1")

    let input: CreateInput =
        { RepoId = "r1"
          Title = "t"
          Tags = [ "a"; "b" ]
          Priority = "low"
          Description = "d" }

    let asJson = Requests.createWork input
    Assert.Equal("/api/repos/r1/work", asJson.Path)

    Assert.Equal(
        Some(
            JObject
                [ "title", JString "t"
                  "tags", JArray [ JString "a"; JString "b" ]
                  "priority", JString "low"
                  "description", JString "d" ]
        ),
        asJson.Json
    )

    Assert.Equal<(string * string) list>(
        [ "title", "t"; "tags", "a,b"; "priority", "low"; "description", "d" ],
        Requests.createFields input
    )

[<Fact>]
let ``input decisions: trimmed, and refused when required fields are missing`` () =
    let raw repoId title tags priority description : RawCreate =
        { RepoId = repoId
          Title = title
          Tags = tags
          Priority = priority
          Description = description }

    Assert.Equal(None, registerInput "  " "x")
    Assert.Equal(Some { Path = "/p"; Name = "n" }, registerInput " /p " " n ")
    Assert.Equal(None, createInput (raw "" "t" "" "medium" ""))
    Assert.Equal(None, createInput (raw "r1" "  " "" "medium" ""))

    let expected: CreateInput =
        { RepoId = "r1"
          Title = "t"
          Tags = [ "a"; "b" ]
          Priority = "high"
          Description = "d" }

    Assert.Equal(Some expected, createInput (raw "r1" " t " "a, b" "high" " d "))

    Assert.Equal("x.txt", uploadName "" "x.txt")

[<Fact>]
let ``selection survives only while its repository is registered`` () =
    Assert.Equal(Some "r1", retainedSelection [ repo ] "r1")
    Assert.Equal(None, retainedSelection [ repo ] "gone")
    Assert.Equal(None, retainedSelection [] "")

[<Fact>]
let ``messages are the ones the page showed`` () =
    Assert.Equal("not a ROS repository", responseError 400 (Some "not a ROS repository"))
    Assert.Equal("request failed (502)", responseError 502 None)

    Assert.Equal(
        "Unregister Summa (/src/summa)? This only removes it from the hub -- the repository itself is unaffected.",
        unregisterConfirmation repo
    )
