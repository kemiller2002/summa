/// Attention-first receivables (v0.4 §10): which open invoices need action,
/// why, and in what order, so the default view helps decide what to do
/// rather than sorting by number. Everything here is derived from the
/// books; nothing is stored (DF-SUMMA-2026-0050).
module Summa.Ledger.Attention

open System
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// Why an open invoice needs attention.
type Reason =
    /// Past its due date by this many days.
    | Overdue of days: int
    /// Due within the next seven days; 0 is today.
    | DueThisWeek of days: int
    /// What it owes is at least a quarter of everything owed in its
    /// currency, among three or more open invoices; the share in percent.
    | LargeBalance of percent: int
    /// Open more than a week longer than this customer usually takes to
    /// pay, from at least three paid invoices.
    | UnusualDelay of openDays: int * usualDays: int

/// An open invoice, what it owes, and why it needs attention (empty when
/// nothing calls for it).
type Ranked =
    { Invoice: IssuedInvoice
      Outstanding: Money
      Reasons: Reason list }

/// The share of what is owed at which a balance counts as large.
let largeShare = 25

/// How many open invoices in a currency there must be before a balance is
/// called large: with fewer, every balance is a large share.
let largeAmongAtLeast = 3

/// How many paid invoices a customer needs before its usual time to pay
/// is known.
let usualFromAtLeast = 3

/// How much longer than usual an invoice must be open to be unusual.
let unusualAfterDays = 7

let private days (from: DateOnly) (until: DateOnly) = until.DayNumber - from.DayNumber

let private isOpen (r: Receivables) (i: IssuedInvoice) = (outstanding r i).Minor > 0L

/// The median number of days from issue to paid over the customer's paid
/// invoices; None with fewer than three.
let usualDaysToPay (r: Receivables) (customerId: string) : int option =
    let taken =
        r.Books.Invoices
        |> Map.toList
        |> List.map snd
        |> List.filter (fun i -> i.CustomerId = customerId)
        |> List.choose (fun i -> paidAt r i |> Option.map (days i.IssueDate))
        |> List.sort

    if taken.Length < usualFromAtLeast then
        None
    else
        let n = taken.Length
        Some(if n % 2 = 1 then taken[n / 2] else (taken[n / 2 - 1] + taken[n / 2]) / 2)

/// Why one open invoice needs attention today, most pressing first.
let reasons (today: DateOnly) (r: Receivables) (invoice: IssuedInvoice) : Reason list =
    let owed = outstanding r invoice

    if owed.Minor <= 0L then
        []
    else
        let openInCurrency =
            r.Books.Invoices
            |> Map.toList
            |> List.map snd
            |> List.filter (fun i -> i.Currency = invoice.Currency && isOpen r i)

        let totalOwed = openInCurrency |> List.sumBy (fun i -> (outstanding r i).Minor)
        let percent = if totalOwed = 0L then 0 else int (owed.Minor * 100L / totalOwed)
        let untilDue = days today invoice.DueDate
        let openDays = days invoice.IssueDate today

        [ if untilDue < 0 then Overdue -untilDue
          elif untilDue <= 7 then DueThisWeek untilDue
          if openInCurrency.Length >= largeAmongAtLeast && percent >= largeShare then LargeBalance percent
          match usualDaysToPay r invoice.CustomerId with
          | Some usual when openDays > usual + unusualAfterDays -> UnusualDelay(openDays, usual)
          | _ -> () ]

/// The order of the reasons' groups: overdue, due this week, large, unusual.
let private group =
    function
    | Overdue _ -> 0
    | DueThisWeek _ -> 1
    | LargeBalance _ -> 2
    | UnusualDelay _ -> 3

/// Within a group, the more pressing first.
let private pressure =
    function
    | Overdue d -> -d
    | DueThisWeek d -> d
    | LargeBalance p -> -p
    | UnusualDelay(openDays, usual) -> -(openDays - usual)

/// Every open invoice, those that need attention first: overdue (longest
/// overdue first), due this week (soonest first), large balances (largest
/// first), unusual delays (furthest past usual first); then the rest by
/// due date. Ties go to the larger amount owed, then the number.
let rank (today: DateOnly) (r: Receivables) : Ranked list =
    r.Books.Invoices
    |> Map.toList
    |> List.map snd
    |> List.filter (isOpen r)
    |> List.map (fun i ->
        { Invoice = i
          Outstanding = outstanding r i
          Reasons = reasons today r i })
    |> List.sortBy (fun x ->
        match x.Reasons with
        | first :: _ -> group first, pressure first, 0, -x.Outstanding.Minor, x.Invoice.Number
        | [] -> 4, 0, x.Invoice.DueDate.DayNumber, -x.Outstanding.Minor, x.Invoice.Number)

/// A reason in a sentence fragment, for the list and its reader.
let reasonText =
    function
    | Overdue 1 -> "Overdue 1 day"
    | Overdue d -> $"Overdue {d} days"
    | DueThisWeek 0 -> "Due today"
    | DueThisWeek 1 -> "Due tomorrow"
    | DueThisWeek d -> $"Due in {d} days"
    | LargeBalance p -> $"Large balance: {p}%% of everything owed"
    | UnusualDelay(openDays, usual) -> $"Open {openDays} days; this customer usually pays in {usual}"
