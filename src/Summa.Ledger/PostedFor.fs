/// Why an entry was posted, as the business event it records (v0.4 §21):
/// "Invoice EF-2026-0001 was issued to ABC Corp for 6,050.00 USD." Read
/// from the entry's source and the books; nothing is stored.
module Summa.Ledger.PostedFor

open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// The business event an entry records, in one sentence.
let why (r: Receivables) (e: PostedEntry) : string =
    let books = r.Books

    let total =
        e.Lines |> List.choose (fun l -> match l.Side with Debit m -> Some m | Credit _ -> None) |> List.reduce add |> Documents.moneyText

    let customer =
        e.Lines
        |> List.tryPick _.Dimensions.Client
        |> Option.bind books.Customers.TryFind
        |> Option.map _.Name
        |> Option.defaultValue "the customer"

    let kind, id =
        match e.Source.IndexOf ':' with
        | -1 -> e.Source, ""
        | at -> e.Source.Substring(0, at), e.Source.Substring(at + 1)

    let invoice =
        match e.Reference with
        | Some { Kind = "invoice"; Id = invoiceId } -> books.Invoices.TryFind invoiceId
        | _ when kind = "invoice" -> books.Invoices |> Map.tryPick (fun _ i -> if i.Number = id || i.InvoiceId = id then Some i else None)
        | _ -> None

    match invoice, kind with
    | Some i, _ -> $"Invoice {i.Number} was issued to {i.Customer.Name} for {Documents.moneyText i.Total}."
    | None, "payment" ->
        let reference = r.Payments.TryFind id |> Option.map (fun p -> $" ({p.Reference})") |> Option.defaultValue ""
        $"{customer} paid {total}{reference}, and it was applied to what they owe: {e.Description}."
    | None, "credit" -> $"{customer} paid {total} more than they owed, and it was kept as a credit for them."
    | None, "deposit" -> $"{customer} paid a deposit of {total} before any invoice."
    | None, "credit-memo" -> $"A credit memo for {total} was issued to {customer}, reducing what they owe: {e.Description}."
    | None, "application" -> $"{total} of {customer}'s credit was applied to what they owe: {e.Description}."
    | None, "refund" -> $"{total} was refunded to {customer}."
    | None, "payment-reversal" -> $"A payment from {customer} came back, so {total} is owed again: {e.Description}."
    | None, "write-off" -> $"{total} that {customer} owed was written off as not collectible: {e.Description}."
    | None, "expense" -> $"An expense of {total} was recorded: {e.Description}."
    | None, "reversal" -> $"This entry undoes entry {id}, line by line: {e.Description}."
    | None, "opening-balance" -> $"The opening balances of these books were recorded: {e.Description}."
    | None, "adjustment" -> $"An accountant recorded an adjustment: {e.Description}."
    | None, "year-end-close" -> $"The year's revenue and expenses were closed into the owners' equity: {e.Description}."
    | None, _ ->
        match e.Reverses with
        | Some original -> $"This entry undoes entry {original}, line by line: {e.Description}."
        | None -> $"This entry records: {e.Description}."

/// The whole explanation: the event, then each line and its reason, then
/// that the entry balances.
let explain (r: Receivables) (e: PostedEntry) : string list = Explanations.entryBecause (why r e) r.Books.Ledger e
