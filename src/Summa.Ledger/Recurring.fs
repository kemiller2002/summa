/// Recurring billing (INV-REC-001 to 004). A recurring template is a
/// definition, not an invoice: each scheduled occurrence becomes an invoice
/// proposal a person reviews (INV-REC-001). Templates are versioned and a
/// version never changes, so an edit affects later proposals only
/// (INV-REC-003). Each occurrence has one proposal id, derived from the
/// template and the date, so a retry or a restarted scheduler finds the
/// proposal already there and makes no second one (INV-REC-004).
///
/// Pure.
module Summa.Ledger.Recurring

open System
open System.Globalization
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

type RecurringProblem =
    | UnknownTemplate of string
    | UnknownCustomerForTemplate of string
    | NoTemplateLines
    | InvalidSchedule of string
    | LineCurrency of expected: string * found: string
    /// Versions only move forward, one at a time.
    | VersionNotNext of expected: int
    | ProposalRefused of Billing.BillingProblem list

/// A template's versions, oldest first.
let versions (books: Books) (templateId: string) =
    books.Templates |> Map.toList |> List.map snd |> List.filter (fun t -> t.Id = templateId) |> List.sortBy _.Version

/// The version that later proposals use.
let latest (books: Books) (templateId: string) = versions books templateId |> List.tryLast

let private problems (books: Books) (t: RecurringTemplate) =
    [ if not (books.Customers.ContainsKey t.CustomerId) then UnknownCustomerForTemplate t.CustomerId
      if t.Lines.IsEmpty then NoTemplateLines
      for line in t.Lines do
          if line.UnitPrice.Currency <> t.Currency then LineCurrency(t.Currency, line.UnitPrice.Currency)
      match t.Schedule with
      | Monthly day when day < 1 || day > 28 -> InvalidSchedule "a monthly day is from 1 to 28"
      | EveryWeeks weeks when weeks < 1 -> InvalidSchedule "weeks are at least 1"
      | _ -> ()
      match t.EndsOn with
      | Some ends when ends < t.StartsOn -> InvalidSchedule "it ends before it starts"
      | _ -> ()
      match t.Occurrences with
      | Some n when n < 1 -> InvalidSchedule "occurrences are at least 1"
      | _ -> () ]
    |> List.distinct

/// Saves a template version. The first is version 1; an edit is the next
/// version, and the earlier ones are kept unchanged. Saving the same
/// version again with the same content changes nothing.
let saveTemplate (context: Context) (template: RecurringTemplate) (books: Books) =
    let expected = latest books template.Id |> Option.map (fun t -> t.Version + 1) |> Option.defaultValue 1

    match books.Templates.TryFind $"{template.Id}@{template.Version}" with
    | Some existing when existing = template -> Ok books
    | _ when template.Version <> expected -> Error [ VersionNotNext expected ]
    | _ ->
        match problems books template with
        | [] ->
            Ok
                { books with
                    Templates = books.Templates.Add($"{template.Id}@{template.Version}", template)
                    Ledger = audit context "recurring-template-saved" $"{template.Id}@{template.Version}" books.Ledger }
        | found -> Error found

/// Every date the template falls due, in order, within its start, end and
/// count. Lazy: a template without an end is unbounded.
let occurrences (t: RecurringTemplate) : DateOnly seq =
    let dates =
        match t.Schedule with
        | Monthly day ->
            let first = DateOnly(t.StartsOn.Year, t.StartsOn.Month, day)
            let first = if first < t.StartsOn then first.AddMonths 1 else first
            Seq.initInfinite first.AddMonths
        | EveryWeeks weeks -> Seq.initInfinite (fun n -> t.StartsOn.AddDays(n * 7 * weeks))

    let bounded =
        match t.EndsOn with
        | Some ends -> dates |> Seq.takeWhile (fun d -> d <= ends)
        | None -> dates

    match t.Occurrences with
    | Some n -> bounded |> Seq.truncate n
    | None -> bounded

/// The one proposal id of an occurrence (INV-REC-004).
let occurrenceId (templateId: string) (date: DateOnly) =
    templateId + "-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)

let private generated (books: Books) (templateId: string) (date: DateOnly) =
    books.Proposals.ContainsKey(occurrenceId templateId date)

/// The next date a proposal will be generated, derived from the template
/// and the proposals already made; None when it has ended or is paused.
let nextGeneration (books: Books) (templateId: string) =
    latest books templateId
    |> Option.filter _.Active
    |> Option.bind (fun t -> occurrences t |> Seq.tryFind (generated books templateId >> not))

/// Makes a proposal for every occurrence due by `today` that has none yet,
/// from the template's latest version. Never a draft or an invoice
/// (INV-REC-001). Running it again, or after a restart, makes nothing new
/// (INV-REC-004). Returns the books and the proposal ids it made.
let generateDue (context: Context) (today: DateOnly) (templateId: string) (r: Receivables) =
    match latest r.Books templateId with
    | None -> Error [ UnknownTemplate templateId ]
    | Some t when not t.Active -> Ok(r, [])
    | Some t ->
        let due = occurrences t |> Seq.takeWhile (fun d -> d <= today) |> Seq.filter (generated r.Books templateId >> not) |> List.ofSeq
        let revenue = t.Lines.Head.RevenueAccountId

        let make (state: Result<Receivables * string list, RecurringProblem list>) (date: DateOnly) =
            state
            |> Result.bind (fun (current, made) ->
                let id = occurrenceId t.Id date

                let request: Billing.ProposalRequest =
                    { ProposalId = id
                      CustomerId = t.CustomerId
                      EngagementId = t.EngagementId
                      Currency = t.Currency
                      Time = []
                      Grouping = []
                      FixedFee = false
                      Milestones = []
                      Expenses = []
                      Manual = t.Lines
                      Accounts = { TimeRevenue = revenue; FeeRevenue = revenue; ReimbursedExpenses = revenue } }

                Billing.propose context request current
                |> Result.mapError (fun found -> [ ProposalRefused found ])
                |> Result.map (fun proposed ->
                    let proposal = proposed.Books.Proposals[id]

                    let on = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

                    let note: Contribution =
                        { Who = context.Who
                          What = $"generated from recurring template {t.Id} version {t.Version} for {on}"
                          At = context.When
                          Provenance = context.Provenance }

                    let books =
                        { proposed.Books with
                            Proposals =
                                proposed.Books.Proposals.Add(
                                    id,
                                    { proposal with
                                        Contributions = proposal.Contributions @ [ note ]
                                        Occurrence = Some { TemplateId = t.Id; TemplateVersion = t.Version; Date = date } }
                                )
                            Ledger = audit context "recurring-proposal-generated" id proposed.Books.Ledger }

                    { proposed with Books = books }, made @ [ id ]))

        due |> List.fold make (Ok(r, []))
