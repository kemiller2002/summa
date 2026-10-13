/// Generated accounting in business language, on demand (INV-ACC-006):
/// what an entry did, line by line, in words a person who is not an
/// accountant can follow. Derived from the entry and the chart only.
///
/// Pure.
module Summa.Ledger.Explanations

open Summa.Ledger.Money
open Summa.Ledger.Ledger

let private amount (m: Money) = Documents.moneyText m

/// One sentence for a journal line.
let line (ledger: Ledger) (l: Line) =
    let account = ledger.Accounts.TryFind l.AccountId

    let name =
        account |> Option.map (fun a -> $"{a.Name} ({a.Code})") |> Option.defaultValue l.AccountId

    let about = l.Memo |> Option.map (fun m -> $" for {m}") |> Option.defaultValue ""

    match account |> Option.map _.Type, l.Side with
    | Some Asset, Debit m -> $"{name} goes up by {amount m}{about}: the business owns, or is owed, that much more."
    | Some Asset, Credit m -> $"{name} goes down by {amount m}{about}: the business owns, or is owed, that much less."
    | Some Liability, Credit m -> $"{name} goes up by {amount m}{about}: the business owes that much more."
    | Some Liability, Debit m -> $"{name} goes down by {amount m}{about}: the business owes that much less."
    | Some Equity, Credit m -> $"{name} goes up by {amount m}{about}: the owners' stake grows."
    | Some Equity, Debit m -> $"{name} goes down by {amount m}{about}: the owners' stake shrinks."
    | Some Revenue, Credit m -> $"{name}: {amount m} earned{about}."
    | Some Revenue, Debit m -> $"{name}: {amount m} less earned{about} (a discount, a correction or a reversal)."
    | Some Expense, Debit m -> $"{name}: {amount m} of cost{about}."
    | Some Expense, Credit m -> $"{name}: {amount m} less cost{about}."
    | None, Debit m -> $"{name} is debited {amount m}{about}."
    | None, Credit m -> $"{name} is credited {amount m}{about}."

/// What an entry did, in sentences: why it exists, then each line, then
/// that it balances.
let entry (ledger: Ledger) (e: PostedEntry) : string list =
    let why =
        match e.Reverses, e.Reference with
        | Some original, _ -> $"This entry reverses entry {original}: each line undoes the same line there. {e.Description}."
        | None, Some r -> $"This entry was posted for {r.Kind} {r.Id}: {e.Description}."
        | None, None -> $"This entry records: {e.Description}."

    let debits =
        e.Lines
        |> List.choose (fun l -> match l.Side with Debit m -> Some m | Credit _ -> None)
        |> List.reduce add

    why :: (e.Lines |> List.map (line ledger)) @ [ $"Debits and credits are equal: {amount debits} each, so the books still balance." ]
