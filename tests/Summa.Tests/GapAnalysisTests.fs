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
