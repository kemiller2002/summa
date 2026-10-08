/// Environments, configuration scopes, health and safe start-up (WI-0023;
/// SUM0-038..042, SUM3-036, SUM3-037, SUM3-040, SUM3-041).
module Summa.Storage.Tests.EnvironmentTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Arca
open Summa.Ledger.Invoicing
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Environments
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private deployment (environment: string) (repository: string) (application: string) (clientId: string) =
    (configText environment "acme" repository "")
        .Replace("summa-test", application)
        .Replace("Iv23liTEST", clientId)
        .Replace("summa-eu", repository + "-eu")
    |> Deployment.parse
    |> Support.ok

[<Fact>]
let ``environments with their own repositories and credentials are isolated`` () =
    let production = deployment "production" "summa-data" "summa-production" "Iv23liPROD"
    let staging = { deployment "staging" "summa-staging" "summa-staging" "Iv23liSTAGE" with EnvironmentName = "staging" }
    let test = { deployment "test" "summa-test-data" "summa-test" "Iv23liTEST" with EnvironmentName = "test" }
    Assert.Empty(check [ production; staging; test ])

[<Fact>]
let ``production never shares a repository or credentials with another environment`` () =
    let production = deployment "production" "summa-data" "summa-production" "Iv23liPROD"
    let sharedRepo = { deployment "staging" "summa-data" "summa-staging" "Iv23liSTAGE" with EnvironmentName = "staging" }
    let found = check [ production; sharedRepo ]
    Assert.Contains(ProductionRepositoryShared "staging", found)
    Assert.Contains(SharedLocation("production", "staging"), found)

    let sharedCredentials = { deployment "test" "summa-test-data" "summa-production" "Iv23liPROD" with EnvironmentName = "test" }
    Assert.Contains(ProductionCredentialsShared "test", check [ production; sharedCredentials ])
    Assert.Contains(DuplicateEnvironment "production", check [ production; production ])

[<Fact>]
let ``staging must be production-like`` () =
    let bare = Deployment.parse """{"environment":"staging","environmentName":"staging"}""" |> Support.ok
    Assert.Equal<IsolationProblem list>([ StagingNotProductionLike "staging" ], check [ bare ])

[<Fact>]
let ``non-production environments say so on every page; production does not`` () =
    Assert.Equal(None, banner production)
    Assert.Equal(Some "SUMMA · STAGING · staging", banner { production with Environment = EnvironmentKind.Staging; EnvironmentName = "staging" })
    Assert.Equal(Some "SUMMA · LOCAL · local", banner { production with Environment = EnvironmentKind.Local; EnvironmentName = "local" })

[<Fact>]
let ``business configuration is data in the organization manifest`` () =
    let configured =
        { acme with
            Company = { LegalName = "Acme Consulting LLC"; Address = "1 Main St"; TaxId = Some "12-3456789"; Email = "billing@acme.example" }
            Invoices = { NumberPrefix = "ACME"; DefaultTermsDays = 15; PaymentInstructions = "ACH to account 000123" }
            Fiscal = { YearStartMonth = 7 } }

    let content = Organization.encode configured |> Support.ok
    let path = Organization.path "org_acme" |> Support.ok
    let stored = { Path = path; Content = content; Revision = Revision "r1" }
    Assert.Equal(configured, Organization.decode "org_acme" stored |> Support.ok)
    Assert.Equal<string list>([ "SUMMA.ORGANIZATION.INVALID_MANIFEST" ], Organization.problems { configured with Company = { configured.Company with LegalName = " " } } |> List.map Diagnostics.code)

[<Fact>]
let ``configuration is scoped, and accounting settings are never personal`` () =
    let values: Settings.Value list =
        [ { Setting = "display.dateFormat"; Scope = Settings.ApplicationScope; Value = "dd/MM/yyyy" }
          { Setting = "display.dateFormat"; Scope = Settings.PrincipalScope("org_acme", "github:1"); Value = "MM/dd/yyyy" }
          { Setting = "invoice.numberPrefix"; Scope = Settings.OrganizationScope "org_acme"; Value = "ACME" }
          { Setting = "invoice.numberPrefix"; Scope = Settings.PrincipalScope("org_acme", "github:1"); Value = "MINE" } ]

    Assert.Equal(Some "MM/dd/yyyy", Settings.resolve values "org_acme" "github:1" "display.dateFormat")
    Assert.Equal(Some "dd/MM/yyyy", Settings.resolve values "org_acme" "github:2" "display.dateFormat")
    Assert.Equal(Some "ACME", Settings.resolve values "org_acme" "github:1" "invoice.numberPrefix")
    Assert.Equal(Some "INV", Settings.resolve values "org_eu" "github:1" "invoice.numberPrefix")
    Assert.Equal<Settings.SettingProblem list>([ Settings.NotSettableAt("invoice.numberPrefix", Settings.PrincipalScope("org_acme", "github:1")) ], Settings.problems values)
    Assert.Equal(None, Settings.resolve values "org_acme" "github:1" "no.such.setting")

let private kevin = Actor.person "583231" "kevin" "corr-1"

let private founded (store: InMemoryStore) =
    let binding = bindingOf production
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    Storage.organizationNamespace production binding "org_acme" |> Support.ok

[<Fact>]
let ``system health and accounting health are separate`` () =
    let store = InMemoryStore()
    let ns = founded store
    Assert.True((Health.system store.Provider ns |> Async.RunSynchronously).StorageAvailable)
    store.Arrange InMemoryFault.CredentialRevoked
    Assert.False((Health.system store.Provider ns |> Async.RunSynchronously).StorageAvailable)

    Assert.True(Health.healthy (Health.accounting "ar" (DateOnly(2026, 12, 31)) (full ())))
    let broken = { full () with Allocations = [] }
    Assert.False(Health.healthy (Health.accounting "ar" (DateOnly(2026, 12, 31)) broken))

[<Fact>]
let ``start-up is ready only when configuration, storage, schema and books are sound`` () =
    let store = InMemoryStore()
    let ns = founded store
    Assert.Equal(Health.Ready Compatibility.ReadWrite, Health.start production store.Provider "org_acme" |> Async.RunSynchronously)

    // An organization that is not configured.
    Assert.True(match Health.start production store.Provider "org_other" |> Async.RunSynchronously with Health.Refused _ -> true | _ -> false)

    // A folder that was never set up.
    Assert.True(match Health.start production store.Provider "org_eu" |> Async.RunSynchronously with Health.Refused _ -> true | _ -> false)

    // A posted record changed after it was written.
    let run key transition =
        Commands.execute store.Provider defaultApprovalGates ns 3 { Actor = kevin; Capability = IssueInvoice; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
        |> Async.RunSynchronously
        |> Support.ok
        |> ignore

    run "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc })
    run "issue" (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b }))
    let path = RelativePath.render ns.Root + "/records/summa.entry/2026/10/JE-000001.json"
    let content = (store.State.Objects |> Map.toList |> List.find (fun (k, _) -> k.EndsWith path) |> snd).Content
    store.WriteExternally(ns.Location, path, Some(content.Replace("Invoice INV-2026-0001", "Invoice INV-2026-0002")))
    Assert.True(match Health.start production store.Provider "org_acme" |> Async.RunSynchronously with Health.Refused _ -> true | _ -> false)

[<Fact>]
let ``tests use synthetic data only: every e-mail address is on a reserved example domain`` () =
    let root = repositoryRoot ()
    let sources = Directory.GetFiles(Path.Combine(root, "tests"), "*.fs", SearchOption.AllDirectories)
    let email = Regex(@"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})")

    let offending =
        sources
        |> Array.collect (fun file -> email.Matches(File.ReadAllText file) |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toArray)
        |> Array.filter (fun domain -> not (domain.EndsWith ".example" || domain = "example.com" || domain.EndsWith ".test"))
        |> Array.distinct

    Assert.Empty offending
