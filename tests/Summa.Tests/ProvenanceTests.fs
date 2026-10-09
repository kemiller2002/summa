/// Audit provenance and agent execution safety (WI-0028): every material
/// event keeps who, of what kind, from where and in which run (SUM2-028,
/// INV-AUD-002, INV-PROV-001, INV-PROV-002, INV-PROV-007); a person's change
/// to an agent's proposal is appended, never written over (INV-AGENT-005);
/// a proposal explains itself (INV-AGENT-004); an agent is given only the
/// context it needs (SUM0-049).
module Summa.Tests.ProvenanceTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Billing
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.BillingTests

let private agent: Context =
    { context with
        Who = "summa-agent"
        Provenance =
            Some
                { ActorKind = "agent"
                  Agent = Some { Provider = "anthropic"; Model = "claude"; Runtime = "claude-code" }
                  ExecutionId = Some "EXE-summa.42"
                  SourceSystem = Some "chrona"
                  SourceId = Some "TE-9821"
                  Reason = None } }

let private person: Context =
    { context with
        Who = "github:583231"
        Provenance = Some { ActorKind = "human"; Agent = None; ExecutionId = None; SourceSystem = Some "summa-app"; SourceId = None; Reason = None } }

let private proposed () =
    fresh ()
    |> importAll [ time "PUB-1" "A-1" 120 "p-1" "apollo"; time "PUB-2" "A-2" 60 "p-1" "zeus" ]
    |> withRates [ ForCustomer abc.Id, usd 15000L ]
    |> propose agent (proposalFor [ "PUB-1" ])
    |> ok

[<Fact>]
let ``an agent's action is recorded as an agent's, with its identity, run and source`` () =
    let r = proposed ()
    let event = r.Books.Ledger.Audit |> List.find (fun a -> a.What = "proposal-created")
    Assert.Equal(agent.Provenance, event.Provenance)
    Assert.Equal(Some "EXE-summa.42", event.Provenance |> Option.bind _.ExecutionId)
    Assert.Equal(Some "TE-9821", event.Provenance |> Option.bind _.SourceId)

[<Fact>]
let ``a person's change to an agent's proposal is appended; the agent's contribution stays`` () =
    let r = proposed ()
    let changed = overrideRate person "P-1" 0 (usd 20000L) "agreed in the kickoff" r |> ok
    let proposal = changed.Books.Proposals["P-1"]

    match proposal.Contributions with
    | [ first; second ] ->
        Assert.Equal("proposed", first.What)
        Assert.Equal(agent.Provenance, first.Provenance)
        Assert.Equal("github:583231", second.Who)
        Assert.Equal(Some "human", second.Provenance |> Option.map _.ActorKind)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a proposal explains its sources, what it left out, its grouping, rates, terms and assumptions`` () =
    let r = proposed ()
    let explanation = explain r r.Books.Proposals["P-1"] (Net 30)
    Assert.Contains(explanation.Selected, fun s -> s.Contains "time A-1")
    Assert.Contains(explanation.Excluded, fun s -> s.StartsWith "time A-2")
    Assert.Equal("time grouped by byproject", explanation.Grouping)
    Assert.Contains(explanation.Rates, fun s -> s.Contains "the customer's rate")
    Assert.Equal("Net 30 (from the customer)", explanation.Terms)
    Assert.Contains(explanation.Assumptions, fun s -> s.StartsWith "tax:")

[<Fact>]
let ``an agent preparing a proposal for one customer reads only that customer's records`` () =
    let other = { abc with Id = "CUST-XYZ"; Name = "XYZ Ltd" }
    let r = proposed ()
    let r = { r with Books = saveCustomer context other r.Books }
    let r = importAll [ { time "PUB-9" "A-9" 30 "p-2" "hermes" with ClientId = Some other.Id } ] r

    match agentContext r abc.Id None with
    | Some scoped ->
        Assert.Equal(abc.Id, scoped.Customer.Id)
        Assert.All(scoped.Time, fun t -> Assert.Equal(Some abc.Id, t.ClientId))
        Assert.DoesNotContain(scoped.Time, fun t -> t.ActivityId = "A-9")
        Assert.All(scoped.Proposals, fun p -> Assert.Equal(abc.Id, p.CustomerId))
    | None -> failwith "no context"

    Assert.Equal(None, agentContext r "CUST-NONE" None |> Option.map _.Customer.Id)

[<Fact>]
let ``proposed by an agent, changed and issued by a person: the trail keeps all three`` () =
    let r = proposed ()
    let changed = overrideRate person "P-1" 0 (usd 20000L) "agreed in the kickoff" r |> ok
    let ready = markReady person false "P-1" changed |> ok
    let issued, invoice = accept person "P-1" issuing ready |> ok
    let trail = issued.Books.Ledger.Audit |> List.filter (fun a -> a.Subject = "P-1" || a.Subject = invoice.InvoiceId)
    let kinds = trail |> List.map (fun a -> a.What, a.Provenance |> Option.map _.ActorKind)
    Assert.Contains(("proposal-created", Some "agent"), kinds)
    Assert.Contains(kinds, fun (what, kind) -> what.Contains "rate" && kind = Some "human")
    Assert.Contains(("invoice-issued", Some "human"), kinds)
    // The proposal keeps every contribution, the agent's first.
    Assert.Equal<string list>([ "agent"; "human"; "human" ], issued.Books.Proposals["P-1"].Contributions |> List.choose (fun c -> c.Provenance |> Option.map _.ActorKind) |> List.truncate 3)
