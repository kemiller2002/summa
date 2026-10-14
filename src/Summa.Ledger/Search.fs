/// One search over the books (v0.4 §18): customers, invoices, payments,
/// amounts, projects, journal entries and memos, each hit saying what it
/// is and what matched. Everything is read from the books in memory, so it
/// is fast enough to navigate by (DF-SUMMA-2026-0051).
module Summa.Ledger.Search

open System
open System.Globalization
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

type Kind =
    | CustomerHit
    | InvoiceHit
    | PaymentHit
    | JournalEntryHit
    | ProjectHit

/// One record found: its kind and id, how it is named, a line about it, and
/// the field that matched and its text.
type Hit =
    { Kind: Kind
      Id: string
      Title: string
      Detail: string
      Matched: string }

/// At most this many hits are returned; the count says how many there were.
let limit = 50

/// How closely a field matched: the whole field, its start, or inside it.
let private closeness (term: string) (text: string) =
    if String.Equals(text, term, StringComparison.OrdinalIgnoreCase) then Some 0
    elif text.StartsWith(term, StringComparison.OrdinalIgnoreCase) then Some 1
    elif text.Contains(term, StringComparison.OrdinalIgnoreCase) then Some 2
    else None

/// The query read as an amount ("1500", "1,500.00"), if it is one.
let private amountOf (query: string) =
    let plain = query.Replace(",", "").Trim()

    if plain.Length > 0 && plain |> Seq.forall (fun c -> Char.IsDigit c || c = '.') then
        match Decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
        | true, value when Decimal.Round(value, 2) = value -> Some(int64 (value * 100m))
        | _ -> None
    else
        None

/// The best-matching field of a record: (closeness, label, text).
let private best (term: string) (amount: int64 option) (fields: (string * string) list) (amounts: (string * Money) list) =
    let byText =
        fields |> List.choose (fun (label, text) -> closeness term text |> Option.map (fun c -> c, label, text))

    let byAmount =
        match amount with
        | Some minor -> amounts |> List.filter (fun (_, m) -> m.Minor = minor) |> List.map (fun (label, m) -> 0, label, Documents.moneyText m)
        | None -> []

    byAmount @ byText |> List.sortBy (fun (c, _, _) -> c) |> List.tryHead

let private sideAmount =
    function
    | Debit m
    | Credit m -> m

let private projectsOf (books: Books) =
    [ for KeyValue(_, t) in books.Time do
          t.ProjectId
      for KeyValue(_, i) in books.Invoices do
          yield! i.Lines |> List.choose _.Project
      for KeyValue(_, e) in books.Expenses do
          yield! Option.toList e.ProjectId
      for KeyValue(_, entry) in books.Ledger.Entries do
          yield! entry.Lines |> List.choose _.Dimensions.Project ]
    |> List.filter (fun p -> p.Trim() <> "")
    |> List.distinct

/// Every record matching the query, closest first (whole field, then start,
/// then inside), then by kind in the order customers, invoices, payments,
/// journal entries, projects; with how many matched in all. An empty query
/// finds nothing.
let find (r: Receivables) (query: string) : Hit list * int =
    let term = query.Trim()

    if term = "" then
        [], 0
    else
        let amount = amountOf term
        let books = r.Books
        let nameOf id = books.Customers.TryFind id |> Option.map _.Name |> Option.defaultValue id

        let hits =
            [ for KeyValue(_, c) in books.Customers do
                  match best term amount [ "name", c.Name; "billing name", c.BillingName; "email", c.Email; "id", c.Id ] [] with
                  | Some(close, label, text) -> close, { Kind = CustomerHit; Id = c.Id; Title = c.Name; Detail = "Customer"; Matched = $"{label}: {text}" }
                  | None -> ()
              for KeyValue(_, i) in books.Invoices do
                  let fields =
                      [ "number", i.Number
                        "customer", i.Customer.Name
                        yield! Lifecycle.searchTexts r i |> List.map (fun t -> "reference", t)
                        yield! i.Details.CustomerNotes |> Option.toList |> List.map (fun t -> "note", t)
                        yield! i.Details.InternalNotes |> Option.toList |> List.map (fun t -> "internal note", t) ]
                      // Amounts are matched as amounts, not as text inside other numbers.
                      |> List.filter (fun (_, t) -> amountOf t |> Option.isNone || t = i.Number)

                  match best term amount fields [ "total", i.Total; "outstanding", outstanding r i ] with
                  | Some(close, label, text) ->
                      close,
                      { Kind = InvoiceHit
                        Id = i.InvoiceId
                        Title = i.Number
                        Detail = $"Invoice · {i.Customer.Name} · {Documents.moneyText i.Total}"
                        Matched = $"{label}: {text}" }
                  | None -> ()
              for KeyValue(_, p) in r.Payments do
                  let fields =
                      [ "reference", p.Reference; "id", p.Id; "customer", nameOf p.CustomerId; yield! p.Memo |> Option.toList |> List.map (fun m -> "memo", m) ]

                  match best term amount fields [ "amount", p.Amount ] with
                  | Some(close, label, text) ->
                      close,
                      { Kind = PaymentHit
                        Id = p.Id
                        Title = (if p.Reference = "" then p.Id else p.Reference)
                        Detail = $"Payment · {nameOf p.CustomerId} · {Documents.moneyText p.Amount}"
                        Matched = $"{label}: {text}" }
                  | None -> ()
              for KeyValue(_, e) in books.Ledger.Entries do
                  let fields =
                      [ "id", e.Id; "description", e.Description; yield! e.Lines |> List.choose _.Memo |> List.map (fun m -> "memo", m) ]

                  match best term amount fields (e.Lines |> List.map (fun l -> "amount", sideAmount l.Side)) with
                  | Some(close, label, text) ->
                      let date = e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

                      close,
                      { Kind = JournalEntryHit
                        Id = e.Id
                        Title = e.Id
                        Detail = $"Journal entry · {date} · {e.Description}"
                        Matched = $"{label}: {text}" }
                  | None -> ()
              for project in projectsOf books do
                  match closeness term project with
                  | Some close -> close, { Kind = ProjectHit; Id = project; Title = project; Detail = "Project"; Matched = $"project: {project}" }
                  | None -> () ]

        let kindOrder =
            function
            | CustomerHit -> 0
            | InvoiceHit -> 1
            | PaymentHit -> 2
            | JournalEntryHit -> 3
            | ProjectHit -> 4

        let ordered = hits |> List.sortBy (fun (close, h) -> close, kindOrder h.Kind, h.Title) |> List.map snd
        List.truncate limit ordered, ordered.Length
