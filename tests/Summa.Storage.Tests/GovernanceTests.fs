/// Bootstrap administrators from the deployment's configuration; no
/// first-opener takeover outside an explicitly local environment; member
/// records on Arca.
module Summa.Storage.Tests.GovernanceTests

open Xunit
open Arca
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Governance
open Summa.Storage.Tests.Support

let private organization (administrators: string list) : Deployment.OrganizationConfig =
    { Id = "org_acme"
      DisplayName = "Acme Consulting"
      Slug = "acme"
      DefaultCurrency = "USD"
      TimeZone = "UTC"
      Location = None
      Administrators = administrators }

let private nobody = empty "org_acme"
let private person id : Principal = { PrincipalId = id; Kind = Human; DisplayName = id }

let private rosterOf (members: (string * Set<Capability>) list) =
    { OrganizationId = "org_acme"
      Members = members |> List.map (fun (id, c) -> id, { Principal = person id; Capabilities = c; Revision = 1 }) |> Map.ofList }

let private refused =
    function
    | Refused _ -> true
    | _ -> false

[<Fact>]
let ``only a listed account sets a new organization up`` () =
    let listed = organization [ "583231" ]
    Assert.Equal(Found, decide EnvironmentKind.Production listed true nobody "github:583231")
    Assert.Equal(Refused "Only Acme Consulting's listed administrators can set it up.", decide EnvironmentKind.Production listed true nobody "github:1001")
    Assert.True(refused (decide EnvironmentKind.Production listed true nobody "gitlab:583231"))

[<Fact>]
let ``with no administrators listed nothing is set up, except in an explicitly local environment`` () =
    let unlisted = organization []

    match decide EnvironmentKind.Production unlisted true nobody "github:583231" with
    | Refused reason -> Assert.Contains("Production organizations are set up only by the GitHub accounts the configuration lists", reason)
    | other -> failwith $"%A{other}"

    Assert.True(refused (decide EnvironmentKind.Staging unlisted true nobody "github:583231"))
    Assert.Equal(Found, decide EnvironmentKind.Local unlisted true nobody "github:583231")
    Assert.True(refused (decide EnvironmentKind.Local (organization [ "1" ]) true nobody "github:583231"))

[<Fact>]
let ``an organization with no listed administrator is held until a listed account confirms`` () =
    let listed = organization [ "583231" ]
    let takenOver = rosterOf [ "github:1001", Grants.administrator ]
    Assert.Equal(NeedsConfirmation false, decide EnvironmentKind.Production listed false takenOver "github:1001")
    Assert.Equal(NeedsConfirmation true, decide EnvironmentKind.Production listed false takenOver "github:583231")
    Assert.Equal(Proceed, decide EnvironmentKind.Production listed false (rosterOf [ "github:583231", Grants.administrator ]) "github:1001")
    let confirmed = administrator (person "github:583231") takenOver
    Assert.True(confirmed.Capabilities.Contains ManageUsers)
    Assert.Equal(1, confirmed.Revision)

[<Fact>]
let ``founding is one commit: manifests and the founder's membership, nothing overwritten`` () =
    let binding = bindingOf production
    let state = InMemory.empty |> committed (Storage.initializeApplication binding RepositoryVisibility.Private None (context "app") |> ok)
    let founder = person "github:583231"
    let operation = found production binding RepositoryVisibility.Private None (context "found") acme founder |> ok
    Assert.Equal(3, operation.Changes.Length)
    let next = state |> committed operation

    let ns = Storage.organizationNamespace production binding "org_acme" |> ok
    let listing = InMemory.list ns MemberRecord.folder next |> fst |> ok

    let stored =
        listing.Entries
        |> List.map (fun entry ->
            match read ns entry.Path next with
            | ReadOutcome.Found s -> s
            | ReadOutcome.Absent -> failwith "listed but absent")

    let roster, revisions = MemberRecord.roster "org_acme" stored |> ok
    Assert.Equal<string list>([ "github:583231" ], roster.Members |> Map.toList |> List.map fst)
    Assert.True(permits roster "github:583231" ManageUsers)
    Assert.Equal(Proceed, decide EnvironmentKind.Production (organization [ "583231" ]) false roster "github:1001")

    // A roster change is one commit of only the members it changed.
    let admitted = execute "github:583231" (Admit(person "github:7", Grants.bookkeeper)) roster |> Result.defaultWith (failwithf "%A")
    let changes = MemberRecord.changes revisions roster admitted |> ok
    Assert.Equal(1, changes.Length)
    let after = next |> committed (Storage.operation ns (context "admit") "admit github:7" changes |> ok)
    let again = InMemory.list ns MemberRecord.folder after |> fst |> ok
    Assert.Equal(2, again.Entries.Length)

    // Founding again conflicts rather than overwriting.
    let second = found production binding RepositoryVisibility.Private None (context "found-2") acme (person "github:9") |> ok

    match InMemory.commit second after with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other, _ -> failwith $"expected a conflict, got %A{other}"

[<Fact>]
let ``member records are untrusted: a forged capability or a person-only grant to an agent is refused`` () =
    let membership = { Principal = person "github:5"; Capabilities = Grants.viewer; Revision = 1 }
    let content = MemberRecord.encode membership |> ok
    let p = MemberRecord.path "github:5" |> ok
    let stored = { Path = p; Content = content; Revision = Revision "r1" }
    Assert.Equal(membership, MemberRecord.decode stored |> ok)
    Assert.Equal("github_583231", MemberRecord.idOf "github:583231")

    let forged = { stored with Content = content.Replace("\"ViewFinancials\"", "\"Everything\"") }
    Assert.True(Result.isError (MemberRecord.decode forged))

    let agentAdmin =
        { stored with
            Content = content.Replace("\"Human\"", "\"Agent\"").Replace("\"ViewFinancials\"", "\"ManageUsers\"") }

    Assert.True(Result.isError (MemberRecord.decode agentAdmin))

    let elsewhere = { stored with Path = MemberRecord.path "github:6" |> ok }
    Assert.True(Result.isError (MemberRecord.decode elsewhere))
