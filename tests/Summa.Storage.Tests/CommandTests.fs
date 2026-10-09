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

let private company =
    { acme with
        Company = { acme.Company with LegalName = "Acme Consulting LLC"; Address = "1 Main St"; Email = "billing@acme.example" }
        Invoices = { acme.Invoices with PaymentInstructions = "ACH to account ending 0042" } }

let private requestFor draftId invoiceId n =
    Organization.issueRequest company chart draftId (DateOnly(2026, 10, 7)) invoiceId $"JE-ISS-{n}" $"OBL-{n}"

let private prepare (draftId: string) (r: Receivables) =
    saveDraft ledgerContext { draft with DraftId = draftId } r.Books
    |> Result.mapError (fun p -> [ { Summa.Ledger.Issuance.Code = "draft"; Summa.Ledger.Issuance.Explanation = $"%A{p}"; Summa.Ledger.Issuance.Resolution = "" } ])
    |> Result.bind (fun books -> Summa.Ledger.Issuance.submitForReview ledgerContext (requestFor draftId "x" 0) { r with Books = books })

[<Fact>]
let ``issuing through a command uses the organization's defaults and stores the invoice with its artifacts`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin CreateDraftInvoice "draft-A" (prepare "D-A") |> Support.ok |> ignore
    let issueA r = Summa.Ledger.Issuance.issueInvoice ledgerContext (requestFor "D-A" "INV-A" 1) r |> Result.map fst
    let issued = run store ns kevin IssueInvoice "issue-A" issueA |> Support.ok
    let invoice = issued.State.Books.Invoices["INV-A"]
    Assert.Equal("INV-2026-0001", invoice.Number)
    Assert.Equal(Summa.Ledger.Invoicing.Net 30, invoice.Terms)
    Assert.Equal("Acme Consulting LLC", invoice.Issuer.LegalName)
    let reread = Commands.readAll store.Provider ns |> Async.RunSynchronously |> Support.ok
    let loaded = FinancialRecords.load (reread |> List.filter (fun o -> Layout.keyOf o.Path |> Option.exists (fun k -> FinancialRecords.isFinancial k.Type)))
    Assert.Empty loaded.Problems
    Assert.True((issued.State.Books.Artifacts = loaded.State.Books.Artifacts))
    Assert.True((invoice = loaded.State.Books.Invoices["INV-A"]))
    Assert.Equal<(Summa.Ledger.Sources.ArtifactKind * Summa.Ledger.Issuance.ArtifactCheck) list>(
        [ Summa.Ledger.Sources.InvoiceHtml, Summa.Ledger.Issuance.Intact; Summa.Ledger.Sources.InvoiceJson, Summa.Ledger.Issuance.Intact ],
        Summa.Ledger.Issuance.verify loaded.State "INV-A"
    )

[<Fact>]
let ``two invoices issued at once never get the same number`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin CreateDraftInvoice "draft-A" (prepare "D-A") |> Support.ok |> ignore
    let prepared = run store ns kevin CreateDraftInvoice "draft-B" (prepare "D-B") |> Support.ok
    let issueA r = Summa.Ledger.Issuance.issueInvoice ledgerContext (requestFor "D-A" "INV-A" 1) r |> Result.map fst
    let issueB r = Summa.Ledger.Issuance.issueInvoice ledgerContext (requestFor "D-B" "INV-B" 2) r |> Result.map fst
    // Someone else issues D-A between our read and our commit.
    let before = prepared.State |> FinancialRecords.toRecords |> Result.bind FinancialRecords.contents |> Support.ok
    let after = prepared.State |> issueA |> Support.ok |> FinancialRecords.toRecords |> Result.bind FinancialRecords.contents |> Support.ok
    let mutable interfered = false

    let racing =
        { store.Provider with
            Commit =
                fun operation ->
                    if not interfered then
                        interfered <- true

                        for KeyValue(path, (_, content)) in after do
                            if before.TryFind path |> Option.map snd <> Some content then
                                store.WriteExternally(ns.Location, RelativePath.render ns.Root + "/" + path, Some content)

                        for KeyValue(path, _) in before do
                            if not (after.ContainsKey path) then
                                store.WriteExternally(ns.Location, RelativePath.render ns.Root + "/" + path, None)

                    store.Provider.Commit operation }

    let outcome =
        execute racing defaultApprovalGates ns 3 { Actor = kevin; Capability = IssueInvoice; Summary = "issue B"; IdempotencyKey = "cmd-issue-B-0001"; Transition = issueB }
        |> Async.RunSynchronously
        |> Support.ok

    Assert.Equal(2, outcome.Attempts)
    Assert.Equal("INV-2026-0001", outcome.State.Books.Invoices["INV-A"].Number)
    Assert.Equal("INV-2026-0002", outcome.State.Books.Invoices["INV-B"].Number)

[<Fact>]
let ``choosing an invoice number is its own capability, and a person must approve it`` () =
    let request = requestFor "D-A" "INV-A" 1
    Assert.Equal(IssueInvoice, Commands.issueCapability request)
    Assert.Equal(OverrideInvoiceNumber, Commands.issueCapability { request with NumberOverride = Some "INV-2026-0100" })
    Assert.Contains(OverrideInvoiceNumber, defaultApprovalGates)
    Assert.DoesNotContain(OverrideInvoiceNumber, Grants.bookkeeper)
    Assert.Contains(OverrideInvoiceNumber, Grants.accountant)

let private admitAgent (store: InMemoryStore) ns =
    let listing = store.Provider.List ns MemberRecord.folder |> Async.RunSynchronously |> Support.ok
    let members = listing.Entries |> List.map (fun e -> match store.Provider.Read ns e.Path |> Async.RunSynchronously |> Support.ok with ReadOutcome.Found s -> s | _ -> failwith "absent")
    let roster, revisions = MemberRecord.roster "org_acme" members |> Support.ok
    let next = Access.execute "github:583231" (Admit({ PrincipalId = bot.ActorId; Kind = Agent; DisplayName = "agent" }, Grants.forKind Agent Grants.accountant)) roster |> Support.ok
    let changes = MemberRecord.changes revisions roster next |> Support.ok
    store.Provider.Commit(Storage.operation ns (context "admit") "admit agent" changes |> Support.ok) |> Async.RunSynchronously |> Support.ok |> ignore

[<Fact>]
let ``an agent's command keeps who, of what kind, from where and in which run, in the stored audit`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    admitAgent store ns
    let agentContext = contextFor bot ledgerContext.When "summa-agent" (Some "TE-9821") None
    Assert.Equal(Some "agent", agentContext.Provenance |> Option.map _.ActorKind)

    let drafted = run store ns bot CreateDraftInvoice "draft-bot" (fun r -> saveDraft agentContext draft r.Books |> Result.map (fun b -> { r with Books = b })) |> Support.ok
    Assert.True drafted.Receipt.IsSome

    // Read back from the store: the audit event is an agent's, with its run and source.
    let loaded = FinancialRecords.load (readAll store.Provider ns |> Async.RunSynchronously |> Support.ok |> List.filter (fun o -> match Layout.keyOf o.Path with Some k -> FinancialRecords.isFinancial k.Type | None -> false))
    let event = loaded.State.Books.Ledger.Audit |> List.find (fun a -> a.Subject = draft.DraftId && a.Who = bot.ActorId)
    Assert.Equal(agentContext.Provenance, event.Provenance)
    Assert.Equal(Some "EXE-summa.1", event.Provenance |> Option.bind _.ExecutionId)
    Assert.Equal(Some "anthropic", event.Provenance |> Option.bind _.Agent |> Option.map _.Provider)

    // A service is automation, never a person (INV-PROV-001).
    let service = { bot with Kind = Service; Agent = None; ActorId = "summa-import" }
    Assert.Equal(Some "automation", (contextFor service ledgerContext.When "import" None None).Provenance |> Option.map _.ActorKind)

[<Fact>]
let ``an agent cannot void, apply credit, reverse a payment or change billing terms without a person`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    admitAgent store ns

    for capability in [ VoidInvoice; ApplyCreditMemo; ReversePayment; ManageBilling; OverrideRate ] do
        match run store ns bot capability $"gated-{capability}" (fun r -> Ok r) with
        | Error(NeedsApproval c) -> Assert.Equal(capability, c)
        | other -> failwith $"{capability}: %A{other}"

[<Fact>]
let ``audit events are append-only: a command can neither rewrite nor drop one`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore

    let rewrite (r: Receivables) =
        let ledger = r.Books.Ledger
        Ok { r with Books = { r.Books with Ledger = { ledger with Audit = ledger.Audit |> List.map (fun a -> { a with Who = "someone-else" }) } } }

    match run store ns kevin ManageSettings "rewrite-audit" rewrite with
    | Error(Unstorable _) -> ()
    | other -> failwith $"%A{other}"

    let drop (r: Receivables) = Ok { r with Books = { r.Books with Ledger = { r.Books.Ledger with Audit = [] } } }

    match run store ns kevin ManageSettings "drop-audit" drop with
    | Error(Unstorable _) -> ()
    | other -> failwith $"%A{other}"

// ---- The authorized agent interface (WI-0029, INV-AGENT-001) --------------------------------

let private accounts: Summa.Ledger.Billing.BillingAccounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }

let private withTime store ns =
    setUp store ns |> Support.ok |> ignore
    run store ns kevin ManageBilling "rates" (fun r -> Summa.Ledger.Billing.saveRateCard ledgerContext { Rates = [ Summa.Ledger.Sources.ForCustomer abc.Id, Summa.Ledger.Money.usd 20000L ]; Roles = Map.empty } r.Books |> Result.map (fun b -> { r with Books = b }))
    |> Support.ok
    |> ignore

    run store ns kevin ImportSourceTime "import" (fun r -> Summa.Ledger.Billing.importTime ledgerContext (sourceTime "pub-1" "act-1" 1) r |> Result.map fst)
    |> Support.ok
    |> ignore

[<Fact>]
let ``an agent the roster lets propose turns a request in words into a stored proposal, and nothing more`` () =
    let store, ns = founded ()
    withTime store ns
    admitAgent store ns
    let request = "Invoice ABC Corp for all approved work from October 1 through October 31 at the contracted rate"

    match execute store.Provider defaultApprovalGates ns 3 (agentRequest bot ledgerContext.When accounts "P-AG-1" request) |> Async.RunSynchronously with
    | Ok outcome ->
        Assert.True outcome.Receipt.IsSome
        let proposal = outcome.State.Books.Proposals["P-AG-1"]
        Assert.Equal(Summa.Ledger.Invoicing.Proposed, proposal.State)
        Assert.True outcome.State.Books.Invoices.IsEmpty
        Assert.Equal(Some "agent", proposal.Contributions.Head.Provenance |> Option.map _.ActorKind)
        Assert.Equal(Some $"request: {request}", proposal.Contributions.Head.Provenance |> Option.bind _.Reason)
        // Again: the same proposal, nothing committed.
        let again = execute store.Provider defaultApprovalGates ns 3 (agentRequest bot ledgerContext.When accounts "P-AG-1" request) |> Async.RunSynchronously |> Support.ok
        Assert.Equal(None, again.Receipt)
    | Error failure -> failwith $"%A{failure}"

[<Fact>]
let ``the agent interface asks rather than guesses, and refuses an agent the roster does not know`` () =
    let store, ns = founded ()
    withTime store ns
    admitAgent store ns

    match execute store.Provider defaultApprovalGates ns 3 (agentRequest bot ledgerContext.When accounts "P-AG-2" "Invoice Acme for approved work") |> Async.RunSynchronously with
    | Error(Rejected [ Summa.Ledger.AgentRequests.WhichCustomer("Acme", []) ]) -> ()
    | other -> failwith $"%A{other}"

    let stranger = Actor.agent "other-agent" { Provider = "x"; Model = "y"; Runtime = "z" } None "corr-9"

    match execute store.Provider defaultApprovalGates ns 3 (agentRequest stranger ledgerContext.When accounts "P-AG-3" "Invoice ABC Corp for approved work") |> Async.RunSynchronously with
    | Error(NotAuthorized _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``what a change did is stored with its audit event and read back`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    run store ns kevin CreateDraftInvoice "draft" (fun r -> saveDraft ledgerContext draft r.Books |> Result.map (fun b -> { r with Books = b })) |> Support.ok |> ignore

    run store ns kevin CreateDraftInvoice "draft-terms" (fun r ->
        saveDraft ledgerContext { r.Books.Drafts[draft.DraftId] with Terms = Some(Net 15) } r.Books |> Result.map (fun b -> { r with Books = b }))
    |> Support.ok
    |> ignore

    let objects = readAll store.Provider ns |> Async.RunSynchronously |> Support.ok
    let loaded = FinancialRecords.load (objects |> List.filter (fun o -> match Layout.keyOf o.Path with Some k -> FinancialRecords.isFinancial k.Type | None -> false))
    // Stored events come back in path order, not time order: find the change by what it says.
    let changes = loaded.State.Books.Ledger.Audit |> List.filter (fun a -> a.Subject = draft.DraftId) |> List.choose _.Change
    Assert.Contains({ Version = Some 2; Outcome = "applied"; Changed = [ "terms: default -> Net 15" ] }, changes)
    Assert.Contains({ Version = Some 1; Outcome = "applied"; Changed = [] }, changes)

// ---- Arca 0.4.0: namespace tokens and erasure (WI-0045) ---------------------------------------

[<Fact>]
let ``a commit elsewhere in the repository does not make a decided change stale`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    let state = store.Provider.NamespaceState ns |> Async.RunSynchronously |> Support.ok
    let objects = readAll store.Provider ns |> Async.RunSynchronously |> Support.ok
    let request = { Actor = kevin; Capability = CreateDraftInvoice; Summary = "draft"; IdempotencyKey = "cmd-draft-elsewhere-0001"; Transition = (fun (r: Receivables) -> saveDraft ledgerContext draft r.Books |> Result.map (fun b -> { r with Books = b })) }

    let operation =
        match decide defaultApprovalGates ns state objects request with
        | Ok(_, Some operation) -> operation
        | other -> failwith $"%A{other}"

    // Another organization, in the same repository, commits in between.
    let both = { production with Organizations = production.Organizations @ [ { production.Organizations.Head with Id = "org_other"; Slug = "other"; DisplayName = "Other" } ] }
    let otherManifest = Storage.manifestFor (Deployment.organization both "org_other" |> Option.get) Support.at
    let found = Governance.found both (bindingOf both) RepositoryVisibility.Private None (context "found-other") otherManifest { PrincipalId = "github:7"; Kind = Human; DisplayName = "someone" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    Assert.NotEqual(state.RepositoryToken, (store.Provider.NamespaceState ns |> Async.RunSynchronously |> Support.ok).RepositoryToken)

    // Summa's change is held to its own namespace only, so it still applies.
    Assert.True(Result.isOk (store.Provider.Commit operation |> Async.RunSynchronously))

    // A change inside the namespace does make it stale.
    let state2 = store.Provider.NamespaceState ns |> Async.RunSynchronously |> Support.ok
    let objects2 = readAll store.Provider ns |> Async.RunSynchronously |> Support.ok

    let second =
        match decide defaultApprovalGates ns state2 objects2 { request with IdempotencyKey = "cmd-draft-elsewhere-0002"; Transition = (fun r -> saveCustomer ledgerContext { abc with Email = "new@abc.example" } r.Books |> fun b -> Ok { r with Books = b }) } with
        | Ok(_, Some operation) -> operation
        | other -> failwith $"%A{other}"

    run store ns kevin ManageBilling "inside" (fun r -> Ok { r with Books = saveCustomer ledgerContext { abc with Email = "other@abc.example" } r.Books }) |> Support.ok |> ignore

    match store.Provider.Commit second |> Async.RunSynchronously with
    | Error(StorageFailure.StaleNamespaceToken _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``books with an erased financial record are refused, never kept on without it`` () =
    let store, ns = founded ()
    setUp store ns |> Support.ok |> ignore
    let objects = readAll store.Provider ns |> Async.RunSynchronously |> Support.ok

    let audit =
        objects
        |> List.find (fun o -> match Layout.keyOf o.Path with Some k -> k.Type = FinancialRecords.auditType | None -> false)

    let key = Layout.keyOf audit.Path |> Option.get
    let schema = FinancialRecords.schemas |> List.find (fun s -> s.Type = key.Type)
    let valid = Integrity.validate key schema Record.DefaultMaxBytes audit |> Support.ok
    let request = Erasure.request audit.Path valid ledgerContext.When "test erasure" |> Support.ok
    let meta = metadata kevin "erase" "erase-audit-0001" |> Support.ok
    let operation = Erasure.operation ns meta [ request ] |> Support.ok
    Erasure.commit store.Provider operation |> Async.RunSynchronously |> Support.ok |> ignore

    match readAll store.Provider ns |> Async.RunSynchronously with
    | Error(Untrustworthy [ Diagnostics.InvalidStoredRecord(_, why) ]) -> Assert.Contains("erased", why)
    | other -> failwith $"%A{other}"
