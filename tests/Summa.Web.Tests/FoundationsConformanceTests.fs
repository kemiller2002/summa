/// Conformance: Summa's web surfaces are built on the Echelon foundations the
/// way the other Echelon applications are, and the repository says so
/// truthfully. Each test fails if a foundation stops being consumed: pinned
/// to the release the `echelon-current` registry channel selects, installed,
/// and actually used (not merely listed), with no local copy standing in.
module Summa.Web.Tests.FoundationsConformanceTests

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Summa.Web.Tests.Support

let private json (relative: string) = JsonNode.Parse(readRepoFile relative)

let private str (node: JsonNode) = node.GetValue<string>()

let private pages = [ "web"; "web-hub" ]

let private formaRelease =
    "https://github.com/kemiller2002/forma/releases/download/v0.4.1/echelon-foundry-design-system-0.4.1.tgz"

let private folioRelease =
    "https://github.com/kemiller2002/folio/releases/download/v0.3.0/echelon-foundry-print-components-0.3.0.tgz"

[<Fact>]
let ``foundations.json requires every foundation, at the echelon-current versions`` () =
    let capabilities = (json ".echelon/foundations.json").["capabilities"]

    for name, version in [ "aegis", "1.0.0"; "forma", "0.4.1"; "folio", "0.3.0"; "limen", "0.7.1" ] do
        Assert.True(capabilities.[name].["required"].GetValue<bool>(), $"{name} must be required")
        Assert.Equal(version, str capabilities.[name].["version"])

    Assert.Equal("aegis-boundaries.json", str capabilities.["aegis"].["boundaryManifest"])

[<Fact>]
let ``the npm foundations are pinned to immutable releases and locked`` () =
    let dependencies = (json "package.json").["dependencies"]
    Assert.Equal(formaRelease, str dependencies.["@echelon-foundry/design-system"])
    Assert.Equal(folioRelease, str dependencies.["@echelon-foundry/print-components"])
    Assert.Equal("0.7.1", str dependencies.["@echelon-foundry/limen"])

    let packages = (json "package-lock.json").["packages"]

    for name, version in
        [ "@echelon-foundry/design-system", "0.4.1"
          "@echelon-foundry/print-components", "0.3.0"
          "@echelon-foundry/limen", "0.7.1" ] do
        let locked = packages.[$"node_modules/{name}"]
        Assert.Equal(version, str locked.["version"])
        Assert.StartsWith("sha512-", str locked.["integrity"])
        // What is installed is what was pinned.
        Assert.Equal(version, str (json $"node_modules/{name}/package.json").["version"])

[<Fact>]
let ``each page takes Forma and Folio from the installed packages`` () =
    for page in pages do
        let css = readRepoFile $"{page}/styles.css"

        let imports =
            Regex.Matches(css, "@import \"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

        Assert.Equal<string list>(
            [ "../node_modules/@echelon-foundry/design-system/dist/all.css"
              "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
              "../web-kernel/page.css" ],
            imports
        )

        // Folio's print stylesheet applies to print only.
        Assert.Contains("print.css\" print;", css)

        for imported in imports do
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, page, imported))), $"{page}: {imported} is not installed")

        let html = readRepoFile $"{page}/index.html"
        Assert.Contains("<link rel=\"stylesheet\" href=\"./styles.css\" />", html)
        Assert.Contains("<script type=\"module\" src=\"./main.js\"></script>", html)
        Assert.Contains("from \"../web-kernel/limen-wasm.js\"", readRepoFile $"{page}/main.js")

[<Fact>]
let ``the shared kernel registers Folio and starts Limen from the installed packages`` () =
    let kernel = readRepoFile "web-kernel/limen-wasm.js"

    for specifier in
        [ "../node_modules/@echelon-foundry/print-components/src/components/register.js"
          "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js"
          "../node_modules/@echelon-foundry/limen/dist/capabilities/files/index.js"
          "../node_modules/@echelon-foundry/limen/dist/capabilities/transfer/index.js" ] do
        Assert.Contains($"\"{specifier}\"", kernel)
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web-kernel", specifier))), specifier)

[<Fact>]
let ``the pages compose Forma's components and Folio's document primitives`` () =
    for page in pages do
        let html = readRepoFile $"{page}/index.html"

        for marker in
            [ "<ef-fault-inline class=\"ef-component-tag\">"
              "class=\"ef-fault ef-fault--inline"
              "<ef-alert class=\"ef-component-tag\">"
              "<ef-data-grid class=\"ef-component-tag\">"
              "<ef-empty-state class=\"ef-component-tag\">"
              "class=\"ef-status-lozenge\""
              "class=\"ef-field\""
              "class=\"ef-file-upload"
              "class=\"ef-dialog\""
              "<ef-print-document"
              "<ef-print-table>"
              "<ef-print-page-number>" ] do
            Assert.True(html.Contains marker, $"{page}/index.html lacks {marker}")

[<Fact>]
let ``nothing forks Forma or bypasses Limen's binding rules`` () =
    let sources =
        [ "web/index.html"; "web/styles.css"; "web/main.js"
          "web-hub/index.html"; "web-hub/styles.css"; "web-hub/main.js"
          "web-kernel/page.css"; "web-kernel/limen-wasm.js" ]

    for source in sources do
        let text = readRepoFile source
        // No local design tokens and no restyled Forma components.
        Assert.False(Regex.IsMatch(text, @"--ef-[\w-]+\s*:"), $"{source} defines a Forma token")
        Assert.False(Regex.IsMatch(text, @"(^|[\s,}])\.ef-[\w-]+[^{;]*\{", RegexOptions.Multiline), $"{source} restyles a Forma component")
        // Forma's wrappers are inert; only Folio registers elements.
        Assert.DoesNotContain("customElements.define", text)
        // State cues are data-* attributes plus CSS: never inline or bound styles.
        Assert.DoesNotContain("data-bind-style", text)
        Assert.False(Regex.IsMatch(text, @"\sstyle="""), $"{source} has an inline style")

[<Fact>]
let ``Aegis is referenced, pinned and its boundary codes are declared`` () =
    let project = readRepoFile "src/Summa.Web.Application/Summa.Web.Application.fsproj"
    Assert.Contains("<PackageReference Include=\"EchelonFoundry.Aegis.Core\" />", project)
    Assert.Contains("<PackageVersion Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />", readRepoFile "Directory.Packages.props")

    let manifest = json "aegis-boundaries.json"
    Assert.Equal("aegis/boundaries/v1", str manifest.["schema"])
    Assert.Equal("Summa", str manifest.["application"])

    let declared =
        manifest.["boundaries"].AsArray()
        |> Seq.collect (fun boundary -> boundary.["codes"].AsArray() |> Seq.map str)
        |> Set.ofSeq

    let used =
        Regex.Matches(readRepoFile "src/Summa.Web.Application/Boundary.fs", "\"(SUMMA\\.[A-Z_.]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq

    Assert.NotEmpty used
    Assert.Equal<Set<string>>(used, declared)

[<Fact>]
let ``the Limen boundary names the F# engine and the browser kernel`` () =
    let boundary = (json "limen.config.json").["boundary"]
    let paths (name: string) = boundary.[name].AsArray() |> Seq.map str |> Seq.toList

    Assert.Equal<string list>([ "src/Summa.Web.Engine"; "src/Summa.Web.Application" ], paths "engine")
    Assert.Equal<string list>([ "src/Summa.Wasm"; "web"; "web-hub"; "web-kernel" ], paths "kernel")

    for path in paths "engine" @ paths "kernel" do
        Assert.True(Directory.Exists(repoFile path), path)

[<Fact>]
let ``the engine answers the Limen contracts the installed kernel offers`` () =
    let generated (relative: string) = readRepoFile $"node_modules/@echelon-foundry/limen/dist/{relative}"

    for contract, file in
        [ Summa.Web.Application.Limen.core, "generated/core.js"
          Summa.Web.Application.Limen.files, "capabilities/files/generated/files.js"
          Summa.Web.Application.Limen.transfer, "capabilities/transfer/generated/transfer.js" ] do
        let text = generated file
        Assert.Contains($"\"{contract.Id}\"", text)
        Assert.Contains($"\"{contract.Fingerprint}\"", text)
