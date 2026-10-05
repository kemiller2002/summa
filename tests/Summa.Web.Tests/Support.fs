module Summa.Web.Tests.Support

open System.IO
open Summa.Web.Engine.Backlog

/// The repository root: the directory holding Summa.sln.
let repositoryRoot =
    let rec up (directory: DirectoryInfo) =
        match directory with
        | null -> failwith "Summa.sln not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Summa.sln")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

let repoFile (relative: string) = Path.Combine(repositoryRoot, relative)

let readRepoFile (relative: string) = File.ReadAllText(repoFile relative)

/// A work row as the server would send it, for engine tests.
let row (id: string) : WorkRow =
    { Id = id
      Title = "Write docs"
      Description = None
      Tags = [ "docs"; "web" ]
      Priority = None
      Status = "captured"
      BacklogActions = []
      Attachments = []
      LiveWorkItem = None
      Detail = None
      Raw = "{}" }
