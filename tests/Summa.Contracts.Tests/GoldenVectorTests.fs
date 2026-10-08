/// The golden vectors shipped in the package: Chrona's tests run the same
/// checks against its own encoder.
module Summa.Contracts.Tests.GoldenVectorTests

open Xunit
open Summa.Contracts
open Summa.Contracts.Tests.Support

let validVectors: TheoryData<string> = TheoryData<string>(GoldenVectors.valid |> List.map _.Name)
let invalidVectors: TheoryData<string> = TheoryData<string>(GoldenVectors.invalid |> List.map _.Vector.Name)

let private validNamed name = GoldenVectors.valid |> List.find (fun v -> v.Name = name)
let private invalidNamed name = GoldenVectors.invalid |> List.find (fun r -> r.Vector.Name = name)

[<Theory>]
[<MemberData(nameof validVectors)>]
let ``every valid vector decodes and re-encodes to exactly its own bytes`` (name: string) =
    let vector = validNamed name

    match decodeAny vector.Text with
    | Ok message -> Assert.Equal(vector.Text, encodeAny message)
    | Error problems -> failwithf "%s was refused: %A" name problems

[<Theory>]
[<MemberData(nameof invalidVectors)>]
let ``every invalid vector is refused with a problem at its expected path`` (name: string) =
    let refusal = invalidNamed name

    match decodeAny refusal.Vector.Text with
    | Ok message -> failwithf "%s was accepted: %A" name message
    | Error problems -> Assert.Contains(refusal.ExpectedPath, paths problems)

[<Fact>]
let ``the vectors cover every message kind`` () =
    let kinds =
        GoldenVectors.valid
        |> List.choose (fun v -> Json.parse v.Text |> Result.toOption |> Option.bind (Json.tryField "kind"))
        |> List.distinct
        |> List.sortBy string

    let expected =
        [ "adjustment-required"; "billable-time-published"; "invoiced"; "publication-withdrawn" ]
        |> List.map Json.String

    Assert.Equal<Json list>(expected, kinds)

[<Fact>]
let ``the package carries the vectors that are in the repository`` () =
    let onDisk folder =
        System.IO.Directory.GetFiles(System.IO.Path.Combine(repositoryRoot, "src/Summa.Contracts/vectors", folder), "*.json")
        |> Array.length

    Assert.Equal(onDisk "valid", GoldenVectors.valid.Length)
    Assert.Equal(onDisk "invalid", GoldenVectors.invalid.Length)
    Assert.True(GoldenVectors.valid.Length >= 8 && GoldenVectors.invalid.Length >= 20)

/// Pins the canonical bytes: a change to canonicalization or to a vector
/// changes this digest, and must be a deliberate contract change.
[<Fact>]
let ``the digest of a vector is pinned`` () =
    let vector = validNamed "invoiced"
    let json = Json.parse vector.Text |> Result.defaultWith failwith
    Assert.Equal("f467f5bfc22384d97ddd54277aa871c507e4b2dfa1386adb95a367a581c930ac", ChronaBilling.V1.Codec.digest json)
