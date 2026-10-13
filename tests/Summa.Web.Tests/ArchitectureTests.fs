/// Architectural ownership for invoicing (WI-0029, INV-ARCH-005 to 008):
/// Praxis governs the engineering, Limen the browser boundary, Forma the
/// interactive presentation and Folio the printed invoice. INV-ARCH-001 to
/// 004 and 009 to 010 are asserted where the records and the domain are
/// (Summa.Storage.Tests, Summa.Tests).
module Summa.Web.Tests.ArchitectureTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Summa.Web.Tests.Support

[<Fact>]
let ``INV-ARCH-005: Praxis governs the engineering: every push and pull request is validated, and the work is in its records`` () =
    let workflow = readRepoFile ".github/workflows/praxis-validation.yml"
    Assert.Contains("pull_request:", workflow)
    Assert.Contains("push:", workflow)
    Assert.Contains("run: ./praxis validate", workflow)
    Assert.Contains("run: ./praxis registry check", workflow)
    // The ROS-named paths are Praxis's own and stay in use.
    Assert.True(File.Exists(Path.Combine(repositoryRoot, ".ros", "installation.json")))
    Assert.Contains("\"id\": \"WI-0029\"", readRepoFile ".ros/work/queue.json")
    Assert.Contains("AGENTS.md", readRepoFile "CLAUDE.md")

[<Fact>]
let ``INV-ARCH-006: the browser side of the invoice application is Limen's kernel and nothing else; the rules are the F# engine's`` () =
    let html = readRepoFile "app/index.html"
    // One script: the module that starts the shared Limen kernel.
    Assert.Equal<string list>([ "<script type=\"module\" src=\"./app.js\">" ], Regex.Matches(html, "<script[^>]*>") |> Seq.map _.Value |> List.ofSeq)
    Assert.False(Regex.IsMatch(html, @"\son[a-z]+\s*="), "app/index.html has an inline event handler")

    let script =
        (readRepoFile "app/app.js").Split('\n')
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//") && line.Trim() <> "")
        |> List.ofArray

    Assert.Equal<string list>([ "import { startApp } from \"../web-kernel/limen-wasm.js\";"; "await startApp();" ], script)
    // The kernel decides nothing about money: no arithmetic or parsing of amounts in browser code.
    let kernel = readRepoFile "web-kernel/limen-wasm.js"

    for rule in [ "parseFloat"; "toFixed"; "Math.round"; "Intl.NumberFormat" ] do
        Assert.False(kernel.Contains rule, $"web-kernel/limen-wasm.js contains {rule}")

[<Fact>]
let ``INV-ARCH-007: Forma presents the editor, tables, dialogs and fields, and holds none of Summa's rules`` () =
    let html = readRepoFile "app/index.html"

    for used in [ "class=\"ef-field"; "class=\"ef-button"; "class=\"ef-status-lozenge"; "class=\"ef-fault"; "class=\"ef-surface" ] do
        Assert.True(html.Contains used, $"app/index.html does not use {used}")

    Assert.Contains("@import \"../node_modules/@echelon-foundry/design-system/dist/all.css\"", readRepoFile "app/summa-app.css")
    // The installed Forma package knows nothing of Summa.
    let forma = Path.Combine(repositoryRoot, "node_modules", "@echelon-foundry", "design-system")
    Assert.True(Directory.Exists forma, "Forma is not installed")

    for file in Directory.EnumerateFiles(forma, "*.*", SearchOption.AllDirectories) |> Seq.filter (fun f -> f.EndsWith ".js" || f.EndsWith ".css") do
        Assert.False(Regex.IsMatch(File.ReadAllText file, @"\bsumma\b", RegexOptions.IgnoreCase), $"{file} mentions Summa")

[<Fact>]
let ``INV-ARCH-008: Folio composes the printed invoice and credit memo; Summa supplies their content`` () =
    let html = readRepoFile "app/index.html"
    Assert.Contains("<ef-print-document class=\"summa-document\" id=\"invoice-document\">", html)
    Assert.Contains("<ef-print-document class=\"summa-document\" id=\"credit-memo-document\">", html)
    Assert.True(Regex.Matches(html, "<ef-print-table").Count >= 2)
    Assert.Contains("@import \"../node_modules/@echelon-foundry/print-components/src/styles/print.css\"", readRepoFile "app/summa-app.css")
    // The kernel registers Folio's components from the installed package.
    Assert.Contains("\"../node_modules/@echelon-foundry/print-components/src/components/register.js\"", readRepoFile "web-kernel/limen-wasm.js")
