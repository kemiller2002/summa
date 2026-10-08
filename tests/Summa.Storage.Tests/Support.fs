module Summa.Storage.Tests.Support

open System
open Arca
open Summa.Storage
open Summa.Storage.Diagnostics
open Summa.Storage.Storage

let ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let codes (result: Result<'a, Diagnostic list>) =
    match result with
    | Ok _ -> []
    | Error diagnostics -> diagnostics |> List.map code

let codeOf (result: Result<'a, Diagnostic>) =
    match result with
    | Ok _ -> "ok"
    | Error diagnostic -> code diagnostic

let configText (environment: string) (owner: string) (repository: string) (basePath: string) =
    """{"environment":"ENV","environmentName":"ENV","location":{"owner":"OWNER","repository":"REPOSITORY","branch":"main","basePath":"BASE"},"identity":{"exchange":"https://fides.test","application":"summa-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://summa.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","defaultCurrency":"USD","timeZone":"America/New_York","administrators":["583231"]},{"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","defaultCurrency":"EUR","timeZone":"Europe/Berlin","administrators":["583231"],"location":{"owner":"acme-eu","repository":"summa-eu","branch":"main","basePath":""}}]}"""
        .Replace("ENV", environment)
        .Replace("OWNER", owner)
        .Replace("REPOSITORY", repository)
        .Replace("BASE", basePath)

let production = configText "production" "acme" "summa-data" "deployments/prod" |> Deployment.parse |> ok

let bindingOf config = binding config |> ok

let at = DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero)

let context (key: string) =
    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:583231" |> ok }
      ProviderIdentity = Some "octocat"
      CorrelationId = CorrelationId.create "req-1" |> ok
      IdempotencyKey = IdempotencyKey.create $"op-{key}-0001" |> ok
      At = at }

let acme = manifestFor (Deployment.organization production "org_acme" |> Option.get) at

let committed (operation: Operation) (state: InMemoryState) =
    match InMemory.commit operation state with
    | Ok _, next -> next
    | Error failure, _ -> failwith $"%A{failure}"

let read ns path state = InMemory.read ns path state |> fst |> ok

let manifestPath = Layout.manifestPath |> ok

/// A deployment with Summa's namespace and Acme initialized.
let initialized () =
    let binding = bindingOf production

    let state =
        InMemory.empty
        |> committed (initializeApplication binding RepositoryVisibility.Private None (context "app") |> ok)
        |> committed (initializeOrganization production binding RepositoryVisibility.Private None (context "org") acme |> ok)

    binding, state
