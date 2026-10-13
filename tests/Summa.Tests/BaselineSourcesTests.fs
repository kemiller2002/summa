/// The measurement baseline index (WI-0049) keeps up with what Conditor
/// selects: every component this repository installs is named there at the
/// version it runs, so an upgrade cannot leave the index stale.
module Summa.Tests.BaselineSourcesTests

open System.Text.Json
open Xunit
open Summa.Tests.Support

let private index = readRepoFile "docs/measurement/baseline-sources.md"

let private names =
    Map.ofList
        [ "ordo", "Ordo"
          "praxis", "Praxis"
          "visual-engineering", "Visual Engineering"
          "arca", "Arca"
          "fides", "Fides"
          "limen-fsharp", "Limen" ]

[<Fact>]
let ``every component Conditor selects is in the baseline index at its version`` () =
    use conditor = JsonDocument.Parse(readRepoFile "conditor.json")
    let lines = index.Split '\n' |> List.ofArray

    let components =
        [ for c in conditor.RootElement.GetProperty("components").EnumerateArray() -> string (c.GetProperty("id").GetString()), string (c.GetProperty("version").GetString()) ]

    Assert.NotEmpty components

    for id, version in components do
        let name = names |> Map.tryFind id |> Option.defaultWith (fun () -> failwith $"name the component {id} in the baseline index and in this test")
        Assert.True(lines |> List.exists (fun l -> l.Contains name && l.Contains version), $"{name} {version} is not in docs/measurement/baseline-sources.md")

[<Fact>]
let ``the baseline index says it copies no telemetry and says what is missing`` () =
    Assert.Contains("**index only**", index)
    Assert.Contains("## Missing or partial", index)
