/// Natural-language invoice requests (INV-AGENT-001): an authorized agent
/// passes on what a person asked for, such as "Invoice Acme for all approved
/// architecture work from September 1 through September 15 at the contracted
/// rate and include approved reimbursable expenses", and the result is a
/// proposal for a person to review, never an issued invoice.
///
/// Summa reads one documented phrasing, and nothing looser: what it cannot
/// read, or cannot resolve from the books, comes back as a question
/// (INV-AGENT-003). It never picks a customer, a rate, a period or source
/// work that the request and the records do not settle.
module Summa.Ledger.AgentRequests

open System
open System.Globalization
open System.Text.RegularExpressions
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Billing

/// The rate the request asks for.
type RateBasis =
    /// The rate card's rate (INV-RATE-001): the only rate an agent proposes.
    | Contracted
    /// A rate the request names. An agent does not change a contracted rate
    /// (INV-AGENT-007): it becomes a question for a person.
    | Stated of Money

/// What the request asks for, as read.
type Intent =
    { /// The customer as the request names them.
      Customer: string
      /// A word the work must mention (in its project, activity type or
      /// description), such as `architecture`; None for all work.
      Work: string option
      From: DateOnly option
      Through: DateOnly option
      Rate: RateBasis
      Expenses: bool }

/// What Summa needs a person to settle before it can propose.
type Question =
    /// The request could not be read; the part that was not understood.
    | NotUnderstood of text: string
    /// No customer, or more than one, has this name.
    | WhichCustomer of named: string * candidates: string list
    | WhichDate of text: string
    | PeriodBackwards of from: DateOnly * through: DateOnly
    | NoMatchingWork of work: string
    /// The request names a rate: agents propose at the contracted rate only.
    | RateNotContracted of Money
    | NothingToBill
    | Refused of BillingProblem list

let private options = RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant

let private grammar =
    Regex(
        @"^\s*invoice\s+(?<customer>.+?)\s+for\s+(?:all\s+)?(?:approved\s+)?(?:(?<work>[\w\- ]+?)\s+)?work"
        + @"(?:\s+(?:from|between)\s+(?<from>.+?)\s+(?:through|to|until|and)\s+(?<through>.+?))?"
        + @"(?:\s+at\s+(?:the\s+)?(?:(?<contracted>contracted|agreed)\s+rates?|(?<rate>\$?[\d,]+(?:\.\d{1,2})?)\s*(?:usd\s*)?(?:per\s+hour|/\s*hour|an\s+hour|/\s*hr)))?"
        + @"(?:\s*,?\s+and\s+include\s+(?:approved\s+)?(?:reimbursable\s+)?(?<expenses>expenses))?"
        + @"\s*\.?\s*$",
        options
    )

/// A date as a person writes one: `2026-09-01`, `September 1`, `Sep 1, 2026`.
/// A date without a year takes the year of `today`.
let private dateOf (today: DateOnly) (text: string) =
    let text = text.Trim().TrimEnd(',')

    let formats = [| "yyyy-MM-dd"; "MMMM d, yyyy"; "MMMM d yyyy"; "MMM d, yyyy"; "MMM d yyyy"; "d MMMM yyyy"; "d MMM yyyy" |]

    match DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, d -> Ok d
    | _ ->
        match DateOnly.TryParseExact(text, [| "MMMM d"; "MMM d"; "d MMMM"; "d MMM" |], CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> Ok(DateOnly(today.Year, d.Month, d.Day))
        | _ -> Error(WhichDate text)

/// Reads a request in the documented phrasing (see the module comment).
let interpret (today: DateOnly) (text: string) : Result<Intent, Question list> =
    let found = grammar.Match text

    if not found.Success then
        Error [ NotUnderstood(text.Trim()) ]
    else
        let group (name: string) = if found.Groups[name].Success then Some(found.Groups[name].Value.Trim()) else None

        let dates =
            match group "from", group "through" with
            | Some f, Some t ->
                match dateOf today f, dateOf today t with
                | Ok f, Ok t when t < f -> Error [ PeriodBackwards(f, t) ]
                | Ok f, Ok t -> Ok(Some f, Some t)
                | f, t -> Error([ f; t ] |> List.choose (function Error q -> Some q | Ok _ -> None))
            | _ -> Ok(None, None)

        let rate =
            match group "rate" with
            | Some amount ->
                match tryParse "USD" (amount.Replace("$", "").Replace(",", "")) with
                | Some money -> Stated money
                | None -> Contracted
            | None -> Contracted

        dates
        |> Result.map (fun (from, through) ->
            { Customer = (group "customer").Value
              Work = group "work" |> Option.filter (fun w -> w <> "" && not (String.Equals(w, "billable", StringComparison.OrdinalIgnoreCase)))
              From = from
              Through = through
              Rate = rate
              Expenses = (group "expenses").IsSome })

let private same (a: string) (b: string) = String.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
let private mentions (word: string) (text: string) = text.Contains(word, StringComparison.OrdinalIgnoreCase)

/// The customer the request names: by name or billing name, exactly (case
/// aside), or else the one active customer whose name contains it.
let private customerOf (books: Books) (named: string) =
    let customers = books.Customers |> Map.toList |> List.map snd
    let exact = customers |> List.filter (fun c -> same c.Name named || same c.BillingName named)

    match exact with
    | [ one ] -> Ok one
    | [] ->
        match customers |> List.filter (fun c -> c.Active && (mentions named c.Name || mentions named c.BillingName)) with
        | [ one ] -> Ok one
        | many -> Error(WhichCustomer(named, many |> List.map _.Name |> List.sort))
    | many -> Error(WhichCustomer(named, many |> List.map _.Name |> List.sort))

let private within (intent: Intent) (d: DateOnly) =
    intent.From |> Option.forall (fun f -> d >= f) && intent.Through |> Option.forall (fun t -> d <= t)

/// The proposal the intent asks for, from the books as they stand: the
/// customer it names, their approved, billable, unbilled time in the period
/// (mentioning the work, when named) and, when asked, their billable,
/// unbilled expenses in the period. Rates come from the rate card.
let resolve (accounts: BillingAccounts) (proposalId: string) (intent: Intent) (r: Receivables) : Result<ProposalRequest, Question list> =
    match customerOf r.Books intent.Customer, intent.Rate with
    | Error question, _ -> Error [ question ]
    | _, Stated money -> Error [ RateNotContracted money ]
    | Ok customer, Contracted ->
        // What this same proposal already holds stays available to it, so
        // the same request again resolves to the same proposal.
        let used =
            Map.fold (fun acc key value -> Map.add key value acc) (consumed r) (reserved r.Books |> Map.filter (fun _ holder -> holder <> proposalId))

        let time =
            r.Books.Time
            |> Map.toList
            |> List.map snd
            |> List.filter (fun t -> (timeProblem r proposalId customer.Id None t).IsNone)
            |> List.filter (fun t -> within intent t.BusinessDate)
            |> List.filter (fun t ->
                intent.Work
                |> Option.forall (fun work -> mentions work t.ProjectId || mentions work t.ActivityTypeId || mentions work t.Description))
            |> List.sortBy (fun t -> t.BusinessDate, t.PublicationId)

        let expenses =
            if intent.Expenses then
                r.Books.Expenses
                |> Map.toList
                |> List.map snd
                |> List.filter (fun e -> e.Billable && e.CustomerId = Some customer.Id && within intent e.Date && not (used.ContainsKey $"expense:{e.Id}"))
                |> List.sortBy (fun e -> e.Date, e.Id)
            else
                []

        match intent.Work, time, expenses with
        | Some work, [], _ -> Error [ NoMatchingWork work ]
        | _, [], [] -> Error [ NothingToBill ]
        | _ ->
            Ok
                { ProposalId = proposalId
                  CustomerId = customer.Id
                  EngagementId = None
                  Currency = "USD"
                  Time = time |> List.map _.PublicationId
                  Grouping = [ ByProject ]
                  FixedFee = false
                  Milestones = []
                  Expenses = expenses |> List.map _.Id
                  Manual = []
                  Accounts = accounts }

/// Reads the request, resolves it and proposes the invoice, under the
/// agent's context with the request kept as the reason (INV-PROV-007). The
/// result is a proposal reserving its sources, waiting for a person.
let propose (context: Context) (accounts: BillingAccounts) (proposalId: string) (request: string) (r: Receivables) : Result<Receivables, Question list> =
    let today = DateOnly.FromDateTime context.When.UtcDateTime

    let withRequest =
        { context with
            Provenance = context.Provenance |> Option.map (fun p -> { p with Reason = Some $"request: {request.Trim()}" }) }

    interpret today request
    |> Result.bind (fun intent -> resolve accounts proposalId intent r)
    |> Result.bind (fun proposal -> Billing.propose withRequest proposal r |> Result.mapError (Refused >> List.singleton))

/// A question in words, for the agent to put to the person.
let describe =
    function
    | NotUnderstood text ->
        $"Summa could not read \"{text}\". Ask in the form: Invoice <customer> for [all] [approved] [<kind of>] work [from <date> through <date>] [at the contracted rate] [and include approved reimbursable expenses]."
    | WhichCustomer(named, []) -> $"No customer is called \"{named}\". Which customer is it?"
    | WhichCustomer(named, candidates) -> $"\"{named}\" could be " + String.concat " or " candidates + ". Which one?"
    | WhichDate text -> $"\"{text}\" is not a date Summa can read. Use a date such as 2026-09-01 or September 1."
    | PeriodBackwards(from, through) ->
        let iso (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        $"The period ends ({iso through}) before it starts ({iso from})."
    | NoMatchingWork work -> $"No approved, unbilled work for this customer mentions \"{work}\" in that period."
    | RateNotContracted money -> $"Agents propose at the contracted rate only. A person can change a line's rate to {Money.text money} on the proposal, with a reason."
    | NothingToBill -> "This customer has no approved, unbilled work or expenses in that period."
    | Refused problems -> "The proposal was refused: " + (problems |> List.map (fun p -> $"%A{p}") |> String.concat "; ")
