/// The work-backlog page as a state machine: which effects each input asks
/// for, and how replies move the page on. Pure; no browser.
module Summa.Web.Tests.BacklogPageTests

open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Backlog
open Summa.Web.Engine.BacklogPage
open Summa.Web.Tests.Support

let private run (messages: Msg list) =
    messages
    |> List.fold
        (fun (page, _) msg -> update msg page)
        (initial, [])

let private sent effects =
    effects
    |> List.choose (function
        | Send(correlation, request) -> Some(correlation, request)
        | _ -> None)

let private only effects =
    match effects with
    | [ effect ] -> effect
    | other -> failwith $"expected one effect, got {other}"

let private valueOf (name: string) (view: View) =
    view |> List.find (fst >> (=) name) |> snd

[<Fact>]
let ``starting loads the queue and the repository status`` () =
    let page, effects = update Started initial

    Assert.Equal<Request list>(
        [ Requests.list initialState.Filter; Requests.status ],
        sent effects |> List.map snd
    )

    Assert.Equal(Some ExpectRows, expectation page "c1")
    Assert.Equal(Some ExpectStatus, expectation page "c2")

[<Fact>]
let ``only the newest list reply replaces the rows`` () =
    let page, _ = run [ Started; TagFilterChanged "a" ]
    // c1 was the first list; c3 the one for tag "a".
    let late, effects = update (Replied("c1", Answered(Rows [ row "OLD" ]))) page
    Assert.Empty effects
    Assert.Empty late.Work.Rows
    let current, _ = update (Replied("c3", Answered(Rows [ row "NEW" ]))) late
    Assert.Equal<string list>([ "NEW" ], current.Work.Rows |> List.map _.Id)

[<Fact>]
let ``a refused list shows the server's message`` () =
    let page, _ = run [ Started; Replied("c1", Refused "boom") ]
    Assert.Equal(Some "boom", page.Work.Error)
    Assert.Equal(Value(Flag true), view page |> valueOf "hasError")

[<Fact>]
let ``row slots map to the row's actions in order`` () =
    let r = { row "WI-1" with BacklogActions = [ "abandon"; "ready" ] }
    let page, _ = run [ Started; Replied("c1", Answered(Rows [ r ])) ]

    // Slot 2 is "Mark ready" (after Show and Abandon): a POST, then a reload.
    let afterReady, effects = update (RowAction(2, "WI-1")) page
    Assert.Equal(Requests.ready "WI-1", only effects |> function Send(_, request) -> request | e -> failwith $"{e}")
    let _, reload = update (Replied("c3", Answered Acknowledged)) afterReady
    Assert.Equal(Requests.list initialState.Filter, only reload |> function Send(_, request) -> request | e -> failwith $"{e}")

    // Slot 1 opens the reason dialog for abandoning; nothing is sent yet.
    let abandoning, none = update (RowAction(1, "WI-1")) page
    Assert.Empty none
    Assert.Equal(ReasonFor(Abandoning, "WI-1"), abandoning.Dialog)
    Assert.Equal("Reason for abandoning", abandoning.ReasonTitle)
    let confirmed, effects = abandoning |> update (ReasonChanged "dup") |> fst |> update ReasonConfirmed
    Assert.Equal(Requests.abandon "WI-1" "dup", only effects |> function Send(_, request) -> request | e -> failwith $"{e}")
    Assert.Equal(Some ExpectAcknowledgement, expectation confirmed "c3")

    // A slot past the row's actions, or an unknown row, does nothing.
    Assert.Equal<Effect list>([], update (RowAction(6, "WI-1")) page |> snd)
    Assert.Equal<Effect list>([], update (RowAction(0, "missing")) page |> snd)

[<Fact>]
let ``the view names each slot's label, visibility and dialog`` () =
    let r = { row "WI-1" with BacklogActions = [ "start" ] }
    let page, _ = run [ Started; Replied("c1", Answered(Rows [ r ])) ]

    let item =
        match view page |> valueOf "rows" with
        | Items [ item ] -> Map.ofList item
        | other -> failwith $"{other}"

    Assert.Equal(Text "Start", item["a1Label"])
    Assert.Equal(Text "start-dialog", item["a1Dialog"])
    Assert.Equal(Text "show-modal", item["a1Command"])
    Assert.Equal(Text "", item["a0Dialog"])
    Assert.Equal(Flag true, item["a4Hidden"])
    Assert.Equal(Text "", item["statusTone"])

[<Fact>]
let ``a capture with files creates the item, then uploads its files, then clears the form`` () =
    let file = { Id = "f1"; Name = "notes.txt" }

    let page, effects =
        run
            [ Started
              AddTitleChanged " Ledger "
              AddFileNameChanged("file-0", "spec.txt")
              FilePicked(AddFileInput, "file-0", Some file)
              AddSubmitted ]

    Assert.Equal(
        Requests.add
            { Title = "Ledger"
              Description = ""
              Tags = []
              Priority = "medium" },
        only effects |> function Send(_, request) -> request | e -> failwith $"{e}"
    )

    let uploading, upload = update (Replied("c3", Answered(Row(row "WI-9")))) page
    Assert.Equal(Upload("c4", "/api/work/WI-9/attachments", [ FilePart("file", "f1", "spec.txt") ]), only upload)
    // The form is not cleared until the upload has answered.
    Assert.Equal(" Ledger ", uploading.Add.Title)

    let cleared, after = update (Replied("c4", Answered Acknowledged)) uploading
    Assert.Equal("", cleared.Add.Title)
    Assert.True(cleared.AddFiles |> List.forall (fun r -> r.File.IsNone && r.Name = ""))
    Assert.NotEqual<string list>([ "file-0" ], cleared.AddFiles |> List.map _.Key)

    Assert.Equal<Effect list>(
        [ Send("c5", Requests.list initialState.Filter); Release("c7", "f1") ],
        after
    )

[<Fact>]
let ``a capture without a title sends nothing`` () =
    Assert.Equal<Effect list>([], run [ Started; AddTitleChanged "  "; AddSubmitted ] |> snd)

[<Fact>]
let ``completion submits only the evidence rows that were filled in`` () =
    let page, _ =
        run
            [ Started
              Replied("c1", Answered(Rows [ { row "WI-1" with LiveWorkItem = Some { State = "active"; SemanticState = "active"; AllowedActions = [ "complete" ] } } ]))
              RowAction(1, "WI-1") ]

    Assert.Equal(CompleteFor "WI-1", page.Dialog)
    let keys = page.Evidence |> List.map _.Key
    Assert.Equal(2, keys.Length)

    let _, effects =
        page
        |> update (EvidenceTypeChanged(keys[0], " tests "))
        |> fst
        |> update (EvidencePathChanged(keys[0], "a.ts"))
        |> fst
        |> update CompleteConfirmed

    Assert.Equal(
        Requests.complete
            "WI-1"
            { Evidence = [ { Type = "tests"; Path = "a.ts" } ]
              Conclusion = None },
        only effects |> function Send(_, request) -> request | e -> failwith $"{e}"
    )

[<Fact>]
let ``attach with no picked file sends nothing`` () =
    let page, _ = run [ Started; Replied("c1", Answered(Rows [ row "WI-1" ])); RowAction(2, "WI-1") ]
    Assert.Equal(AttachFor "WI-1", page.Dialog)
    Assert.Equal<Effect list>([], update AttachConfirmed page |> snd)
