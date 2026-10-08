module Summa.Contracts.Tests.Support

open System
open System.IO
open Summa.Contracts
open Summa.Contracts.ChronaBilling.V1

/// The repository root: the directory holding Summa.sln.
let repositoryRoot =
    let rec up (directory: DirectoryInfo | null) =
        match directory with
        | null -> failwith "Summa.sln not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Summa.sln")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let readRepoFile (relative: string) = File.ReadAllText(Path.Combine(repositoryRoot, relative))

let at (text: string) = DateTimeOffset.Parse text

let source revision =
    { OrganizationId = "org-1"
      ActivityId = "act-1"
      Revision = revision }

/// A valid publication to vary in tests.
let billableTime: BillableTime =
    { PublicationId = "pub-1"
      Source = source 2
      PerformerId = "github:1"
      Service =
        { BusinessDate = DateOnly(2026, 10, 6)
          Zone = "Europe/London"
          Interval = Some(at "2026-10-06T09:00:00+01:00", at "2026-10-06T09:45:00+01:00") }
      Classification =
        { ProjectId = "proj-1"
          ClientId = Some "client-1"
          EngagementId = None
          ActivityTypeId = "development"
          Description = "Build the ledger"
          BusinessPurpose = "Ship v0.1"
          Tags = [ "ledger" ] }
      ExactMinutes = 45
      BillableMinutes = 48
      Policy = { PolicyId = "legacy-six-minute-up"; Version = 1 }
      BillingReference =
        { RateReference = None
          BillingClass = None
          ContractReference = None }
      Approval = ApprovedBy("github:2", at "2026-10-07T10:00:00Z")
      Origin = Known(Manual, None, None)
      Lineage = []
      WorkItemReference = None
      Supersedes = None
      PublishedAt = at "2026-10-07T11:00:00Z" }

let paths (problems: Problem list) = problems |> List.map _.Path

/// Decodes a message of either direction.
let decodeAny (text: string) =
    match Codec.decodePublication text, Codec.decodeFeedback text with
    | Ok p, _ -> Ok(Choice1Of2 p)
    | _, Ok f -> Ok(Choice2Of2 f)
    | Error a, Error b -> Error(a @ b)

let encodeAny =
    function
    | Choice1Of2 p -> Codec.encodePublication p
    | Choice2Of2 f -> Codec.encodeFeedback f
