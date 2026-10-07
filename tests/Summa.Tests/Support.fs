module Summa.Tests.Support

open System.IO

/// The repository root: the directory holding Summa.sln.
let repositoryRoot =
    let rec up (directory: DirectoryInfo | null) =
        match directory with
        | null -> failwith "Summa.sln not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Summa.sln")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

let readRepoFile (relative: string) = File.ReadAllText(Path.Combine(repositoryRoot, relative))
