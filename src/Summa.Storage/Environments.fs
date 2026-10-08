/// Environment isolation and visibility (SUM0-038..040, SUM3-040): local,
/// test, staging and production each have their own data location,
/// credentials and runtime indexes, production data never shares a
/// repository with anything else, and every non-production environment is
/// obvious on screen.
///
/// Pure.
module Summa.Storage.Environments

open Arca
open Summa.Storage.Deployment

/// Why a set of deployments does not keep its environments apart.
type IsolationProblem =
    /// Two environments would store into the same repository, branch and folder.
    | SharedLocation of first: string * second: string
    /// Production shares a data repository with a non-production environment.
    | ProductionRepositoryShared of other: string
    /// Production signs in with another environment's application or client id.
    | ProductionCredentialsShared of other: string
    /// An environment listed twice.
    | DuplicateEnvironment of name: string
    /// Staging must be production-like: storage and sign-in both configured.
    | StagingNotProductionLike of name: string
    /// Two environments would keep their artifacts (PDFs) in one store.
    | SharedArtifacts of first: string * second: string

let private kindName =
    function
    | EnvironmentKind.Local -> "local"
    | EnvironmentKind.Test -> "test"
    | EnvironmentKind.Staging -> "staging"
    | EnvironmentKind.Production -> "production"

let private locations (config: DeploymentConfig) =
    (config.Location :: (config.Organizations |> List.map _.Location))
    |> List.choose id
    |> List.distinct

let private repositoryOf (l: LocationConfig) = (l.Owner.ToLowerInvariant(), l.Repository.ToLowerInvariant())

/// Every reason the deployments do not keep their environments apart.
let check (deployments: DeploymentConfig list) : IsolationProblem list =
    let named = deployments |> List.map (fun d -> d.EnvironmentName, d)

    let duplicates =
        named |> List.countBy fst |> List.filter (fun (_, n) -> n > 1) |> List.map (fst >> DuplicateEnvironment)

    let pairs =
        [ for i, (a, first) in List.indexed named do
              for b, second in List.skip (i + 1) named -> (a, first), (b, second) ]

    let shared =
        pairs
        |> List.collect (fun ((a, first), (b, second)) ->
            [ for x in locations first do
                  for y in locations second do
                      if repositoryOf x = repositoryOf y && x.Branch = y.Branch && x.BasePath = y.BasePath then
                          SharedLocation(a, b)
              if first.Artifacts = second.Artifacts then
                  SharedArtifacts(a, b)

              let production, other, otherName =
                  if first.Environment = EnvironmentKind.Production then Some first, second, b
                  elif second.Environment = EnvironmentKind.Production then Some second, first, a
                  else None, first, a

              match production with
              | Some p when other.Environment <> EnvironmentKind.Production ->
                  let productionRepos = locations p |> List.map repositoryOf |> Set.ofList

                  if locations other |> List.exists (fun l -> productionRepos.Contains(repositoryOf l)) then
                      ProductionRepositoryShared otherName

                  match p.Identity, other.Identity with
                  | Some pi, Some oi when pi.ClientId = oi.ClientId || pi.Application = oi.Application -> ProductionCredentialsShared otherName
                  | _ -> ()
              | _ -> () ])
        |> List.distinct

    let staging =
        deployments
        |> List.filter (fun d -> d.Environment = EnvironmentKind.Staging && (d.Location.IsNone || d.Identity.IsNone))
        |> List.map (fun d -> StagingNotProductionLike d.EnvironmentName)

    duplicates @ shared @ staging

/// The banner a non-production environment shows on every page (SUM0-040);
/// production shows none.
let banner (config: DeploymentConfig) : string option =
    match config.Environment with
    | EnvironmentKind.Production -> None
    | kind -> Some $"SUMMA · {(kindName kind).ToUpperInvariant()} · {config.EnvironmentName}"
