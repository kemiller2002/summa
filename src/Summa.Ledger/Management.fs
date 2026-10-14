/// Management reporting (v0.4 §29-30): revenue and expenses broken down by
/// client, project and engagement, engagement contribution, and what each
/// customer owes. Management reports, not formal statements: each is
/// derived from the posted journal and the books, and nothing is stored
/// (DF-SUMMA-2026-0053).
module Summa.Ledger.Management

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// What a breakdown groups by.
type By =
    | ByClient
    | ByProject
    | ByEngagement

/// One group and its amount. `Key` is None for amounts with nothing to
/// group them by, shown as unassigned rather than dropped.
type Row =
    { Key: string option
      Label: string
      Amount: Money }

/// An engagement's contribution: its revenue less the costs Summa holds.
/// Labor, AI and infrastructure costs have no source in Summa yet, so they
/// are None, never zero (DF-SUMMA-2026-0053).
type Contribution =
    { Key: string option
      Label: string
      Revenue: Money
      ExternalExpenses: Money
      LaborCost: Money option
      AiCost: Money option
      InfrastructureCost: Money option
      Contribution: Money }

let unassigned = "Unassigned"

let private amountOf (l: Line) =
    match l.Side with
    | Debit m
    | Credit m -> m

/// The posted lines on accounts of one type in the period and currency,
/// with the entry they belong to. Year-end closing is left out, as in the
/// income statement.
let private linesOf (r: Receivables) (accountType: AccountType) (currency: string) (from: DateOnly) (until: DateOnly) =
    let ledger = Reports.withoutClosing r.Books.Ledger

    [ for id in ledger.Journal do
          let e = ledger.Entries[id]

          if e.Date >= from && e.Date <= until then
              for l in e.Lines do
                  match ledger.Accounts.TryFind l.AccountId with
                  | Some a when a.Type = accountType && (amountOf l).Currency = currency -> e, l
                  | _ -> () ]

/// The invoice an entry was posted for, if any.
let private invoiceOf (r: Receivables) (e: PostedEntry) =
    match e.Reference with
    | Some { Kind = "invoice"; Id = id } -> r.Books.Invoices.TryFind id
    | _ -> None

/// The group a line falls in. The engagement of an invoice's revenue is the
/// invoice's, since its lines are posted by client and project.
let private keyOf (r: Receivables) (by: By) (e: PostedEntry, l: Line) =
    match by with
    | ByClient -> l.Dimensions.Client
    | ByProject -> l.Dimensions.Project
    | ByEngagement -> l.Dimensions.Engagement |> Option.orElse (invoiceOf r e |> Option.bind _.EngagementId)

let private labelOf (r: Receivables) (by: By) (key: string option) =
    match by, key with
    | _, None -> unassigned
    | ByClient, Some id -> r.Books.Customers.TryFind id |> Option.map _.Name |> Option.defaultValue id
    | ByEngagement, Some id -> r.Books.Engagements.TryFind id |> Option.map _.Name |> Option.defaultValue id
    | ByProject, Some id -> id

/// Revenue earned less revenue given back, for an account type's natural
/// side: credits for revenue, debits for expenses.
let private signed (accountType: AccountType) (l: Line) =
    match accountType, l.Side with
    | Revenue, Credit m
    | Expense, Debit m -> m.Minor
    | Revenue, Debit m
    | Expense, Credit m -> -m.Minor
    | _ -> 0L

let private breakdown (accountType: AccountType) (by: By) (r: Receivables) (currency: string) (from: DateOnly) (until: DateOnly) : Row list =
    linesOf r accountType currency from until
    |> List.groupBy (keyOf r by)
    |> List.map (fun (key, lines) ->
        { Key = key
          Label = labelOf r by key
          Amount = { Currency = currency; Minor = lines |> List.sumBy (snd >> signed accountType) } })
    |> List.filter (fun row -> row.Amount.Minor <> 0L)
    // Largest first; unassigned last.
    |> List.sortBy (fun row -> row.Key.IsNone, -row.Amount.Minor, row.Label)

/// Revenue in the period by client, project or engagement.
let revenueBy (by: By) (r: Receivables) (currency: string) (from: DateOnly) (until: DateOnly) = breakdown Revenue by r currency from until

/// Expenses in the period by client, project or engagement.
let expensesBy (by: By) (r: Receivables) (currency: string) (from: DateOnly) (until: DateOnly) = breakdown Expense by r currency from until

/// Each engagement's revenue less its external expenses in the period
/// (v0.4 §30). Costs Summa has no source for are None.
let contribution (r: Receivables) (currency: string) (from: DateOnly) (until: DateOnly) : Contribution list =
    let revenue = revenueBy ByEngagement r currency from until |> List.map (fun x -> x.Key, x.Amount) |> Map.ofList
    let expenses = expensesBy ByEngagement r currency from until |> List.map (fun x -> x.Key, x.Amount) |> Map.ofList
    let none = zero currency

    Set.union (revenue.Keys |> Set.ofSeq) (expenses.Keys |> Set.ofSeq)
    |> Set.toList
    |> List.map (fun key ->
        let earned = revenue.TryFind key |> Option.defaultValue none
        let spent = expenses.TryFind key |> Option.defaultValue none

        { Key = key
          Label = labelOf r ByEngagement key
          Revenue = earned
          ExternalExpenses = spent
          LaborCost = None
          AiCost = None
          InfrastructureCost = None
          Contribution = subtract earned spent })
    |> List.sortBy (fun c -> c.Key.IsNone, -c.Contribution.Minor, c.Label)

/// What each customer owes, as of a date, largest first.
let outstandingByCustomer (r: Receivables) (currency: string) (asOf: DateOnly) : Row list =
    let books = Payments.asOf asOf r

    books.Books.Invoices
    |> Map.toList
    |> List.map snd
    |> List.filter (fun i -> i.Currency = currency)
    |> List.groupBy _.CustomerId
    |> List.map (fun (customerId, invoices) ->
        { Key = Some customerId
          Label = labelOf r ByClient (Some customerId)
          Amount = invoices |> List.map (outstanding books) |> sum currency })
    |> List.filter (fun row -> row.Amount.Minor <> 0L)
    |> List.sortBy (fun row -> -row.Amount.Minor, row.Label)
