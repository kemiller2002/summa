/// Artifact storage per environment (set 0 §0.35, §0.38, INV-DOC-011): each
/// deployment keeps its PDFs in its own store, named for its environment;
/// the records hold only the reference, SHA-256, size and renderer.
module Summa.Storage.Tests.ArtifactsTests

open System
open System.Text
open Xunit
open Arca
open Summa.Ledger
open Summa.Storage
open Summa.Storage.Deployment
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private local name = Deployment.parse $$"""{"environment":"local","environmentName":"{{name}}"}""" |> Support.ok

let private withArtifacts () =
    let r = full ()
    let artifacts = Issuance.initialArtifacts r.Books.Invoices["INV-001"] |> Support.ok
    { r with Books = { r.Books with Artifacts = artifacts |> List.fold (fun m a -> Map.add a.Id a m) r.Books.Artifacts } }

let private pdf (body: string) = Encoding.ASCII.GetBytes("%PDF-1.7\n" + body)

[<Fact>]
let ``each environment keeps its artifacts in its own store unless it names one of its own`` () =
    Assert.Equal(BrowserDatabase "summa-artifacts-local", (local "demo").Artifacts)
    Assert.Equal(BrowserDatabase "summa-artifacts-production", production.Artifacts)

    let named = Deployment.parse """{"environment":"local","environmentName":"demo","artifacts":{"store":"browser","database":"summa-artifacts-local-demo"}}""" |> Support.ok
    Assert.Equal("summa-artifacts-local-demo", Artifacts.database named)

    let refused (artifacts: string) =
        Deployment.parse ("""{"environment":"local","environmentName":"demo","artifacts":""" + artifacts + "}") |> codeOf

    // Another environment's name, an unknown store, or an extra field.
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused """{"store":"browser","database":"summa-artifacts-production"}""")
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused """{"store":"browser","database":"summa-artifacts-localx"}""")
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused """{"store":"browser","database":"summa-artifacts-local-"}""")
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused """{"store":"s3","database":"summa-artifacts-local"}""")
    Assert.Equal("SUMMA.STORAGE.INVALID_CONFIGURATION", refused """{"store":"browser","database":"summa-artifacts-local","bucket":"x"}""")

[<Fact>]
let ``two deployments sharing an artifact store are not isolated`` () =
    let demo = local "demo"
    let dev = local "development"
    Assert.Contains(Environments.SharedArtifacts("demo", "development"), Environments.check [ demo; dev ])
    let separate = { dev with Artifacts = BrowserDatabase "summa-artifacts-local-dev" }
    Assert.DoesNotContain(Environments.SharedArtifacts("demo", "development"), Environments.check [ demo; separate ])

[<Fact>]
let ``a PDF is checked, fingerprinted and recorded with a reference into this environment's store`` () =
    let config = local "demo"
    let bytes = pdf "invoice"
    let sha, size = Artifacts.fingerprint bytes |> Support.ok
    Assert.Equal(64, sha.Length)
    Assert.Equal(int64 bytes.Length, size)
    Assert.Equal(Error Artifacts.NotAPdf, Artifacts.fingerprint (Encoding.ASCII.GetBytes "<html>"))
    Assert.Equal(Error Artifacts.Empty, Artifacts.fingerprint [||])
    Assert.Equal(Error(Artifacts.TooLarge Artifacts.MaxPdfBytes), Artifacts.checkSize (Artifacts.MaxPdfBytes + 1L))

    let r = withArtifacts ()
    Assert.Equal(Ok None, Artifacts.storedPdf config r "INV-001")
    let recorded = Artifacts.recordPdf ledgerContext config "INV-001" (sha, size) r |> Support.ok
    Assert.Equal(Ok(Some sha), Artifacts.storedPdf config recorded "INV-001")
    let artifact = recorded.Books.Artifacts[Issuance.artifactId "INV-001" Sources.InvoicePdf]
    Assert.Equal($"browser-db:summa-artifacts-local/{sha}", artifact.Reference)
    Assert.Equal(Some Artifacts.BrowserPrintRenderer, artifact.Renderer)

    // The same file again changes nothing; a different one is refused.
    Assert.Equal(Ok recorded, Artifacts.recordPdf ledgerContext config "INV-001" (sha, size) recorded)
    let otherSha, otherSize = Artifacts.fingerprint (pdf "other") |> Support.ok

    match Artifacts.recordPdf ledgerContext config "INV-001" (otherSha, otherSize) recorded with
    | Error(Artifacts.Recording(Issuance.AlreadyGenerated _)) -> ()
    | other -> failwith $"%A{other}"

    // Another environment reading these books never takes the PDF for its own.
    let staging = { config with Environment = EnvironmentKind.Staging; Artifacts = BrowserDatabase "summa-artifacts-staging" }
    Assert.Equal(Error(Artifacts.ForeignStore artifact.Reference), Artifacts.storedPdf staging recorded "INV-001")
