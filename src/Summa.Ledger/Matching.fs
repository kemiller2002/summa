/// Matching a payment to the invoices it pays (v0.4 §12). Summa suggests an
/// application only when exactly one reading of the payment fits the
/// customer's open invoices; when several do, it names them and a person
/// chooses. It never guesses silently, and a suggestion applies nothing:
/// applying stays an explicit allocation.
///
/// Pure.
module Summa.Ledger.Matching

open System
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// What the books say a payment pays.
type Suggestion =
    /// One reading fits: apply these amounts to these invoices, and why.
    | ApplyTo of applications: (IssuedInvoice * Money) list * because: string
    /// Several readings fit, or none does: a person chooses among these, and why.
    | Choose of candidates: IssuedInvoice list * because: string
    /// The customer owes nothing on any invoice.
    | NothingToApplyTo

/// The customer's invoices that still owe something, the earliest due first.
let openFor (r: Receivables) (customerId: string) =
    r.Books.Invoices
    |> Map.toList
    |> List.map snd
    |> List.filter (fun i -> i.CustomerId = customerId && status r i <> Voided && (outstanding r i).Minor > 0L)
    |> List.sortBy (fun i -> i.DueDate, i.Number)

/// The open invoices the payment's reference names by number.
let private named (payment: Payment) (candidates: IssuedInvoice list) =
    candidates |> List.filter (fun i -> payment.Reference.Contains(i.Number, StringComparison.OrdinalIgnoreCase))

/// What the payment's unapplied amount pays, read in this order:
/// 1. the invoices its reference names by number;
/// 2. the one invoice that owes exactly the amount;
/// 3. all the open invoices, when the amount is exactly what they owe together;
/// 4. the only open invoice.
/// Two invoices owing the same amount, or an amount no reading explains, is
/// left to a person.
let suggest (r: Receivables) (payment: Payment) =
    let amount = unallocated r payment
    let candidates = openFor r payment.CustomerId
    let owed (i: IssuedInvoice) = outstanding r i
    let total (invoices: IssuedInvoice list) = invoices |> List.sumBy (fun i -> (owed i).Minor)
    let text (m: Money) = Documents.moneyText m

    if amount.Minor <= 0L || candidates.IsEmpty then
        NothingToApplyTo
    else
        match named payment candidates with
        | [ only ] ->
            let applied = if amount.Minor <= (owed only).Minor then amount else owed only
            ApplyTo([ only, applied ], $"its reference names {only.Number}")
        | (_ :: _ :: _) as several when total several = amount.Minor ->
            let numbers = several |> List.map _.Number |> String.concat " and "
            ApplyTo(several |> List.map (fun i -> i, owed i), $"its reference names {numbers}, which together owe exactly {text amount}")
        | _ ->
            match candidates |> List.filter (fun i -> (owed i).Minor = amount.Minor) with
            | [ exact ] -> ApplyTo([ exact, amount ], $"{exact.Number} owes exactly {text amount}")
            | (_ :: _ :: _) as same -> Choose(same, $"{same.Length} invoices each owe exactly {text amount}")
            | [] when candidates.Length > 1 && total candidates = amount.Minor ->
                ApplyTo(candidates |> List.map (fun i -> i, owed i), $"it is exactly what the {candidates.Length} open invoices owe together")
            | [] ->
                match candidates with
                | [ only ] ->
                    let applied = if amount.Minor <= (owed only).Minor then amount else owed only
                    ApplyTo([ only, applied ], $"{only.Number} is the only open invoice")
                | _ -> Choose(candidates, $"no open invoice owes {text amount}, and it is not what they owe together")
