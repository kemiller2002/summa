/// The pre-step before every Conditor authorize (WI-0052): build outputs on
/// disk at authorize time end up in the Conditor lock, so
/// tools/authorize-clean.sh removes them, refuses if any remain, and only
/// then runs Conditor.
module Summa.Tests.AuthorizeCleanTests

open System.Diagnostics
open System.IO
open Xunit
open Summa.Tests.Support

let private script = Path.Combine(repositoryRoot, "tools", "authorize-clean.sh")

/// Runs the script with these arguments; returns the exit code and stderr.
let private run (args: string list) =
    let start = ProcessStartInfo("bash", [ script ] @ args, RedirectStandardOutput = true, RedirectStandardError = true)
    use p =
        match Process.Start start with
        | null -> failwith "bash could not be started"
        | started -> started

    let error = p.StandardError.ReadToEnd()
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    p.ExitCode, error

/// A throwaway repository with these directories in it.
let private repo (dirs: string list) =
    let root = Directory.CreateTempSubdirectory("summa-clean-").FullName
    dirs |> List.iter (fun d -> Directory.CreateDirectory(Path.Combine(root, d)) |> ignore)
    root

[<Fact>]
let ``it refuses while build outputs exist, and names them`` () =
    let root = repo [ "src/App/bin/Release"; "src/App/obj"; "build/wasm"; "src/App/Code" ]

    try
        let code, error = run [ "--root"; root; "--verify-only" ]
        Assert.Equal(3, code)
        Assert.Contains("refusing", error)
        Assert.Contains("src/App/bin", error)
        Assert.Contains("src/App/obj", error)
        Assert.Contains("build", error)
        // Verifying removes nothing.
        Assert.True(Directory.Exists(Path.Combine(root, "src/App/bin")))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``it removes build outputs, then runs the command; node_modules and sources are left alone`` () =
    let root = repo [ "src/App/bin/Release"; "tests/T/obj"; "test-results"; "playwright-report"; "node_modules/pkg/bin"; "src/App/Code" ]
    let marker = Path.Combine(root, "ran")

    try
        let code, error = run [ "--root"; root; "--"; "touch"; marker ]
        Assert.True((code = 0), error)
        Assert.True(File.Exists marker)
        Assert.False(Directory.Exists(Path.Combine(root, "src/App/bin")))
        Assert.False(Directory.Exists(Path.Combine(root, "tests/T/obj")))
        Assert.False(Directory.Exists(Path.Combine(root, "test-results")))
        Assert.False(Directory.Exists(Path.Combine(root, "playwright-report")))
        Assert.True(Directory.Exists(Path.Combine(root, "node_modules/pkg/bin")))
        Assert.True(Directory.Exists(Path.Combine(root, "src/App/Code")))
        Assert.Equal(0, run [ "--root"; root; "--verify-only" ] |> fst)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``without a command it runs nothing`` () =
    let root = repo [ "src/App/bin" ]

    try
        Assert.Equal(2, run [ "--root"; root ] |> fst)
        Assert.True(Directory.Exists(Path.Combine(root, "src/App/bin")))
    finally
        Directory.Delete(root, true)
