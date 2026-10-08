/// Capability-based authorization, approval gates and actors (SUM0-003,
/// SUM0-004, SUM0-022, SUM0-023, SUM2-029, SUM3-012..015).
module Summa.Storage.Tests.AccessTests

open Xunit
open Summa.Access.Access
open Summa.Access.Actor

let private human id : Principal = { PrincipalId = id; Kind = Human; DisplayName = id }
let private bot id : Principal = { PrincipalId = id; Kind = Agent; DisplayName = id }

let private roster =
    { OrganizationId = "org_acme"
      Members =
        Map.ofList
            [ "github:1", { Principal = human "github:1"; Capabilities = Grants.administrator; Revision = 1 }
              "github:2", { Principal = human "github:2"; Capabilities = Grants.bookkeeper; Revision = 1 }
              "github:3", { Principal = human "github:3"; Capabilities = Grants.accountant; Revision = 1 }
              "agent-1", { Principal = bot "agent-1"; Capabilities = Grants.forKind Agent Grants.accountant; Revision = 1 } ] }

let private codeOf =
    function
    | Ok _ -> "ok"
    | Error refusal -> refusalCode refusal

[<Fact>]
let ``being signed in grants nothing: only the roster's capabilities count`` () =
    // SUM0-003: a valid GitHub identity that is not a member may do nothing.
    Assert.Equal("SUMMA.ACCESS.NOT_A_MEMBER", authorize roster "org_acme" "github:999" ViewFinancials |> codeOf)
    Assert.Equal("ok", authorize roster "org_acme" "github:2" RecordPayment |> codeOf)
    Assert.Equal("SUMMA.ACCESS.UNAUTHORIZED", authorize roster "org_acme" "github:2" IssueInvoice |> codeOf)
    Assert.Equal("SUMMA.ACCESS.ORGANIZATION_MISMATCH", authorize roster "org_eu" "github:1" ViewFinancials |> codeOf)

[<Fact>]
let ``every capability the requirements name exists and round-trips by name`` () =
    for name in
        [ "CreateDraftInvoice"; "IssueInvoice"; "RecordPayment"; "AllocatePayment"; "CreateCreditMemo"; "PostJournalEntry"
          "ReverseJournalEntry"; "ClosePeriod"; "ReopenPeriod"; "ManageOrganization"; "ManageUsers"; "ManageSettings"
          "ReversePayment"; "PostManualAdjustment"; "WriteOffReceivable"; "RefundCustomer"; "ImportSourceTime"; "RecordExpense"
          "ProposeInvoice"; "OverrideRate"; "ManageBilling"; "OverrideInvoiceNumber" ] do
        Assert.Equal(Some name, capabilityOf name |> Option.map capabilityName)

    Assert.Equal(allCapabilities.Length, allCapabilities |> List.map capabilityName |> List.distinct |> List.length)

[<Fact>]
let ``a person executes; an agent may only prepare a gated operation for a person to approve`` () =
    Assert.Equal(Allowed, decide defaultApprovalGates roster "org_acme" "github:3" IssueInvoice)
    Assert.Equal(NeedsHumanApproval, decide defaultApprovalGates roster "org_acme" "agent-1" IssueInvoice)
    Assert.Equal(Allowed, decide defaultApprovalGates roster "org_acme" "agent-1" RecordPayment)
    // Gates are configurable: an organization may gate more.
    Assert.Equal(NeedsHumanApproval, decide (defaultApprovalGates.Add RecordPayment) roster "org_acme" "agent-1" RecordPayment)
    Assert.Equal(Refused(Unauthorized("github:2", IssueInvoice)), decide defaultApprovalGates roster "org_acme" "github:2" IssueInvoice)

[<Fact>]
let ``approval needs a different person who holds the capability`` () =
    Assert.Equal(Ok(), canApprove roster "org_acme" "github:3" "agent-1" IssueInvoice)
    Assert.True(Result.isError (canApprove roster "org_acme" "github:3" "github:3" IssueInvoice))
    Assert.True(Result.isError (canApprove roster "org_acme" "github:2" "agent-1" IssueInvoice))
    Assert.True(Result.isError (canApprove roster "org_acme" "agent-1" "github:3" IssueInvoice))

[<Fact>]
let ``only a person manages users, and an organization always keeps an administrator`` () =
    Assert.True(Result.isError (execute "github:2" (Grant("github:2", IssueInvoice)) roster))
    Assert.Equal<string list>(
        [ "SUMMA.ACCESS.CAPABILITY_NOT_FOR_KIND" ],
        execute "github:1" (Grant("agent-1", ManageUsers)) roster |> Result.mapError (List.map refusalCode) |> function Error e -> e | Ok _ -> []
    )

    let granted = execute "github:1" (Grant("github:2", IssueInvoice)) roster |> Result.defaultWith (failwithf "%A")
    Assert.True(permits granted "github:2" IssueInvoice)
    Assert.Equal(2, granted.Members["github:2"].Revision)

    Assert.Equal<string list>(
        [ "SUMMA.ACCESS.LAST_ADMINISTRATOR" ],
        execute "github:1" (Revoke("github:1", ManageUsers)) roster |> function Error e -> List.map refusalCode e | Ok _ -> []
    )

    let admitted = execute "github:1" (Admit(human "github:9", Grants.viewer)) roster |> Result.defaultWith (failwithf "%A")
    let written, removed = changed roster admitted
    Assert.Equal<string list>([ "github:9" ], written |> List.map _.Principal.PrincipalId)
    Assert.Empty removed

[<Fact>]
let ``actors: no anonymous mutation, agents are never people, and no tokens`` () =
    Assert.Empty(problems (Summa.Access.Actor.person "583231" "octocat" "corr-1"))

    let agentActor = agent "summa-agent-01" { Provider = "anthropic"; Model = "unknown"; Runtime = "claude-code" } (Some "EXE-summa.run-1") "corr-2"
    Assert.Empty(problems agentActor)
    Assert.Contains(AgentAsPerson, problems { agentActor with ActorId = "github:583231" })
    Assert.Contains(AgentWithoutIdentity, problems { agentActor with Agent = None })
    Assert.Contains(AgentAsPerson, problems { Summa.Access.Actor.person "1" "x" "c" with Agent = agentActor.Agent })
    Assert.Contains(MissingActorId, problems { Summa.Access.Actor.person "1" "x" "c" with ActorId = " " })
    Assert.Contains(MissingCorrelation, problems { Summa.Access.Actor.person "1" "x" "c" with CorrelationId = "" })
    Assert.Contains(CredentialInActor "gitHubLogin", problems { Summa.Access.Actor.person "1" "x" "c" with GitHubLogin = Some "ghp_abcdef" })
    Assert.Contains(CredentialInActor "executionId", problems { agentActor with ExecutionId = Some "Bearer abc" })
