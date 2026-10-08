/// One command, one commit (SUM0-016..021, SUM3-013): authorization against
/// the stored roster, change tokens, conflicts decided again, unknown
/// outcomes reconciled, posted records never rewritten.
module Summa.Storage.Tests.CommandTests

open System
open Xunit
open Arca
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Commands
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private kevin = Actor.person "583231" "kevin" "corr-1"
let private bot = Actor.agent "summa-agent" { Provider = "anthropic"; Model = "unknown"; Runtime = "claude-code" } (Some "EXE-summa.1") "corr-2"

let private founded () =
    let binding = bindingOf production
    let store = InMemoryStore()
    let ns = Storage.organizationNamespace production binding "org_acme" |> Support.ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    store, ns

let private run (store: InMemoryStore) ns (actor: Actor.Actor) capability key transition =
    execute store.Provider defaultApprovalGates ns 3 { Actor = actor; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }
    |> Async.RunSynchronously

let private setUp store ns =
    run store ns kevin ManageSettings "chart" (fun r -> Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc })

let private issueDraft (r: Receivables) =
    (if r.Books.IssuedFrom.ContainsKey draft.DraftId then Ok r.Books else saveDraft ledgerContext draft r.Books)
    |> Result.bind (issue ledgerContext issueRequest)
    |> Result.map (fun (books, _) -> { r with Books = books })

[<Fact>]
let ``a command is one commit of only what it changed, and a retry commits nothing`` () =
    let store, ns = founded ()
    let first = setUp store ns |> Support.ok
    Assert.True first.Receipt.IsSome
    let before = store.State.History.Length
    let issued = run store ns kevin IssueInvoice "issue-D-1" issueDraft |> Support.ok
    Assert.Equal(before + 1, store.State.History.Length)
    Assert.Contains("INV-001", issued.State.Books.Invoices |> Map.keys)
    // The same command again: the domain is idempotent, so nothing changes and nothing commits.
    let again = run store ns kevin IssueInvoice "issue-D-1" issueDraft |> Support.ok
    Assert.Equal(None, again.Receipt)
    Assert.Equal(before + 1, store.State.History.Length)

[<Fact>]
let ``authorization is checked against the stored roster before anything is written`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    let stranger = Actor.person "999" "stranger" "corr-3"

    match run store ns stranger IssueInvoice "issue-x" issueDraft with
    | Error(NotAuthorized(NotAMember _)) -> ()
    | other -> failwith $"%A{other}"

    match run store ns { kevin with ActorId = "" } IssueInvoice "issue-y" issueDraft with
    | Error(InvalidActor _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an agent may prepare but not issue: issuing needs a person`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    // Admit the agent as an accountant through the roster.
    let listing = store.Provider.List ns MemberRecord.folder |> Async.RunSynchronously |> Support.ok
    let members = listing.Entries |> List.map (fun e -> match store.Provider.Read ns e.Path |> Async.RunSynchronously |> Support.ok with ReadOutcome.Found s -> s | _ -> failwith "absent")
    let roster, revisions = MemberRecord.roster "org_acme" members |> Support.ok
    let next = Access.execute "github:583231" (Admit({ PrincipalId = bot.ActorId; Kind = Agent; DisplayName = "agent" }, Grants.forKind Agent Grants.accountant)) roster |> Support.ok
    let changes = MemberRecord.changes revisions roster next |> Support.ok
    store.Provider.Commit(Storage.operation ns (context "admit") "admit agent" changes |> Support.ok) |> Async.RunSynchronously |> Support.ok |> ignore

    match run store ns bot IssueInvoice "issue-bot" issueDraft with
    | Error(NeedsApproval IssueInvoice) -> ()
    | other -> failwith $"%A{other}"

    let drafted = run store ns bot CreateDraftInvoice "draft-bot" (fun r -> saveDraft ledgerContext draft r.Books |> Result.map (fun b -> { r with Books = b })) |> Support.ok
    Assert.True(drafted.State.Books.Drafts.ContainsKey "D-1")

[<Fact>]
let ``a concurrent change makes the command decide again on fresh state`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    let mutable interfered = false

    // Someone else writes a customer between our read and our commit.
    let other = { abc with Id = "CUST-OTHER"; Name = "Other" }

    let otherFile =
        start (openBooks Summa.Ledger.Ledger.empty |> saveCustomer ledgerContext other)
        |> FinancialRecords.toRecords
        |> Result.bind FinancialRecords.contents
        |> Support.ok
        |> Map.find "records/summa.customer/CUST-OTHER.json"
        |> snd

    let racing =
        { store.Provider with
            Commit =
                fun operation ->
                    if not interfered then
                        interfered <- true
                        store.WriteExternally(ns.Location, RelativePath.render ns.Root + "/records/summa.customer/CUST-OTHER.json", Some otherFile)

                    store.Provider.Commit operation }

    let outcome =
        execute racing defaultApprovalGates ns 3 { Actor = kevin; Capability = IssueInvoice; Summary = "issue"; IdempotencyKey = "cmd-issue-race-0001"; Transition = issueDraft }
        |> Async.RunSynchronously
        |> Support.ok

    Assert.Equal(2, outcome.Attempts)
    // Both changes survive: the other customer and our invoice.
    Assert.True(outcome.State.Books.Customers.ContainsKey "CUST-OTHER")
    Assert.True(outcome.State.Books.Invoices.ContainsKey "INV-001")

[<Fact>]
let ``a command whose domain rule now fails after a concurrent change is refused, not merged`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin IssueInvoice "issue" issueDraft |> Support.ok |> ignore
    run store ns kevin RecordPayment "pay" (recordPayment ledgerContext payment) |> Support.ok |> ignore
    let allocateAll = allocate ledgerContext allocation
    run store ns kevin AllocatePayment "allocate-1" allocateAll |> Support.ok |> ignore
    // A second allocation of the same money to the same invoice exceeds what is outstanding.
    match run store ns kevin AllocatePayment "allocate-2" (allocate ledgerContext { allocation with AllocationId = "AL-2"; JournalEntryId = "JE-9" }) with
    | Error(Rejected _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an unknown outcome is reconciled, never resent blindly`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    store.Arrange InMemoryFault.OutcomeUnknownLanded
    let landed = run store ns kevin IssueInvoice "issue-unknown" issueDraft |> Support.ok
    Assert.True landed.Receipt.IsSome
    let commits = store.State.History.Length
    // It landed once; running again finds nothing to do.
    Assert.Equal(None, (run store ns kevin IssueInvoice "issue-unknown" issueDraft |> Support.ok).Receipt)
    Assert.Equal(commits, store.State.History.Length)

    store.Arrange InMemoryFault.OutcomeUnknownLost
    let lost = run store ns kevin RecordPayment "pay-lost" (recordPayment ledgerContext payment) |> Support.ok
    Assert.True(lost.State.Payments.ContainsKey "PAY-1")
    Assert.Equal(2, lost.Attempts)

[<Fact>]
let ``posted records are never rewritten or deleted by a command`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin IssueInvoice "issue" issueDraft |> Support.ok |> ignore

    let rewrite (r: Receivables) =
        let entry = r.Books.Ledger.Entries["JE-000001"]
        Ok { r with Books = { r.Books with Ledger = { r.Books.Ledger with Entries = r.Books.Ledger.Entries.Add("JE-000001", { entry with Description = "changed" }) } } }

    match run store ns kevin PostJournalEntry "rewrite" rewrite with
    | Error(Unstorable _) -> ()
    | other -> failwith $"%A{other}"

    let drop (r: Receivables) = Ok { r with Payments = Map.empty; Allocations = [] }
    run store ns kevin RecordPayment "pay" (recordPayment ledgerContext payment) |> Support.ok |> ignore

    match run store ns kevin RecordPayment "drop" drop with
    | Error(Unstorable _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``commits stay inside the organization's folder and carry the actor, never a token`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin IssueInvoice "issue" issueDraft |> Support.ok |> ignore
    let root = RelativePath.render ns.Root

    for KeyValue(address, o) in store.State.Objects do
        Assert.Contains(root, address)
        Assert.DoesNotContain("ghp_", o.Content)

    Assert.Contains(store.State.History, fun c -> c.Message.Contains "github:583231")

[<Fact>]
let ``billing through commands: import, propose and accept each commit once, and nothing is billed twice`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    let accounts: Summa.Ledger.Billing.BillingAccounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }
    let card: Summa.Ledger.Sources.RateCard = { Rates = [ Summa.Ledger.Sources.Everyone, Summa.Ledger.Money.usd 15000L ]; Roles = Map.empty }
    let lift f (r: Receivables) = f r.Books |> Result.map (fun b -> { r with Books = b })
    run store ns kevin ManageBilling "rates" (lift (Summa.Ledger.Billing.saveRateCard ledgerContext card)) |> Support.ok |> ignore
    let import = fun r -> Summa.Ledger.Billing.importTime ledgerContext { sourceTime "pub-1" "act-1" 1 with EngagementId = None } r |> Result.map fst
    let before = store.State.History.Length
    run store ns kevin ImportSourceTime "import-pub-1" import |> Support.ok |> ignore
    Assert.Equal(before + 1, store.State.History.Length)

    let proposal id: Summa.Ledger.Billing.ProposalRequest =
        { ProposalId = id
          CustomerId = abc.Id
          EngagementId = None
          Currency = "USD"
          Time = [ "pub-1" ]
          Grouping = []
          FixedFee = false
          Milestones = []
          Expenses = []
          Manual = []
          Accounts = accounts }

    run store ns kevin ProposeInvoice "propose-P-1" (Summa.Ledger.Billing.propose ledgerContext (proposal "P-1")) |> Support.ok |> ignore

    // A second proposal for the same time is decided on what is stored: refused.
    match run store ns kevin ProposeInvoice "propose-P-2" (Summa.Ledger.Billing.propose ledgerContext (proposal "P-2")) with
    | Error(Rejected [ Summa.Ledger.Billing.Unavailable("time pub-1", "reserved by proposal P-1") ]) -> ()
    | other -> failwith $"%A{other}"

    let issuing = { issueRequest with DraftId = "PD-1" }
    let accept r = Summa.Ledger.Billing.markReady ledgerContext false "P-1" r |> Result.bind (Summa.Ledger.Billing.accept ledgerContext "P-1" issuing) |> Result.map fst
    let accepted = run store ns kevin IssueInvoice "accept-P-1" accept |> Support.ok
    Assert.Equal(Some "INV-001", (Summa.Ledger.Billing.consumed accepted.State).TryFind "time:act-1")
    // The stored books reload with the invoice and its sources, and stay sound.
    let reread = Commands.readAll store.Provider ns |> Async.RunSynchronously |> Support.ok
    let loaded = FinancialRecords.load (reread |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type)))
    Assert.Empty loaded.Problems
    Assert.Equal(Summa.Ledger.Sources.TimeSource [ { PublicationId = "pub-1"; ActivityId = "act-1"; Revision = 1; Minutes = 90 } ], loaded.State.Books.Invoices["INV-001"].Lines.Head.Source)
