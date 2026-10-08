/// The package stays small and independent (DF-SUMMA-2026-0001): it is the
/// contract, not a piece of Summa.
module Summa.Contracts.Tests.PackageTests

open System.Text.RegularExpressions
open Xunit
open Summa.Contracts.Tests.Support

let private project = readRepoFile "src/Summa.Contracts/Summa.Contracts.fsproj"

[<Fact>]
let ``the package references no project and no package beyond FSharp.Core`` () =
    Assert.DoesNotContain("ProjectReference", project)
    Assert.DoesNotContain("PackageReference", project)

[<Fact>]
let ``the package has an exact release version and its own package id`` () =
    Assert.Matches(Regex(@"<Version>\d+\.\d+\.\d+</Version>"), project)
    Assert.Contains("<PackageId>EchelonFoundry.Summa.Contracts</PackageId>", project)

[<Fact>]
let ``the release input names the package the project produces`` () =
    let input = readRepoFile "release/summa-contracts.release-input.json"
    Assert.Contains("\"systemId\": \"summa-contracts\"", input)
    Assert.Contains("\"distributionClass\": \"nuget-library\"", input)
    Assert.Contains("EchelonFoundry.Summa.Contracts.{version}.nupkg", input)
