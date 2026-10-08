/// The requirement gap analysis stays complete and consistent: every
/// numbered section and every invoice-generation requirement family has
/// exactly one row, and both summaries are the counts of their columns.
module Summa.Tests.GapAnalysisTests

open System.Text.RegularExpressions
open Xunit
open Summa.Tests.Support

let private analysis = readRepoFile "docs/requirements/implementation-gap-analysis.md"
let private source name = readRepoFile $"input-documents/{name}"

let private ids (pattern: string) (prefix: string) (text: string) =
    Regex.Matches(text, pattern) |> Seq.map (fun m -> sprintf "%s-%03d" prefix (int m.Groups[1].Value)) |> Set.ofSeq

let private expected =
    Set.unionMany
        [ ids @"(?m)^# 0\.(\d+) " "SUM0" (source "summa-requirement-set-0.txt")
          ids @"(?m)^## (\d+)\. " "SUM1" (source "summa-v0.1-requirements.txt")
          ids @"(?m)^## (\d+)\. " "SUM2" (source "summa-v0.2-requirements.txt")
          ids @"(?m)^## (\d+)\. " "SUM3" (source "summa-v0.3-requirements.txt")
          ids @"(?m)^## (\d+)\. " "SUM4" (source "summa-v0.4-requirements.txt")
          Regex.Matches(source "summa-invoice-generation-requirements.txt", @"\b(INV-[A-Z0-9]+)-\d{3}\b")
          |> Seq.map _.Groups[1].Value
          |> Set.ofSeq ]

let private rows =
    Regex.Matches(analysis, @"(?m)^\| ((?:SUM\d-\d{3})|(?:INV-[A-Z0-9]+)) \| (tested|partial|missing) \| (tested|partial|missing) \|")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
    |> Seq.toList

[<Fact>]
let ``every section and requirement family has exactly one row`` () =
    let found = rows |> List.map (fun (id, _, _) -> id)
    Assert.Equal<string list>(List.distinct found, found)
    Assert.Equal<Set<string>>(expected, Set.ofList found)

let private summary (heading: string) =
    let section = analysis.Substring(analysis.IndexOf heading)
    let m = Regex.Match(section, @"(?m)^\| Summa requirements \| (\d+) \| (\d+) \| (\d+) \| (\d+) \|")
    Assert.True(m.Success, heading)
    [ for i in 1..4 -> int m.Groups[i].Value ]

let private counts column =
    let count status = rows |> List.filter (fun row -> column row = status) |> List.length
    [ rows.Length; count "tested"; count "partial"; count "missing" ]

[<Fact>]
let ``both summaries are the counts of their columns`` () =
    Assert.Equal<int list>(counts (fun (_, b, _) -> b), summary "## Summary")
    Assert.Equal<int list>(counts (fun (_, _, c) -> c), summary "## Coverage after this programme")

/// Every row that is not yet `tested` is planned: an open work item in the
/// Praxis queue (captured, ready, active or blocked) names it, directly
/// (`SUM2-028`, `INV-DOC`) or inside a range of the same set
/// (`SUM0-005..011`). A gap that no open work item names would be silently
/// dropped from the backlog.
let private openWorkText =
    use queue = System.Text.Json.JsonDocument.Parse(readRepoFile ".ros/work/queue.json")

    queue.RootElement.GetProperty("items").EnumerateArray()
    |> Seq.filter (fun item ->
        match item.GetProperty("status").GetString() with
        | "complete"
        | "abandoned" -> false
        | _ -> true)
    |> Seq.map (fun item ->
        let text (name: string) =
            match item.TryGetProperty name with
            | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> string (value.GetString())
            | _ -> ""

        text "title" + "\n" + text "description")
    |> String.concat "\n"

let private plannedIds =
    let known = rows |> List.map (fun (id, _, _) -> id)

    let direct =
        Regex.Matches(openWorkText, @"\b(?:SUM\d-\d{3}|INV-[A-Z0-9]+)\b") |> Seq.map _.Value

    let ranges =
        Regex.Matches(openWorkText, @"\b(SUM\d)-(\d{3})\.\.(?:SUM\d-)?(\d{3})")
        |> Seq.collect (fun m ->
            let set, low, high = m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value
            known
            |> List.filter (fun id ->
                id.StartsWith(set + "-")
                && int (id.Substring 5) >= low
                && int (id.Substring 5) <= high))

    Seq.append direct ranges |> Set.ofSeq

[<Fact>]
let ``every row that is not yet tested is named by an open work item`` () =
    let unplanned =
        rows
        |> List.filter (fun (id, _, current) -> current <> "tested" && not (plannedIds.Contains id))
        |> List.map (fun (id, _, _) -> id)

    Assert.Empty unplanned
