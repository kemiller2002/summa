/// Billing models (v0.2 §14-17, v0.1 §17, INV-SOURCE, INV-CHR, INV-RATE).
///
/// Approved time, fixed fees, completed milestones and billable expenses
/// become an invoice proposal; a person reviews it; only accepting it issues
/// an invoice. Nothing is billed twice:
/// - a live proposal reserves its sources;
/// - an issued invoice that is not voided consumes them;
/// - both are derived from the records, never stored as flags.
///
/// Time is consumed per Chrona activity, so a corrected publication of
/// invoiced time cannot be billed again silently: it raises a billing
/// review instead (INV-CHR-012).
module Summa.Ledger.Billing

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

type BillingProblem =
    | UnknownCustomerToBill of string
    | UnknownEngagement of string
    | EngagementOfAnotherCustomer of string
    /// An engagement or rate card that cannot be saved as given.
    | InvalidEngagementTerms of string
    | UnknownMilestone of engagementId: string * milestoneId: string
    | UnknownExpense of string
    | InvalidExpense of string
    | UnknownTime of string
    /// A source that cannot go on this proposal, and why (INV-CHR-001).
    | Unavailable of source: string * reason: string
    | UnknownProposal of string
    | ProposalIs of proposalId: string * state: ProposalState
    | NothingToPropose
    /// Lines whose rate is unknown: no rate is invented (INV-RATE-005).
    | Unpriced of lines: string list
    /// Zero-priced lines the policy does not allow (INV-RATE-006).
    | ZeroPriced of lines: string list
    /// Sources that changed after the proposal was made (INV-CHR-008).
    | SourcesChanged of sources: string list
    | ReasonRequired
    | UnknownLine of index: int
    | UnknownReview of string
    | IdReused of string
    | BillingPosting of Problem list
    | Issuing of InvoiceProblem list

/// The revenue accounts billed sources post to.
type BillingAccounts =
    { TimeRevenue: string
      FeeRevenue: string
      /// Revenue from reimbursed expenses, apart from the expense itself (v0.2 §14).
      ReimbursedExpenses: string }

let private audited (context: Context) what subject (books: Books) =
    { books with Ledger = audit context what subject books.Ledger }

// ---- Sources and their use ----------------------------------------------------

/// The keys of what a line bills: time per Chrona activity, an engagement's
/// fee, a milestone or an expense.
let sourceKeys (books: Books) (line: InvoiceLine) =
    match line.Source with
    | ManualLine -> []
    | TimeSource refs ->
        refs
        |> List.map (fun t ->
            let activity = books.Time.TryFind t.PublicationId |> Option.map _.ActivityId |> Option.defaultValue t.ActivityId
            $"time:{activity}")
    | FixedFeeSource engagement -> [ $"fee:{engagement}" ]
    | MilestoneSource(engagement, milestone) -> [ $"milestone:{engagement}/{milestone}" ]
    | ExpenseSource expense -> [ $"expense:{expense}" ]

/// Source key -> the invoice that consumed it: issued invoices that are not voided.
let consumed (r: Receivables) : Map<string, string> =
    r.Books.Invoices
    |> Map.toList
    |> List.filter (fun (id, _) -> not (r.Voids.ContainsKey id))
    |> List.collect (fun (id, i) -> i.Lines |> List.collect (sourceKeys r.Books) |> List.map (fun key -> key, id))
    |> Map.ofList

let private live (p: Proposal) =
    match p.State with
    | Proposed
    | ReadyForReview -> true
    | Abandoned
    | Accepted _ -> false

/// Source key -> the live proposal that reserves it (INV-CHR-004).
let reserved (books: Books) : Map<string, string> =
    books.Proposals
    |> Map.toList
    |> List.filter (fun (_, p) -> live p)
    |> List.collect (fun (id, p) -> p.Lines |> List.collect (fun l -> sourceKeys books l.Line) |> List.map (fun key -> key, id))
    |> Map.ofList

let private superseded (books: Books) (publicationId: string) =
    books.Time |> Map.exists (fun _ t -> t.Supersedes = Some publicationId)

/// Why a source is not available to a proposal, if it is not.
let private blocked (r: Receivables) (proposalId: string) (key: string) =
    match (consumed r).TryFind key, (reserved r.Books).TryFind key with
    | Some invoice, _ -> Some $"already invoiced on {invoice}"
    | None, Some other when other <> proposalId -> Some $"reserved by proposal {other}"
    | _ -> None

/// Whether imported time may be billed to this customer and engagement
/// (INV-CHR-001): approved, billable, theirs, current and not already used.
let timeProblem (r: Receivables) (proposalId: string) (customerId: string) (engagementId: string option) (t: SourceTime) =
    if not t.Approved then Some "not approved"
    elif t.BillableMinutes <= 0 then Some "not billable"
    elif t.ClientId <> Some customerId then Some "another customer's"
    elif engagementId.IsSome && t.EngagementId <> engagementId then Some "another engagement's"
    elif r.Books.Withdrawals.ContainsKey t.PublicationId then Some "withdrawn by Chrona"
    elif superseded r.Books t.PublicationId then Some "superseded by a later publication"
    else blocked r proposalId $"time:{t.ActivityId}"

/// Imported time a proposal for this customer could bill now.
let eligibleTime (r: Receivables) (customerId: string) (engagementId: string option) =
    r.Books.Time
    |> Map.toList
    |> List.map snd
    |> List.filter (fun t -> (timeProblem r "" customerId engagementId t).IsNone)

// ---- Engagements, milestones and expenses -------------------------------------------

/// Saves a billing agreement: its fee, milestones and terms.
let saveEngagement (context: Context) (engagement: Engagement) (books: Books) =
    let shares =
        engagement.Milestones
        |> List.sumBy (fun m ->
            match m.Amount with
            | ShareOfFee bp -> bp
            | MilestoneFixed _ -> 0)

    let problems =
        [ if not (books.Customers.ContainsKey engagement.CustomerId) then UnknownCustomerToBill engagement.CustomerId
          match engagement.FixedFee with
          | Some fee when fee.Currency <> engagement.Currency || not (isPositive fee) ->
              InvalidEngagementTerms "the fixed fee must be positive and in the engagement's currency"
          | None when shares > 0 -> InvalidEngagementTerms "a share of the fee needs a fixed fee"
          | _ -> ()
          if shares > 10000 then InvalidEngagementTerms "milestone shares add up to more than the fee"
          for m in engagement.Milestones do
              match m.Amount with
              | ShareOfFee bp when bp < 1 -> InvalidEngagementTerms $"milestone {m.Id} has no share"
              | MilestoneFixed amount when amount.Currency <> engagement.Currency || not (isPositive amount) ->
                  InvalidEngagementTerms $"milestone {m.Id} must be positive and in the engagement's currency"
              | _ -> ()
          if engagement.Milestones |> List.countBy _.Id |> List.exists (fun (_, n) -> n > 1) then
              InvalidEngagementTerms "milestone ids repeat" ]

    if problems.IsEmpty then
        let what = if books.Engagements.ContainsKey engagement.Id then "engagement-changed" else "engagement-created"
        Ok({ books with Engagements = books.Engagements.Add(engagement.Id, engagement) } |> audited context what engagement.Id)
    else
        Error problems

/// Records that a milestone was completed. Invoicing it is a separate
/// action (v0.2 §17). Idempotent.
let completeMilestone (context: Context) (engagementId: string) (milestoneId: string) (on: DateOnly) (books: Books) =
    match books.Engagements.TryFind engagementId with
    | None -> Error [ UnknownEngagement engagementId ]
    | Some e ->
        match e.Milestones |> List.tryFind (fun m -> m.Id = milestoneId) with
        | None -> Error [ UnknownMilestone(engagementId, milestoneId) ]
        | Some m when m.CompletedOn.IsSome -> Ok books
        | Some _ ->
            let milestones = e.Milestones |> List.map (fun m -> if m.Id = milestoneId then { m with CompletedOn = Some on } else m)

            Ok(
                { books with Engagements = books.Engagements.Add(engagementId, { e with Milestones = milestones }) }
                |> audited context "milestone-completed" $"{engagementId}/{milestoneId}"
            )

/// What a milestone bills.
let milestoneAmount (engagement: Engagement) (milestone: Milestone) =
    match milestone.Amount, engagement.FixedFee with
    | MilestoneFixed amount, _ -> Some amount
    | ShareOfFee bp, Some fee -> Some(discountOn fee (Percent bp))
    | ShareOfFee _, None -> None

/// Records an expense incurred for a customer and posts it at once: Debit
/// the expense, Credit what paid it (v0.2 §14). Idempotent by id.
let recordExpense (context: Context) (expense: Expense) (r: Receivables) =
    match r.Books.Expenses.TryFind expense.Id with
    | Some existing when existing = expense -> Ok r
    | Some _ -> Error [ IdReused expense.Id ]
    | None ->
        let problems =
            [ if not (isPositive expense.Amount) then InvalidExpense "the amount must be positive"
              if String.IsNullOrWhiteSpace expense.Description then InvalidExpense "it needs a description"
              match expense.CustomerId with
              | Some c when not (r.Books.Customers.ContainsKey c) -> UnknownCustomerToBill c
              | None when expense.Billable -> InvalidExpense "a billable expense needs a customer"
              | _ -> ()
              match expense.EngagementId with
              | Some e when not (r.Books.Engagements.ContainsKey e) -> UnknownEngagement e
              | _ -> () ]

        if not problems.IsEmpty then
            Error problems
        else
            let dims = { noDimensions with Client = expense.CustomerId; Project = expense.ProjectId; Engagement = expense.EngagementId }

            let entry =
                { Date = expense.Date
                  Description = $"Expense: {expense.Description}"
                  Lines =
                    [ { AccountId = expense.ExpenseAccountId; Side = Debit expense.Amount; Memo = Some expense.Description; Dimensions = dims }
                      { AccountId = expense.PaidFromAccountId; Side = Credit expense.Amount; Memo = Some expense.Id; Dimensions = dims } ]
                  Source = $"expense:{expense.Id}" }

            match post context $"expense:{expense.Id}" expense.JournalEntryId entry r.Books.Ledger with
            | Error problems -> Error [ BillingPosting problems ]
            | Ok(ledger, _) ->
                Ok
                    { r with
                        Books =
                            { r.Books with Ledger = ledger; Expenses = r.Books.Expenses.Add(expense.Id, expense) }
                            |> audited context "expense-recorded" expense.Id }

/// Saves the organization's rate card. A scope holds one rate, so
/// resolution never chooses between rates arbitrarily (INV-RATE-002).
let saveRateCard (context: Context) (card: RateCard) (books: Books) =
    let problems =
        [ for scope, uses in card.Rates |> List.groupBy fst do
              if uses.Length > 1 then InvalidEngagementTerms $"%A{scope} has more than one rate"
          for _, rate in card.Rates do
              if rate.Minor < 0L then InvalidEngagementTerms "a rate cannot be negative" ]

    if problems.IsEmpty then Ok({ books with Rates = card } |> audited context "rates-changed" "rates") else Error problems

// ---- Time from Chrona -----------------------------------------------------------------

/// What importing a publication did.
type ImportOutcome =
    | Imported
    /// The same activity at the same revision was already imported under
    /// this publication id; nothing was added (v0.2 §25).
    | AlreadyImported of publicationId: string

let private review (context: Context) (publicationId: string) (invoiceId: string) (reason: string) (books: Books) =
    let id = $"review-{publicationId}"

    if books.Reviews.ContainsKey id then
        books
    else
        { books with
            Reviews =
                books.Reviews.Add(
                    id,
                    { Id = id
                      PublicationId = publicationId
                      InvoiceId = invoiceId
                      Reason = reason
                      RaisedAt = context.When
                      Resolution = None }
                ) }
        |> audited context "billing-review-raised" id

/// Imports approved time Chrona published. Idempotent by publication id; a
/// second publication of the same activity revision is a duplicate. A
/// correction of time already invoiced raises a billing review rather than
/// changing the invoice (INV-CHR-012).
let importTime (context: Context) (time: SourceTime) (r: Receivables) : Result<Receivables * ImportOutcome, BillingProblem list> =
    match r.Books.Time.TryFind time.PublicationId with
    | Some existing when existing = time -> Ok(r, Imported)
    | Some _ -> Error [ IdReused time.PublicationId ]
    | None ->
        match r.Books.Time |> Map.tryFindKey (fun _ t -> t.ActivityId = time.ActivityId && t.Revision = time.Revision) with
        | Some duplicate -> Ok(r, AlreadyImported duplicate)
        | None ->
            let books = { r.Books with Time = r.Books.Time.Add(time.PublicationId, time) } |> audited context "time-imported" time.PublicationId

            let books =
                match time.Supersedes |> Option.bind r.Books.Time.TryFind with
                | Some earlier ->
                    match (consumed r).TryFind $"time:{earlier.ActivityId}" with
                    | Some invoice -> review context earlier.PublicationId invoice "Chrona corrected time after it was invoiced" books
                    | None -> books
                | None -> books

            Ok({ r with Books = books }, Imported)

/// Records that Chrona withdrew published time. Withdrawn time is no longer
/// eligible; if an invoice used it, a billing review is raised. Idempotent.
let withdrawTime (context: Context) (withdrawal: TimeWithdrawal) (r: Receivables) =
    match r.Books.Withdrawals.TryFind withdrawal.PublicationId, r.Books.Time.TryFind withdrawal.PublicationId with
    | Some existing, _ when existing = withdrawal -> Ok r
    | Some _, _ -> Error [ IdReused withdrawal.PublicationId ]
    | None, None -> Error [ UnknownTime withdrawal.PublicationId ]
    | None, Some time ->
        let books =
            { r.Books with Withdrawals = r.Books.Withdrawals.Add(withdrawal.PublicationId, withdrawal) }
            |> audited context "time-withdrawn" withdrawal.PublicationId

        let books =
            match (consumed r).TryFind $"time:{time.ActivityId}" with
            | Some invoice -> review context time.PublicationId invoice $"Chrona withdrew invoiced time: {withdrawal.Reason}" books
            | None -> books

        Ok { r with Books = books }

/// Records how a billing review was settled: a credit memo, a supplemental
/// invoice, or no action, in the reviewer's words.
let resolveReview (context: Context) (reviewId: string) (resolution: string) (books: Books) =
    match books.Reviews.TryFind reviewId with
    | None -> Error [ UnknownReview reviewId ]
    | Some _ when String.IsNullOrWhiteSpace resolution -> Error [ ReasonRequired ]
    | Some found ->
        Ok(
            { books with Reviews = books.Reviews.Add(reviewId, { found with Resolution = Some resolution }) }
            |> audited context "billing-review-resolved" reviewId
        )

// ---- Proposals ------------------------------------------------------------------------

/// What to propose: which sources, how to group time, and where revenue goes.
type ProposalRequest =
    { ProposalId: string
      CustomerId: string
      EngagementId: string option
      Currency: string
      /// Publication ids of imported time.
      Time: string list
      Grouping: GroupBy list
      FixedFee: bool
      Milestones: string list
      Expenses: string list
      Manual: InvoiceLine list
      Accounts: BillingAccounts }

let private label (by: GroupBy) (t: SourceTime) =
    match by with
    | ByProject -> $"project {t.ProjectId}"
    | ByEngagement -> "engagement " + (t.EngagementId |> Option.defaultValue "none")
    | ByPerson -> $"person {t.PerformerId}"
    | ByActivityType -> $"activity {t.ActivityTypeId}"
    | ByServiceMonth -> $"{t.BusinessDate.Year:D4}-{t.BusinessDate.Month:D2}"

/// Minutes as thousandths of an hour, rounded half away from zero; the
/// exact minutes stay on each time reference (INV-CHR-011).
let hoursThousandths (minutes: int) =
    int64 (Decimal.Round(decimal minutes * 1000m / 60m, 0, MidpointRounding.AwayFromZero))

/// Groups time into lines deterministically: by the policy's dimensions and
/// always by rate, in a stable order. Each line names every entry it bills.
let groupTime (card: RateCard) (customerId: string) (currency: string) (revenueAccount: string) (grouping: GroupBy list) (time: SourceTime list) =
    time
    |> List.map (fun t ->
        let rate = resolveRate card customerId t.EngagementId (Some t.ProjectId) (Some t.PerformerId) |> Option.filter (fun (m, _) -> m.Currency = currency)
        t, rate)
    |> List.groupBy (fun (t, rate) -> grouping |> List.map (fun by -> label by t), rate |> Option.map (fun (m, p) -> m.Minor, p.Reference))
    |> List.sortBy fst
    |> List.map (fun ((labels, _), members) ->
        let entries = members |> List.map fst |> List.sortBy _.PublicationId
        let rate = members |> List.head |> snd
        let minutes = entries |> List.sumBy _.BillableMinutes
        let projects = entries |> List.map _.ProjectId |> List.distinct

        { Line =
            { Description = String.Join(", ", "Time" :: labels)
              QuantityThousandths = hoursThousandths minutes
              UnitPrice = rate |> Option.map fst |> Option.defaultValue (zero currency)
              RevenueAccountId = revenueAccount
              Project = (match projects with [ p ] -> Some p | _ -> None)
              WorkItem = None
              Discount = None
              Source =
                TimeSource(
                    entries
                    |> List.map (fun t ->
                        { PublicationId = t.PublicationId
                          ActivityId = t.ActivityId
                          Revision = t.Revision
                          Minutes = t.BillableMinutes })
                )
              Rate = rate |> Option.map snd }
          Priced = rate.IsSome })

let private fixedLine description (amount: Money) account source reference =
    { Line =
        { Description = description
          QuantityThousandths = 1000L
          UnitPrice = amount
          RevenueAccountId = account
          Project = None
          WorkItem = None
          Discount = None
          Source = source
          Rate = Some { RateSource = EngagementAgreement; Reference = reference; Override = None } }
      Priced = true }

/// Builds a proposal from the sources asked for, checking each is
/// available to it. Pure: the proposal is not saved.
let buildProposal (context: Context) (request: ProposalRequest) (r: Receivables) : Result<Proposal, BillingProblem list> =
    let books = r.Books
    let id = request.ProposalId

    let engagement =
        request.EngagementId
        |> Option.map (fun e ->
            match books.Engagements.TryFind e with
            | None -> Error(UnknownEngagement e)
            | Some found when found.CustomerId <> request.CustomerId -> Error(EngagementOfAnotherCustomer e)
            | Some found -> Ok found)

    let time =
        request.Time
        |> List.map (fun p ->
            match books.Time.TryFind p with
            | None -> Error(UnknownTime p)
            | Some t ->
                match timeProblem r id request.CustomerId request.EngagementId t with
                | Some why -> Error(Unavailable($"time {p}", why))
                | None -> Ok t)

    let fee =
        match request.FixedFee, engagement with
        | false, _ -> []
        | true, None -> [ Error(Unavailable("fixed fee", "the proposal has no engagement")) ]
        | true, Some(Error problem) -> [ Error problem ]
        | true, Some(Ok e) ->
            match e.FixedFee with
            | None -> [ Error(Unavailable($"fee:{e.Id}", "the engagement has no fixed fee")) ]
            | Some _ when not e.Milestones.IsEmpty -> [ Error(Unavailable($"fee:{e.Id}", "the engagement bills its fee by milestone")) ]
            | Some amount ->
                match blocked r id $"fee:{e.Id}" with
                | Some why -> [ Error(Unavailable($"fee:{e.Id}", why)) ]
                | None -> [ Ok(fixedLine $"{e.Name}: fixed fee" amount request.Accounts.FeeRevenue (FixedFeeSource e.Id) $"engagement:{e.Id}") ]

    let milestones =
        request.Milestones
        |> List.map (fun m ->
            match engagement with
            | None -> Error(Unavailable($"milestone {m}", "the proposal has no engagement"))
            | Some(Error problem) -> Error problem
            | Some(Ok e) ->
                match e.Milestones |> List.tryFind (fun x -> x.Id = m) with
                | None -> Error(UnknownMilestone(e.Id, m))
                | Some x when x.CompletedOn.IsNone -> Error(Unavailable($"milestone:{e.Id}/{m}", "not completed"))
                | Some x ->
                    match blocked r id $"milestone:{e.Id}/{m}", milestoneAmount e x with
                    | Some why, _ -> Error(Unavailable($"milestone:{e.Id}/{m}", why))
                    | None, None -> Error(Unavailable($"milestone:{e.Id}/{m}", "its share has no fee"))
                    | None, Some amount ->
                        Ok(fixedLine $"{e.Name}: {x.Label}" amount request.Accounts.FeeRevenue (MilestoneSource(e.Id, m)) $"engagement:{e.Id}/milestone:{m}"))

    let expenses =
        request.Expenses
        |> List.map (fun x ->
            match books.Expenses.TryFind x with
            | None -> Error(UnknownExpense x)
            | Some e when not e.Billable -> Error(Unavailable($"expense:{x}", "not billable"))
            | Some e when e.CustomerId <> Some request.CustomerId -> Error(Unavailable($"expense:{x}", "another customer's"))
            | Some e when e.Amount.Currency <> request.Currency -> Error(Unavailable($"expense:{x}", "another currency"))
            | Some e ->
                match blocked r id $"expense:{x}" with
                | Some why -> Error(Unavailable($"expense:{x}", why))
                | None ->
                    Ok
                        { Line =
                            { Description = $"Reimbursable: {e.Description}"
                              QuantityThousandths = 1000L
                              UnitPrice = e.Amount
                              RevenueAccountId = request.Accounts.ReimbursedExpenses
                              Project = e.ProjectId
                              WorkItem = None
                              Discount = None
                              Source = ExpenseSource e.Id
                              Rate = None }
                          Priced = true })

    let problems =
        [ if not (books.Customers.ContainsKey request.CustomerId) then UnknownCustomerToBill request.CustomerId
          match engagement with
          | Some(Error problem) -> problem
          | _ -> ()
          for item in time do
              match item with
              | Error problem -> problem
              | Ok _ -> ()
          for item in fee @ milestones @ expenses do
              match item with
              | Error problem -> problem
              | Ok _ -> () ]
        |> List.distinct

    let manual = request.Manual |> List.map (fun l -> { Line = { l with Source = ManualLine }; Priced = true })

    if not problems.IsEmpty then
        Error problems
    else
        let timeLines =
            groupTime books.Rates request.CustomerId request.Currency request.Accounts.TimeRevenue request.Grouping (time |> List.choose Result.toOption)

        let lines = timeLines @ (fee @ milestones @ expenses |> List.choose Result.toOption) @ manual

        if lines.IsEmpty then
            Error [ NothingToPropose ]
        else
            Ok
                { Id = id
                  CustomerId = request.CustomerId
                  EngagementId = request.EngagementId
                  Currency = request.Currency
                  Lines = lines
                  Grouping = request.Grouping
                  State = Proposed
                  CreatedAt = context.When }

/// Proposes an invoice and reserves its sources. Retrying the same
/// proposal changes nothing; reusing its id for another is refused.
let propose (context: Context) (request: ProposalRequest) (r: Receivables) =
    match r.Books.Proposals.TryFind request.ProposalId with
    | Some existing ->
        match buildProposal context request { r with Books = { r.Books with Proposals = r.Books.Proposals.Remove request.ProposalId } } with
        | Ok rebuilt when rebuilt.Lines = existing.Lines && rebuilt.CustomerId = existing.CustomerId -> Ok r
        | _ -> Error [ IdReused request.ProposalId ]
    | None ->
        buildProposal context request r
        |> Result.map (fun proposal ->
            { r with Books = { r.Books with Proposals = r.Books.Proposals.Add(proposal.Id, proposal) } |> audited context "proposal-created" proposal.Id })

let private openProposal (books: Books) (proposalId: string) =
    match books.Proposals.TryFind proposalId with
    | None -> Error [ UnknownProposal proposalId ]
    | Some p when live p -> Ok p
    | Some p -> Error [ ProposalIs(proposalId, p.State) ]

let private saveProposal context what (proposal: Proposal) (r: Receivables) =
    { r with Books = { r.Books with Proposals = r.Books.Proposals.Add(proposal.Id, proposal) } |> audited context what proposal.Id }

/// A person sets a line's rate before issue, with a reason; the previous
/// rate, the person and the time are kept (INV-RATE-004). The proposal
/// goes back to Proposed, to be reviewed again.
let overrideRate (context: Context) (proposalId: string) (index: int) (rate: Money) (reason: string) (r: Receivables) =
    openProposal r.Books proposalId
    |> Result.bind (fun p ->
        match p.Lines |> List.tryItem index with
        | None -> Error [ UnknownLine index ]
        | Some _ when String.IsNullOrWhiteSpace reason -> Error [ ReasonRequired ]
        | Some _ when rate.Currency <> p.Currency || rate.Minor < 0L -> Error [ Unpriced [ $"line {index}: the rate must be in {p.Currency} and not negative" ] ]
        | Some found ->
            let line = found.Line

            let changed =
                { Line =
                    { line with
                        UnitPrice = rate
                        Rate =
                            Some
                                { RateSource = InvoiceOverride
                                  Reference = line.Rate |> Option.map _.Reference |> Option.defaultValue "override"
                                  Override =
                                    Some
                                        { Previous = (if found.Priced then Some line.UnitPrice else None)
                                          Reason = reason
                                          Actor = context.Who
                                          At = context.When } } }
                  Priced = true }

            Ok(
                saveProposal
                    context
                    "rate-overridden"
                    { p with Lines = p.Lines |> List.mapi (fun i l -> if i = index then changed else l); State = Proposed }
                    r
            ))

/// What is wrong with the sources of these lines, billed by `owner` (a
/// proposal or draft id): time withdrawn, superseded or missing, or a source
/// invoiced or reserved elsewhere (INV-CHR-008, INV-ISS-011).
let sourceProblems (r: Receivables) (owner: string) (lines: InvoiceLine list) =
    [ for line in lines do
          match line.Source with
          | TimeSource refs ->
              for t in refs do
                  match r.Books.Time.TryFind t.PublicationId with
                  | None -> $"time {t.PublicationId} is missing"
                  | Some found when r.Books.Withdrawals.ContainsKey found.PublicationId -> $"time {t.PublicationId} was withdrawn"
                  | Some found when superseded r.Books found.PublicationId -> $"time {t.PublicationId} was superseded"
                  | Some found when found.Revision <> t.Revision -> $"time {t.PublicationId} changed revision"
                  | Some _ -> ()
          | ExpenseSource x when not (r.Books.Expenses.ContainsKey x) -> $"expense {x} is missing"
          | _ -> ()
          for key in sourceKeys r.Books line do
              match blocked r owner key with
              | Some why -> $"{key} is {why}"
              | None -> () ]

/// The sources of a proposal that changed since it was made (INV-CHR-008).
let changedSources (r: Receivables) (p: Proposal) =
    sourceProblems r p.Id (p.Lines |> List.map _.Line)

/// Marks a proposal ready for review: every line priced, zero prices only
/// where the policy allows them, and every source current. Idempotent.
let markReady (context: Context) (allowZeroPriced: bool) (proposalId: string) (r: Receivables) =
    openProposal r.Books proposalId
    |> Result.bind (fun p ->
        let unpriced = p.Lines |> List.filter (fun l -> not l.Priced) |> List.map _.Line.Description
        let zero = p.Lines |> List.filter (fun l -> l.Priced && l.Line.UnitPrice.Minor = 0L) |> List.map _.Line.Description
        let changed = changedSources r p

        let problems =
            [ if not unpriced.IsEmpty then Unpriced unpriced
              if not allowZeroPriced && not zero.IsEmpty then ZeroPriced zero
              if not changed.IsEmpty then SourcesChanged changed ]

        match problems, p.State with
        | [], ReadyForReview -> Ok r
        | [], _ -> Ok(saveProposal context "proposal-ready" { p with State = ReadyForReview } r)
        | problems, _ -> Error problems)

/// Abandons a proposal and releases what it reserved (INV-CHR-005). Idempotent.
let abandon (context: Context) (proposalId: string) (r: Receivables) =
    match r.Books.Proposals.TryFind proposalId with
    | None -> Error [ UnknownProposal proposalId ]
    | Some { State = Abandoned } -> Ok r
    | Some p when live p -> Ok(saveProposal context "proposal-abandoned" { p with State = Abandoned } r)
    | Some p -> Error [ ProposalIs(proposalId, p.State) ]

/// Accepts a reviewed proposal: its lines, as they stand, become an issued
/// invoice in the same transition, and its sources are consumed
/// (INV-CHR-006). If anything fails nothing changes and the sources stay
/// reserved (INV-CHR-007). Idempotent.
let accept (context: Context) (proposalId: string) (request: IssueRequest) (r: Receivables) =
    match r.Books.Proposals.TryFind proposalId with
    | Some { State = Accepted invoiceId } -> Ok(r, r.Books.Invoices[invoiceId])
    | Some p when p.State <> ReadyForReview -> Error [ ProposalIs(proposalId, p.State) ]
    | None -> Error [ UnknownProposal proposalId ]
    | Some p ->
        match changedSources r p with
        | _ :: _ as changed -> Error [ SourcesChanged changed ]
        | [] ->
            let draft =
                { DraftId = request.DraftId
                  CustomerId = p.CustomerId
                  Currency = p.Currency
                  Lines = p.Lines |> List.map _.Line
                  Adjustments = []
                  Discounts = []
                  Terms = None
                  DueDate = None
                  Corrects = None
                  EngagementId = p.EngagementId
                  Details = noDetails
                  Version = 0
                  Review = Editing }

            saveDraft context draft r.Books
            |> Result.bind (issue context request)
            |> Result.mapError (Issuing >> List.singleton)
            |> Result.map (fun (books, invoice) ->
                let books =
                    { books with Proposals = books.Proposals.Add(p.Id, { p with State = Accepted invoice.InvoiceId }) }
                    |> audited context "proposal-accepted" p.Id

                { r with Books = books }, invoice)
