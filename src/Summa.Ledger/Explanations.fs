/// Generated accounting in business language, on demand (INV-ACC-006):
/// what an entry did, line by line, in words a person who is not an
/// accountant can follow. Derived from the entry and the chart only.
///
/// Pure.
module Summa.Ledger.Explanations

open Summa.Ledger.Money
open Summa.Ledger.Ledger

let private amount (m: Money) = Documents.moneyText m

/// One sentence for a journal line, in the form v0.4 §21 shows: the
/// side, the account and the amount, then why in business words.
let line (ledger: Ledger) (l: Line) =
    let account = ledger.Accounts.TryFind l.AccountId

    let name =
        account |> Option.map (fun a -> $"{a.Name} ({a.Code})") |> Option.defaultValue l.AccountId

    let about = l.Memo |> Option.map (fun m -> $" for {m}") |> Option.defaultValue ""

    let side, m =
        match l.Side with
        | Debit m -> "Debit", m
        | Credit m -> "Credit", m

    let because =
        match account |> Option.map _.Type, l.Side with
        | Some Asset, Debit _ -> Some "the business owns, or is owed, that much more"
        | Some Asset, Credit _ -> Some "the business owns, or is owed, that much less"
        | Some Liability, Credit _ -> Some "the business owes that much more"
        | Some Liability, Debit _ -> Some "the business owes that much less"
        | Some Equity, Credit _ -> Some "the owners' stake grows by that much"
        | Some Equity, Debit _ -> Some "the owners' stake shrinks by that much"
        | Some Revenue, Credit _ -> Some "that much was earned"
        | Some Revenue, Debit _ -> Some "that much less was earned (a discount, a correction or a reversal)"
        | Some Expense, Debit _ -> Some "that much was spent"
        | Some Expense, Credit _ -> Some "that much less was spent"
        | None, _ -> None

    match because with
    | Some reason -> $"{side} {name} {amount m}{about}, because {reason}."
    | None -> $"{side} {name} {amount m}{about}."

/// Why an entry exists when nothing more is known than the entry itself.
let private plainWhy (e: PostedEntry) =
    match e.Reverses, e.Reference with
    | Some original, _ -> $"This entry reverses entry {original}: each line undoes the same line there. {e.Description}."
    | None, Some r -> $"This entry was posted for {r.Kind} {r.Id}: {e.Description}."
    | None, None -> $"This entry records: {e.Description}."

/// What an entry did, in sentences: why it exists (`why`), then each
/// line, then that it balances.
let entryBecause (why: string) (ledger: Ledger) (e: PostedEntry) : string list =
    let debits =
        e.Lines
        |> List.choose (fun l -> match l.Side with Debit m -> Some m | Credit _ -> None)
        |> List.reduce add

    why :: (e.Lines |> List.map (line ledger)) @ [ $"Debits and credits are equal: {amount debits} each, so the books still balance." ]

/// What an entry did, from the entry and the chart alone.
let entry (ledger: Ledger) (e: PostedEntry) : string list = entryBecause (plainWhy e) ledger e
