/// Billing models (WI-0025): hourly billing from approved Chrona time,
/// fixed fees, milestones and reimbursable expenses, through a reviewed
/// proposal, with rate provenance and double-billing protection (v0.2
/// §14-17, v0.1 §17, INV-SOURCE, INV-CHR, INV-RATE, INV-TERM).
module Summa.Tests.BillingTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Billing
open Summa.Ledger.Reports
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let billingAccounts =
    { TimeRevenue = "revenue"
      FeeRevenue = "fees"
      ReimbursedExpenses = "reimbursed" }

let private extra =
    [ { Id = "fees"; Code = "4100"; Name = "Fixed-Fee Revenue"; Type = Revenue; Active = true }
      { Id = "reimbursed"; Code = "4200"; Name = "Reimbursed Expenses"; Type = Revenue; Active = true }
      { Id = "travel"; Code = "6200"; Name = "Travel"; Type = Expense; Active = true } ]

/// ABC as a customer and the billing accounts, nothing billed yet.
let fresh () =
    let ledger = extra |> List.fold (fun l a -> addAccount context a l |> ok) chart
    start (openBooks ledger |> saveCustomer context abc)

let at (text: string) = DateTimeOffset.Parse text

/// Approved billable time Chrona published for ABC.
let time (publication: string) (activity: string) (minutes: int) (person: string) (project: string) : SourceTime =
    { PublicationId = publication
      OrganizationId = "org-1"
      ActivityId = activity
      Revision = 1
      PerformerId = person
      BusinessDate = DateOnly(2026, 10, 6)
      ProjectId = project
      ClientId = Some abc.Id
      EngagementId = None
      ActivityTypeId = "consulting"
      Description = $"Work on {project}"
      ExactMinutes = minutes
      BillableMinutes = minutes
      Approved = true
      RateReference = None
      Origin = OriginKnown("manual", None, None)
      Lineage = []
      WorkItem = None
      Supersedes = None
      PublishedAt = at "2026-10-07T11:00:00Z" }

let importAll (times: SourceTime list) (r: Receivables) =
    times |> List.fold (fun state t -> importTime context t state |> ok |> fst) r

let withRates (rates: (RateScope * Money) list) (r: Receivables) =
    { r with Books = saveRateCard context { Rates = rates; Roles = Map.empty } r.Books |> ok }

let proposalFor (publications: string list) =
    { ProposalId = "P-1"
      CustomerId = abc.Id
      EngagementId = None
      Currency = "USD"
      Time = publications
      Grouping = [ ByProject ]
      FixedFee = false
      Milestones = []
      Expenses = []
      Manual = []
      Accounts = billingAccounts }

let issuing = { request with DraftId = "PD-1" }

let private balancesOk (r: Receivables) =
    let tb = trialBalance "USD" (DateOnly(2026, 12, 31)) r.Books.Ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)

/// Three entries on two projects: 150.00 by system default, 175.00 for
/// github:2 by person rate, and 200.00 on project B by agreement.
let private hourly () =
    fresh ()
    |> withRates [ Everyone, usd 15000L; ForPerson "github:2", usd 17500L; ForProject "PRJ-B", usd 20000L ]
    |> importAll [ time "pub-1" "act-1" 90 "github:1" "PRJ-A"; time "pub-2" "act-2" 45 "github:2" "PRJ-A"; time "pub-3" "act-3" 60 "github:1" "PRJ-B" ]

let private sourcesOf (l: InvoiceLine) =
    match l.Source with
    | TimeSource refs -> refs |> List.map _.PublicationId
    | _ -> []

[<Fact>]
let ``rates resolve by explicit precedence and never by guess`` () =
    let card =
        { Rates =
            [ Everyone, usd 10000L
              ForRole "senior", usd 12000L
              ForPerson "p", usd 13000L
              ForCustomer "c", usd 14000L
              ForProject "pr", usd 15000L
              ForEngagement "e", usd 16000L ]
          Roles = Map.ofList [ "p", "senior"; "q", "senior" ] }

    let source c e pr p = resolveRate card c e pr p |> Option.map (fun (m, prov) -> m.Minor, prov.RateSource)
    Assert.Equal(Some(16000L, EngagementAgreement), source "c" (Some "e") (Some "pr") (Some "p"))
    Assert.Equal(Some(15000L, ProjectAgreement), source "c" None (Some "pr") (Some "p"))
    Assert.Equal(Some(14000L, CustomerDefault), source "c" None None (Some "p"))
    Assert.Equal(Some(13000L, PersonRate), source "x" None None (Some "p"))
    Assert.Equal(Some(12000L, RoleRate), source "x" None None (Some "q"))
    Assert.Equal(Some(10000L, SystemDefault), source "x" None None None)
    Assert.Equal(None, resolveRate noRates "x" None None None)
    // A scope holds one rate.
    Assert.True(Result.isError (saveRateCard context { card with Rates = (Everyone, usd 1L) :: card.Rates } (fresh ()).Books))

[<Fact>]
let ``approved time becomes a proposal, then a reviewed invoice that traces every entry`` () =
    let proposed = hourly () |> propose context (proposalFor [ "pub-1"; "pub-2"; "pub-3" ]) |> ok
    let lines = proposed.Books.Proposals["P-1"].Lines |> List.map _.Line
    // Grouped by project and always by rate: three lines, in a stable order.
    Assert.Equal<string list>([ "Time, project PRJ-A"; "Time, project PRJ-A"; "Time, project PRJ-B" ], lines |> List.map _.Description)
    Assert.Equal<string list list>([ [ "pub-1" ]; [ "pub-2" ]; [ "pub-3" ] ], lines |> List.map sourcesOf)
    Assert.Equal<int64 list>([ 1500L; 750L; 1000L ], lines |> List.map _.QuantityThousandths)
    Assert.Equal<RateSource list>([ SystemDefault; PersonRate; ProjectAgreement ], lines |> List.choose _.Rate |> List.map _.RateSource)
    // Nothing is issued until a person reviews it.
    Assert.Empty(proposed.Books.Invoices)
    Assert.True(Result.isError (accept context "P-1" issuing proposed))
    let ready = markReady context false "P-1" proposed |> ok
    let accepted, invoice = accept context "P-1" issuing ready |> ok
    // 1.5 h at 150.00 + 0.75 h at 175.00 + 1 h at 200.00 = 556.25.
    Assert.Equal(usd 55625L, invoice.Total)
    Assert.Equal<InvoiceLine list>(lines, invoice.Lines)
    Assert.Equal(Accepted invoice.InvoiceId, accepted.Books.Proposals["P-1"].State)
    // The time stays, consumed by the invoice, and cannot be billed again.
    Assert.Equal(3, accepted.Books.Time.Count)
    Assert.Equal(Some invoice.InvoiceId, (consumed accepted).TryFind "time:act-1")
    let again = propose context { proposalFor [ "pub-1" ] with ProposalId = "P-2" } accepted |> refused
    Assert.Equal<BillingProblem list>([ Unavailable("time pub-1", "already invoiced on INV-001") ], again)
    Assert.Empty(eligibleTime accepted abc.Id None)
    // Accepting again is idempotent.
    Assert.Equal(invoice, accept context "P-1" issuing accepted |> ok |> snd)
    balancesOk accepted

[<Fact>]
let ``grouping is deterministic and visible`` () =
    let r = hourly ()
    let forward = buildProposal context { proposalFor [ "pub-1"; "pub-2"; "pub-3" ] with Grouping = [ ByPerson; ByServiceMonth ] } r |> ok
    let backward = buildProposal context { proposalFor [ "pub-3"; "pub-2"; "pub-1" ] with Grouping = [ ByPerson; ByServiceMonth ] } r |> ok
    Assert.Equal<ProposalLine list>(forward.Lines, backward.Lines)
    Assert.Equal<GroupBy list>([ ByPerson; ByServiceMonth ], forward.Grouping)
    // github:1 has two entries at two rates, so two lines; each names its entries.
    Assert.Equal<string list>(
        [ "Time, person github:1, 2026-10"; "Time, person github:1, 2026-10"; "Time, person github:2, 2026-10" ],
        forward.Lines |> List.map _.Line.Description
    )
    let none = buildProposal context { proposalFor [ "pub-1"; "pub-3" ] with Grouping = [] } (r |> withRates [ ForCustomer abc.Id, usd 15000L ]) |> ok
    Assert.Equal<string list list>([ [ "pub-1"; "pub-3" ] ], none.Lines |> List.map (_.Line >> sourcesOf))
    Assert.Equal(2500L, none.Lines.Head.Line.QuantityThousandths)

[<Fact>]
let ``only approved, billable time of the customer that is still current may be proposed`` () =
    let r =
        hourly ()
        |> importAll
            [ { time "pub-4" "act-4" 30 "github:1" "PRJ-A" with Approved = false }
              { time "pub-5" "act-5" 30 "github:1" "PRJ-A" with ClientId = Some "CUST-XYZ" }
              { time "pub-6" "act-6" 30 "github:1" "PRJ-A" with BillableMinutes = 0 }
              { time "pub-7" "act-7" 30 "github:1" "PRJ-A" with EngagementId = Some "ENG-9" } ]

    let problems = propose context (proposalFor [ "pub-4"; "pub-5"; "pub-6"; "pub-404" ]) r |> refused

    Assert.Equal<BillingProblem list>(
        [ Unavailable("time pub-4", "not approved"); Unavailable("time pub-5", "another customer's"); Unavailable("time pub-6", "not billable"); UnknownTime "pub-404" ],
        problems
    )

    Assert.Equal<string list>([ "pub-1"; "pub-2"; "pub-3"; "pub-7" ], eligibleTime r abc.Id None |> List.map _.PublicationId)
    Assert.Equal<string list>([ "pub-7" ], eligibleTime r abc.Id (Some "ENG-9") |> List.map _.PublicationId)

[<Fact>]
let ``a live proposal reserves its time until it is abandoned`` () =
    let first = hourly () |> propose context (proposalFor [ "pub-1"; "pub-2" ]) |> ok
    let second = { proposalFor [ "pub-2"; "pub-3" ] with ProposalId = "P-2" }
    Assert.Equal<BillingProblem list>([ Unavailable("time pub-2", "reserved by proposal P-1") ], propose context second first |> refused)
    let released = abandon context "P-1" first |> ok
    Assert.Equal(Abandoned, released.Books.Proposals["P-1"].State)
    let taken = propose context second released |> ok
    Assert.Equal(Proposed, taken.Books.Proposals["P-2"].State)
    // An abandoned proposal cannot be accepted; retrying a proposal is a no-op.
    Assert.Equal<BillingProblem list>([ ProposalIs("P-1", Abandoned) ], accept context "P-1" issuing taken |> refused)
    Assert.Equal(taken, propose context second taken |> ok)

[<Fact>]
let ``a missing rate blocks review until a person sets one, with its provenance`` () =
    let proposed = fresh () |> importAll [ time "pub-1" "act-1" 60 "github:1" "PRJ-A" ] |> propose context (proposalFor [ "pub-1" ]) |> ok
    let line = proposed.Books.Proposals["P-1"].Lines.Head
    Assert.False(line.Priced)
    Assert.Equal(None, line.Line.Rate)
    Assert.Equal<BillingProblem list>([ Unpriced [ "Time, project PRJ-A" ] ], markReady context false "P-1" proposed |> refused)
    Assert.Equal<BillingProblem list>([ ReasonRequired ], overrideRate context "P-1" 0 (usd 16000L) " " proposed |> refused)
    let reviewer = { context with Who = "github:7"; When = at "2026-10-08T09:30:00Z" }
    let priced = overrideRate reviewer "P-1" 0 (usd 16000L) "Agreed by e-mail" proposed |> ok
    let changed = priced.Books.Proposals["P-1"].Lines.Head

    Assert.Equal(
        Some
            { RateSource = InvoiceOverride
              Reference = "override"
              Override =
                Some
                    { Previous = None
                      Reason = "Agreed by e-mail"
                      Actor = "github:7"
                      At = at "2026-10-08T09:30:00Z" } },
        changed.Line.Rate
    )

    let _, invoice = priced |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    Assert.Equal(usd 16000L, invoice.Total)
    Assert.Equal(changed.Line.Rate, invoice.Lines.Head.Rate)

[<Fact>]
let ``a zero price is allowed only by policy`` () =
    let proposed = fresh () |> withRates [ Everyone, usd 0L ] |> importAll [ time "pub-1" "act-1" 60 "github:1" "PRJ-A" ] |> propose context (proposalFor [ "pub-1" ]) |> ok
    Assert.Equal<BillingProblem list>([ ZeroPriced [ "Time, project PRJ-A" ] ], markReady context false "P-1" proposed |> refused)
    Assert.Equal(ReadyForReview, (markReady context true "P-1" proposed |> ok).Books.Proposals["P-1"].State)

[<Fact>]
let ``time that changes during review must be reviewed again`` () =
    let proposed = hourly () |> propose context (proposalFor [ "pub-1"; "pub-3" ]) |> ok
    let ready = markReady context false "P-1" proposed |> ok
    let corrected = importAll [ { time "pub-1b" "act-1" 120 "github:1" "PRJ-A" with Revision = 2; Supersedes = Some "pub-1" } ] ready
    Assert.Equal<BillingProblem list>([ SourcesChanged [ "time pub-1 was superseded" ] ], accept context "P-1" issuing corrected |> refused)
    let withdrawn = withdrawTime context { PublicationId = "pub-3"; Revision = 2; Reason = "voided in Chrona"; WithdrawnAt = at "2026-10-08T10:00:00Z" } ready |> ok
    Assert.Equal<BillingProblem list>([ SourcesChanged [ "time pub-3 was withdrawn" ] ], markReady context false "P-1" withdrawn |> refused)
    // Nothing was issued; the proposal can be abandoned and made again from current time.
    Assert.Empty(corrected.Books.Invoices)

[<Fact>]
let ``corrections after invoicing raise a billing review and never touch the invoice`` () =
    let accepted, invoice = hourly () |> propose context (proposalFor [ "pub-1"; "pub-2" ]) |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    let corrected = importAll [ { time "pub-1b" "act-1" 120 "github:1" "PRJ-A" with Revision = 2; Supersedes = Some "pub-1" } ] accepted
    Assert.Equal(invoice, corrected.Books.Invoices[invoice.InvoiceId])
    let review = corrected.Books.Reviews["review-pub-1"]
    Assert.Equal((invoice.InvoiceId, None), (review.InvoiceId, review.Resolution))
    // The corrected time is still the invoiced activity, so it cannot be billed silently.
    Assert.Equal<BillingProblem list>([ Unavailable("time pub-1b", "already invoiced on INV-001") ], propose context { proposalFor [ "pub-1b" ] with ProposalId = "P-2" } corrected |> refused)
    let withdrawn = withdrawTime context { PublicationId = "pub-2"; Revision = 2; Reason = "voided in Chrona"; WithdrawnAt = at "2026-10-08T10:00:00Z" } corrected |> ok
    Assert.Equal("Chrona withdrew invoiced time: voided in Chrona", withdrawn.Books.Reviews["review-pub-2"].Reason)
    let resolved = resolveReview context "review-pub-1" "Supplemental invoice INV-002 for 0.5 h" withdrawn.Books |> ok
    Assert.Equal(Some "Supplemental invoice INV-002 for 0.5 h", resolved.Reviews["review-pub-1"].Resolution)

[<Fact>]
let ``voiding an invoice releases its time for billing again`` () =
    let accepted, invoice = hourly () |> propose context (proposalFor [ "pub-1" ]) |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    let request: Summa.Ledger.Corrections.VoidRequest = { InvoiceId = invoice.InvoiceId; Reason = "Wrong project"; Date = DateOnly(2026, 10, 9); JournalEntryId = "JE-VOID-1" }
    let voided = Summa.Ledger.Corrections.voidInvoice context request accepted |> ok
    Assert.Equal(None, (consumed voided).TryFind "time:act-1")
    Assert.Equal(Proposed, (propose context { proposalFor [ "pub-1" ] with ProposalId = "P-2" } voided |> ok).Books.Proposals["P-2"].State)

[<Fact>]
let ``importing is idempotent and a second publication of the same revision is a duplicate`` () =
    let r = fresh () |> importAll [ time "pub-1" "act-1" 60 "github:1" "PRJ-A" ]
    Assert.Equal((r, Imported), importTime context (time "pub-1" "act-1" 60 "github:1" "PRJ-A") r |> ok)
    Assert.Equal((r, AlreadyImported "pub-1"), importTime context (time "pub-9" "act-1" 60 "github:1" "PRJ-A") r |> ok)
    Assert.Equal<BillingProblem list>([ IdReused "pub-1" ], importTime context (time "pub-1" "act-1" 90 "github:1" "PRJ-A") r |> refused)

let private assessment milestones fee =
    { Id = "ENG-1"
      CustomerId = abc.Id
      Name = "Architecture Assessment"
      Currency = "USD"
      FixedFee = fee
      Milestones = milestones
      Terms = Some(Net 15) }

[<Fact>]
let ``a fixed fee bills the agreement, not the hours, once`` () =
    let r = fresh ()
    let books = saveEngagement context (assessment [] (Some(usd 1500000L))) r.Books |> ok
    let r = { r with Books = books } |> importAll [ { time "pub-1" "act-1" 600 "github:1" "PRJ-A" with EngagementId = Some "ENG-1" } ]
    let request = { proposalFor [] with EngagementId = Some "ENG-1"; FixedFee = true }
    let accepted, invoice = r |> propose context request |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    Assert.Equal(usd 1500000L, invoice.Total)
    Assert.Equal(FixedFeeSource "ENG-1", invoice.Lines.Head.Source)
    Assert.Equal(Some EngagementAgreement, invoice.Lines.Head.Rate |> Option.map _.RateSource)
    // Terms come from the engagement, before the customer's.
    Assert.Equal((Net 15, EngagementTerms), (invoice.Terms, invoice.TermsSource))
    // Internal time still belongs to the engagement and stays unbilled.
    Assert.Equal<string list>([ "pub-1" ], eligibleTime accepted abc.Id (Some "ENG-1") |> List.map _.PublicationId)
    Assert.Equal<BillingProblem list>([ Unavailable("fee:ENG-1", "already invoiced on INV-001") ], propose context { request with ProposalId = "P-2" } accepted |> refused)

[<Fact>]
let ``milestones are completed and invoiced as separate actions`` () =
    let milestones =
        [ { Id = "kickoff"; Label = "Kickoff (30%)"; Amount = ShareOfFee 3000; CompletedOn = None }
          { Id = "midpoint"; Label = "Midpoint (40%)"; Amount = ShareOfFee 4000; CompletedOn = None }
          { Id = "completion"; Label = "Completion (30%)"; Amount = ShareOfFee 3000; CompletedOn = None } ]

    let r = fresh ()
    let r = { r with Books = saveEngagement context (assessment milestones (Some(usd 1500000L))) r.Books |> ok }
    let request = { proposalFor [] with EngagementId = Some "ENG-1"; Milestones = [ "kickoff" ] }
    Assert.Equal<BillingProblem list>([ Unavailable("milestone:ENG-1/kickoff", "not completed") ], propose context request r |> refused)
    let completed = { r with Books = completeMilestone context "ENG-1" "kickoff" (DateOnly(2026, 10, 5)) r.Books |> ok }
    // Completing it issued nothing.
    Assert.Empty(completed.Books.Invoices)
    let accepted, invoice = completed |> propose context request |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    Assert.Equal(usd 450000L, invoice.Total)
    Assert.Equal(MilestoneSource("ENG-1", "kickoff"), invoice.Lines.Head.Source)
    Assert.True(Result.isError (propose context { request with ProposalId = "P-2" } accepted))
    // A fee billed by milestones cannot also be billed whole; shares cannot exceed the fee.
    Assert.Equal<BillingProblem list>([ Unavailable("fee:ENG-1", "the engagement bills its fee by milestone") ], propose context { request with ProposalId = "P-3"; Milestones = []; FixedFee = true } accepted |> refused)
    Assert.True(Result.isError (saveEngagement context (assessment ({ milestones.Head with Id = "extra" } :: milestones) (Some(usd 1500000L))) r.Books))

let private flight =
    { Id = "EXP-1"
      Date = DateOnly(2026, 10, 3)
      Description = "Flight to the client site"
      Amount = usd 42000L
      ExpenseAccountId = "travel"
      PaidFromAccountId = "cash"
      CustomerId = Some abc.Id
      ProjectId = Some "PRJ-A"
      EngagementId = None
      Billable = true
      JournalEntryId = "JE-EXP-1" }

[<Fact>]
let ``a reimbursable expense and its reimbursement stay separate accounting events`` () =
    let r = fresh () |> recordExpense context flight |> ok
    Assert.Equal(usd 42000L, balance r.Books.Ledger "USD" (DateOnly(2026, 10, 31)) "travel")
    let accepted, invoice = r |> propose context { proposalFor [] with Expenses = [ "EXP-1" ] } |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    Assert.Equal(ExpenseSource "EXP-1", invoice.Lines.Head.Source)
    Assert.Equal("Reimbursable: Flight to the client site", invoice.Lines.Head.Description)
    // The expense is unchanged; the reimbursement is revenue of its own.
    Assert.Equal(usd 42000L, balance accepted.Books.Ledger "USD" (DateOnly(2026, 10, 31)) "travel")
    Assert.Equal(usd 42000L, balance accepted.Books.Ledger "USD" (DateOnly(2026, 10, 31)) "reimbursed")
    Assert.True(Result.isError (propose context { proposalFor [] with ProposalId = "P-2"; Expenses = [ "EXP-1" ] } accepted))
    let internalOnly = fresh () |> recordExpense context { flight with Id = "EXP-2"; JournalEntryId = "JE-EXP-2"; Billable = false } |> ok
    Assert.Equal<BillingProblem list>([ Unavailable("expense:EXP-2", "not billable") ], propose context { proposalFor [] with Expenses = [ "EXP-2" ] } internalOnly |> refused)
    balancesOk accepted

[<Fact>]
let ``one invoice may mix sources, each line keeping its own provenance`` () =
    let r = hourly () |> recordExpense context flight |> ok
    let manual = { consulting 1000L 5000L with Description = "Workshop materials" }
    let request = { proposalFor [ "pub-3" ] with Expenses = [ "EXP-1" ]; Manual = [ manual ] }
    let _, invoice = r |> propose context request |> ok |> markReady context false "P-1" |> ok |> accept context "P-1" issuing |> ok
    Assert.Equal<LineSource list>([ TimeSource [ { PublicationId = "pub-3"; ActivityId = "act-3"; Revision = 1; Minutes = 60 } ]; ExpenseSource "EXP-1"; ManualLine ], invoice.Lines |> List.map _.Source)
    Assert.Equal(usd (20000L + 42000L + 5000L), invoice.Total)
