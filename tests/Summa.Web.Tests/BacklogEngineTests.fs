/// Pure tests of the work-backlog engine's decisions. They carry over every
/// assertion of the TypeScript engine tests this engine replaced
/// (tests/web/backlog-engine.test.ts), so the page's behaviour is pinned
/// across the move to F#.
module Summa.Web.Tests.BacklogEngineTests

open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Backlog
open Summa.Web.Tests.Support

[<Fact>]
let ``transitions replace only the fields they own`` () =
    let loaded = initialState |> requestFailed "boom" |> rowsLoaded [ row "WI-0001" ]
    Assert.Equal(None, loaded.Error)
    Assert.Equal(1, loaded.Rows.Length)
    Assert.Equal(Some "nope", (requestFailed "nope" loaded).Error)
    Assert.Equal<WorkRow list>(loaded.Rows, (requestFailed "nope" loaded).Rows)

[<Fact>]
let ``selecting a row toggles it`` () =
    let selected = rowToggled "WI-0001" initialState
    Assert.Equal(Some "WI-0001", selected.SelectedId)
    Assert.Equal(None, (rowToggled "WI-0001" selected).SelectedId)
    Assert.Equal(Some "WI-0002", (rowToggled "WI-0002" selected).SelectedId)

[<Fact>]
let ``filters change independently and clear together`` () =
    let filtered = initialState |> tagFilterChanged "a, b" |> statusFilterChanged "ready"
    Assert.Equal({ Tag = "a, b"; Status = "ready" }, filtered.Filter)
    Assert.Equal({ Tag = ""; Status = "" }, (filtersCleared filtered).Filter)

[<Fact>]
let ``the list query carries each trimmed tag and the status`` () =
    Assert.Equal("", buildQuery { Tag = ""; Status = "" })
    Assert.Equal("", buildQuery { Tag = "   "; Status = "" })
    Assert.Equal("?tag=wasm&tag=state&status=ready", buildQuery { Tag = " wasm, ,state "; Status = "ready" })
    Assert.Equal("/api/work?tag=x", (Requests.list { Tag = "x"; Status = "" }).Path)
    // URLSearchParams encoding: space as '+', reserved characters escaped.
    Assert.Equal("?tag=a+b&tag=c%26d", buildQuery { Tag = "a b, c&d"; Status = "" })

[<Fact>]
let ``requests name the item path and carry the same JSON the page sent`` () =
    Assert.Equal(
        { Method = Post
          Path = "/api/work/WI%201/block"
          Json = Some(JObject [ "reason", JString "waiting" ]) },
        Requests.block "WI 1" "waiting"
    )

    Assert.Equal({ Method = Post; Path = "/api/work/WI-1/ready"; Json = None }, Requests.ready "WI-1")

    Assert.Equal(
        Some(
            JObject
                [ "title", JString "t"
                  "tags", JArray [ JString "a" ]
                  "priority", JString "high"
                  "description", JString "d" ]
        ),
        (Requests.add
            { Title = "t"
              Description = "d"
              Tags = [ "a" ]
              Priority = "high" })
            .Json
    )

    Assert.Equal(
        Some(
            JObject
                [ "evidence", JArray [ JObject [ "type", JString "tests"; "path", JString "a.ts" ] ]
                  "conclusion", JNull ]
        ),
        (Requests.complete
            "WI-1"
            { Evidence = [ { Type = "tests"; Path = "a.ts" } ]
              Conclusion = None })
            .Json
    )

    Assert.Equal("/api/work/WI-1/attachments", Requests.uploadAttachmentsPath "WI-1")

[<Fact>]
let ``a failed response reports the server's error, else the status`` () =
    Assert.Equal("illegal transition", responseError 400 (Some "illegal transition"))
    Assert.Equal("request failed (500)", responseError 500 None)

[<Fact>]
let ``a row offers live actions when in flight, backlog actions otherwise`` () =
    let backlog =
        { row "WI-0001" with BacklogActions = [ "ready"; "block"; "start"; "abandon"; "unknown" ] }

    Assert.Equal<string list>(
        [ "Show"; "Mark ready"; "Block"; "Start"; "Abandon"; "Edit"; "Attach" ],
        rowActions backlog |> List.map _.Label
    )

    let live =
        { row "WI-0001" with
            BacklogActions = [ "ready" ]
            LiveWorkItem =
                Some
                    { State = "active"
                      SemanticState = "active"
                      AllowedActions = [ "complete"; "block" ] } }

    Assert.Equal<RowActionKind list>([ Show; Complete; Block; Edit; Attach ], rowActions live |> List.map _.Kind)
    // No row can need more slots than the page renders.
    Assert.True((rowActions backlog).Length <= actionSlots)

[<Fact>]
let ``input becomes a submission: trimmed, tags parsed, empty title refused`` () =
    let raw title description tags priority : RawInput =
        { Title = title
          Description = description
          Tags = tags
          Priority = priority }

    Assert.Equal(None, addInput (raw "  " "" "" "medium"))

    let expected: AddInput =
        { Title = "T"
          Description = "d"
          Tags = [ "a"; "b" ]
          Priority = "low" }

    Assert.Equal(Some expected, addInput (raw " T " " d " "a, ,b" "low"))

    let existing =
        { row "WI-0001" with
            Title = "Original"
            Description = Some "desc"
            Priority = Some "high" }

    Assert.Equal(raw "Original" "desc" "docs, web" "high", updateDefaults existing)
    Assert.Equal("medium", (updateDefaults (row "WI-0001")).Priority)
    Assert.Equal("Original", (updateInput None (raw "" "" "" "medium") existing).Title)
    Assert.Equal("New", (updateInput (Some "  New ") (raw "" "" "" "medium") existing).Title)

    Assert.Equal(
        { Evidence = [ { Type = "tests"; Path = "a.ts" } ]
          Conclusion = None },
        completionInput
            [ { Type = " tests "; Path = " a.ts " }
              { Type = "x"; Path = "" }
              { Type = ""; Path = "" } ]
            "  "
    )

    Assert.Equal(Some "done", (completionInput [] " done ").Conclusion)
    Assert.Equal("a.pdf", uploadName "  " "a.pdf")
    Assert.Equal("spec", uploadName " spec " "a.pdf")

[<Fact>]
let ``the detail panel projects the selected row`` () =
    Assert.Equal(None, detailView initialState)

    let withAttachment =
        { row "WI-0001" with
            Description = Some ""
            Detail = Some "extra"
            Raw = "{ \"id\": \"WI-0001\" }"
            Attachments =
                [ { Id = "f/1"
                    Name = "a.pdf"
                    Size = 2048L
                    ContentType = None
                    UploadedAt = "2026-01-01" } ] }

    let view = initialState |> rowsLoaded [ withAttachment ] |> rowToggled "WI-0001" |> detailView |> Option.get
    Assert.Equal("No description.", view.Description)

    Assert.Equal<AttachmentView list>(
        [ { Key = "f/1"
            Href = "/api/work/WI-0001/attachments/f%2F1"
            Name = "a.pdf"
            SizeLabel = " (2.0 KB)" } ],
        view.Attachments
    )

    Assert.Equal("{ \"id\": \"WI-0001\" }\n\nextra", view.Raw)
    Assert.Equal(None, initialState |> rowsLoaded [ row "WI-0001" ] |> rowToggled "missing" |> detailView)

[<Fact>]
let ``sizes and the repository label format as before`` () =
    Assert.Equal("512 B", formatSize 512L)
    Assert.Equal("1.5 KB", formatSize 1536L)
    Assert.Equal("3.0 MB", formatSize (3L * 1024L * 1024L))

    Assert.Equal(
        "summa · protocol 1.0.0 · validation passed",
        repositoryLabel
            { Repository = "summa"
              ProtocolVersion = "1.0.0"
              Validation = "passed" }
    )

[<Fact>]
let ``paths are encoded the way encodeURIComponent encodes them`` () =
    Assert.Equal("a%20b%2Fc!~*'()", encodeComponent "a b/c!~*'()")
    Assert.Equal("%C3%A9", encodeComponent "é")
