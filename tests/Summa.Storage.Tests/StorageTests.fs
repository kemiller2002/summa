/// Summa's namespace, organization folders and manifests on Arca's
/// in-memory provider (SUM-DATALOC-001..005, SUM0-005..011, SUM0-033,
/// SUM0-043).
module Summa.Storage.Tests.StorageTests

open System
open Xunit
open Arca
open Summa.Storage
open Summa.Storage.Diagnostics
open Summa.Storage.Storage
open Summa.Storage.Tests.Support

[<Fact>]
let ``the in-memory provider the tests use passes Arca's conformance suite in Summa's namespace`` () =
    let ns = applicationNamespace (bindingOf production) |> ok
    let results = Conformance.run (fun () -> Conformance.inMemory ns) |> Async.RunSynchronously
    Assert.Equal(Conformance.cases.Length, results.Length)
    Assert.All(results, fun result -> Assert.True((result.Outcome = ConformanceOutcome.Passed), $"%A{result}"))

[<Fact>]
let ``initializing writes only inside Summa's own folder`` () =
    let binding, state = initialized ()
    let ns = applicationNamespace binding |> ok

    let paths =
        state.Objects
        |> Map.toList
        |> List.map (fun (address, _) -> address.Substring(address.IndexOf(':') + 1))

    Assert.NotEmpty paths
    // SUM0-005, SUM0-043, SUM-DATALOC-002: nothing at the repository root or outside deployments/prod/summa.
    Assert.All(paths, fun path -> Assert.StartsWith("deployments/prod/summa/", path))
    Assert.Contains("deployments/prod/summa/arca-manifest.json", paths)
    Assert.Contains("deployments/prod/summa/records/summa.application/summa.json", paths)
    Assert.Contains("deployments/prod/summa/datasets/org_acme/arca-manifest.json", paths)
    Assert.Contains("deployments/prod/summa/datasets/org_acme/records/summa.organization/org_acme.json", paths)
    Assert.True(ns.Root |> RelativePath.render = "deployments/prod/summa")

[<Fact>]
let ``the application manifest names Summa, its versions and where organizations live`` () =
    let binding, state = initialized ()
    let ns = applicationNamespace binding |> ok

    match read ns Application.path state with
    | ReadOutcome.Found stored ->
        let manifest = Application.decode stored |> ok
        Assert.Equal("Summa", manifest.Name)
        Assert.Equal("summa", manifest.ApplicationId)
        Assert.Equal(Organization.StorageVersion, manifest.StorageVersion)
        Assert.Equal("datasets", manifest.OrganizationsPath)
        Assert.Equal(Some 1, manifest.RecordSchemas.TryFind "summa.organization")
        Assert.Equal(Some 1, manifest.RecordSchemas.TryFind "summa.application")
        Assert.Equal(Application.ApplicationVersion, manifest.MinimumApplicationVersion)
    | ReadOutcome.Absent -> failwith "no application manifest"

[<Fact>]
let ``a namespace opens only when its Arca manifest matches the configuration`` () =
    let binding, state = initialized ()
    let ns = applicationNamespace binding |> ok
    Assert.Equal(ManifestScope.Application, (openNamespace ns (read ns manifestPath state) |> ok).Scope)

    // The same folder configured at another repository is a relocation, never a silent re-point.
    let moved =
        configText "production" "acme" "other-repo" "deployments/prod" |> Deployment.parse |> ok |> bindingOf

    let movedNs = { (applicationNamespace moved |> ok) with Location = ns.Location }
    let configured = applicationNamespace moved |> ok
    Assert.Equal<string list>([ "SUMMA.STORAGE.NOT_INITIALIZED" ], codes (openNamespace configured ReadOutcome.Absent))

    match read movedNs manifestPath state with
    | ReadOutcome.Found stored ->
        Assert.Equal<string list>([ "SUMMA.STORAGE.NAMESPACE_UNUSABLE" ], codes (openNamespace configured (ReadOutcome.Found stored)))
    | ReadOutcome.Absent -> failwith "manifest missing"

[<Fact>]
let ``an organization's folder is named by its immutable id and may be in its own repository`` () =
    let binding = bindingOf production
    let acmeNs = organizationNamespace production binding "org_acme" |> ok
    let euNs = organizationNamespace production binding "org_eu" |> ok

    Assert.Equal("deployments/prod/summa/datasets/org_acme", RelativePath.render acmeNs.Root)
    Assert.Equal("acme/summa-data", string acmeNs.Location.Repository)
    // SUM0-009: another repository, Summa's folder at its root.
    Assert.Equal("summa/datasets/org_eu", RelativePath.render euNs.Root)
    Assert.Equal("acme-eu/summa-eu", string euNs.Location.Repository)
    // SUM0-033: only configured organizations have folders; nothing is discovered by scanning.
    Assert.Equal("SUMMA.ORGANIZATION.UNKNOWN", organizationNamespace production binding "org_other" |> codeOf)

    let all = namespaces production binding |> ok
    Assert.Equal<string list>(
        [ "deployments/prod/summa"; "deployments/prod/summa/datasets/org_acme"; "summa/datasets/org_eu" ],
        all |> List.map (fun ns -> RelativePath.render ns.Root)
    )

[<Fact>]
let ``the organization manifest round-trips with every SUM0-011 field`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok

    match read ns path state with
    | ReadOutcome.Found stored ->
        let manifest = Organization.decode "org_acme" stored |> ok
        Assert.Equal(acme, manifest)
        Assert.Equal("USD", manifest.DefaultCurrency)
        Assert.Equal(1, manifest.Fiscal.YearStartMonth)
        Assert.Equal("INV", manifest.Invoices.NumberPrefix)
        Assert.Equal(30, manifest.Invoices.DefaultTermsDays)
        Assert.Equal(Organization.Accrual, manifest.Accounting.Basis)
        Assert.Equal(at, manifest.CreatedAt)
        // Read as another organization's manifest, it is refused.
        Assert.Equal("SUMMA.STORAGE.INVALID_RECORD", Organization.decode "org_eu" stored |> codeOf)
    | ReadOutcome.Absent -> failwith "no organization manifest"

[<Fact>]
let ``initializing twice never overwrites`` () =
    let binding, state = initialized ()
    let again = initializeOrganization production binding RepositoryVisibility.Private None (context "org-2") acme |> ok

    match InMemory.commit again state with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other, _ -> failwith $"expected a conflict, got %A{other}"

[<Fact>]
let ``production data is never initialized in a public repository without a recorded reason`` () =
    let binding = bindingOf production
    let refused = initializeApplication binding RepositoryVisibility.Public None (context "app")
    Assert.Equal<string list>([ "SUMMA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY" ], codes refused)

    let withoutReason = initializeApplication binding RepositoryVisibility.Public (Some { Reason = " " }) (context "app")
    Assert.Equal<string list>([ "SUMMA.STORAGE.OVERRIDE_WITHOUT_REASON" ], codes withoutReason)

    let decided = initializeApplication binding RepositoryVisibility.Public (Some { Reason = "demo data only" }) (context "app")
    Assert.True(Result.isOk decided)

    let test = configText "test" "acme" "summa-data" "" |> Deployment.parse |> ok |> bindingOf
    Assert.True(Result.isOk (initializeApplication test RepositoryVisibility.Public None (context "app")))

[<Fact>]
let ``renaming changes names, never the id or the folder, and needs the revision last read`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok

    let revision =
        match read ns path state with
        | ReadOutcome.Found stored -> stored.Revision
        | ReadOutcome.Absent -> failwith "missing"

    let renamed =
        { acme with
            DisplayName = "Acme Advisory"
            Slug = "acme-advisory" }

    let next = state |> committed (updateOrganization ns (context "rename") revision acme renamed |> ok)

    match read ns path next with
    | ReadOutcome.Found stored -> Assert.Equal(renamed, Organization.decode "org_acme" stored |> ok)
    | ReadOutcome.Absent -> failwith "missing"

    // A stale revision is a conflict, never a blind overwrite.
    let stale = updateOrganization ns (context "rename-2") revision renamed { renamed with Slug = "acme-2" } |> ok

    match InMemory.commit stale next with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other, _ -> failwith $"expected a conflict, got %A{other}"

    // The id never changes.
    Assert.Equal<string list>(
        [ "SUMMA.ORGANIZATION.INVALID_MANIFEST" ],
        codes (updateOrganization ns (context "x") revision acme { acme with OrganizationId = "org_new" })
    )

[<Fact>]
let ``a manifest that breaks a rule is never written`` () =
    let binding = bindingOf production

    let bad =
        { acme with
            DefaultCurrency = "dollars"
            Fiscal = { YearStartMonth = 13 }
            Invoices = { acme.Invoices with NumberPrefix = "INV-" } }

    let result = initializeOrganization production binding RepositoryVisibility.Private None (context "org") bad

    Assert.Equal<string list>(
        [ "SUMMA.ORGANIZATION.INVALID_CURRENCY"; "SUMMA.ORGANIZATION.INVALID_MANIFEST"; "SUMMA.ORGANIZATION.INVALID_MANIFEST" ],
        codes result
    )

[<Fact>]
let ``stored content is untrusted: a tampered manifest is refused, not used`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok

    match read ns path state with
    | ReadOutcome.Found stored ->
        let tampered = { stored with Content = stored.Content.Replace("\"USD\"", "\"XX\"") }
        Assert.Equal("SUMMA.STORAGE.INVALID_RECORD", Organization.decode "org_acme" tampered |> codeOf)
        let notCanonical = { stored with Content = stored.Content + " " }
        Assert.Equal("SUMMA.STORAGE.INVALID_RECORD", Organization.decode "org_acme" notCanonical |> codeOf)
        let extra = { stored with Content = stored.Content.Replace("\"slug\":", "\"owner\":\"x\",\"slug\":") }
        Assert.Equal("SUMMA.STORAGE.INVALID_RECORD", Organization.decode "org_acme" extra |> codeOf)
    | ReadOutcome.Absent -> failwith "missing"
