/// The deployment's configuration (SUM-DATALOC-001, SUM0-009, SUM0-010,
/// SUM0-033): the only source of locations, organizations and sign-in.
module Summa.Storage.Tests.DeploymentTests

open Xunit
open Arca
open Summa.Storage
open Summa.Storage.Storage
open Summa.Storage.Tests.Support

[<Fact>]
let ``the data location comes from configuration, never from code`` () =
    let first = bindingOf production
    let second = configText "test" "other-owner" "books" "" |> Deployment.parse |> ok |> bindingOf

    Assert.Equal("acme/summa-data", string first.Location.Repository)
    Assert.Equal("deployments/prod/summa", RelativePath.render (applicationNamespace first |> ok).Root)
    Assert.Equal("other-owner/books", string second.Location.Repository)
    // An empty base path puts Summa's folder at the repository root: still only its own folder.
    Assert.Equal("summa", RelativePath.render (applicationNamespace second |> ok).Root)
    Assert.Equal(EnvironmentKind.Production, first.Environment.Kind)

[<Theory>]
[<InlineData("../outside")>]
[<InlineData(".github")>]
[<InlineData("a//b")>]
let ``an unsafe base path is refused`` (basePath: string) =
    Assert.Equal("SUMMA.STORAGE.INVALID_LOCATION", configText "production" "acme" "summa-data" basePath |> Deployment.parse |> codeOf)

[<Fact>]
let ``a configuration that is not closed, complete and valid is refused`` () =
    let refused (text: string) = Deployment.parse text |> codeOf
    let valid = configText "production" "acme" "summa-data" ""

    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused "not json")
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("\"environment\":\"production\"", "\"environment\":\"prod\"")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("{\"environment\"", "{\"token\":\"x\",\"environment\"")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("https://fides.test", "http://fides.test")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("https://fides.test", "https://fides.test/path")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("\"provider\":\"github\"", "\"provider\":\"gitlab\"")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("[\"583231\"]", "[\"octocat\"]")))
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused (valid.Replace("\"id\":\"org_eu\"", "\"id\":\"org_acme\"")))
    Assert.Equal("SUMMA.ORGANIZATION.INVALID_CURRENCY", refused (valid.Replace("\"USD\"", "\"usd\"")))
    Assert.Equal("SUMMA.ORGANIZATION.INVALID_SLUG", refused (valid.Replace("\"slug\":\"acme\"", "\"slug\":\"Acme\"")))
    Assert.Equal("SUMMA.ORGANIZATION.INVALID_ID", refused (valid.Replace("\"id\":\"org_acme\"", "\"id\":\"org acme\"")))
    Assert.Equal("SUMMA.STORAGE.MISSING_FIELD", refused (valid.Replace("\"environmentName\":\"production\"", "\"environmentName\":\" \"")))

[<Fact>]
let ``storage needs sign-in and organizations, and organizations need storage`` () =
    let refused (text: string) = Deployment.parse text |> codeOf

    Assert.Equal(
        "SUMMA.STORAGE.INVALID_CONFIGURATION",
        refused """{"environment":"test","environmentName":"t","location":{"owner":"a","repository":"b","branch":"main","basePath":""}}"""
    )

    Assert.Equal(
        "SUMMA.STORAGE.INVALID_CONFIGURATION",
        refused
            """{"environment":"test","environmentName":"t","organizations":[{"id":"o","displayName":"O","slug":"o","defaultCurrency":"USD","timeZone":"UTC"}]}"""
    )

    let local = Deployment.parse """{"environment":"local","environmentName":"local"}""" |> ok
    Assert.Equal(None, local.Location)
    Assert.Equal("SUMMA.STORAGE.MISSING_FIELD", binding local |> codeOf)

[<Fact>]
let ``organizations keep their configured order, ids, currencies and administrators`` () =
    Assert.Equal<string list>([ "org_acme"; "org_eu" ], production.Organizations |> List.map _.Id)
    let eu = Deployment.organization production "org_eu" |> Option.get
    Assert.Equal("EUR", eu.DefaultCurrency)
    Assert.Equal<string list>([ "583231" ], eu.Administrators)
    Assert.True(Deployment.isBootstrapAdministrator eu "github:583231")
    Assert.False(Deployment.isBootstrapAdministrator eu "github:1")
    Assert.False(Deployment.isBootstrapAdministrator eu "583231")
