/// The accounting invariants Summa checks over any books, wherever they came
/// from (SUM0-029, SUM0-030, SUM3-004, SUM3-019). A violation is an
/// integrity failure, never a warning: a command does not run on books that
/// break one (SUM0-047).
///
/// Pure.
module Summa.Storage.Invariants

open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// One broken invariant: which rule, about what, and how.
type Violation = { Rule: string; Subject: string; Detail: string }

let private violation rule subject detail = { Rule = rule; Subject = subject; Detail = detail }

let private amountOf =
    function
    | Debit m
    | Credit m -> m

let private entryRules (ledger: Ledger) =
    [ for KeyValue(id, e) in ledger.Entries do
          match e.Lines |> List.map (fun l -> (amountOf l.Side).Currency) |> List.distinct with
          | [ currency ] ->
              let debits, credits = totals currency e.Lines
              if debits <> credits then violation "posted-entry-balances" id $"debits {debits} and credits {credits} differ"
          | [] -> violation "posted-entry-balances" id "the entry has no lines"
          | _ -> violation "posted-entry-balances" id "the entry mixes currencies"

          if e.Lines.Length < 2 then violation "posted-entry-balances" id "an entry needs at least two lines"

          for l in e.Lines do
              if not (isPositive (amountOf l.Side)) then violation "positive-amounts" id $"a line on {l.AccountId} is not positive"
              if not (ledger.Accounts.ContainsKey l.AccountId) then violation "known-accounts" id $"account {l.AccountId} is unknown"

          match e.Reverses with
          | Some original when not (ledger.Entries.ContainsKey original) ->
              violation "reversals-name-their-entry" id $"it reverses {original}, which does not exist"
          | Some original ->
              let mirrored =
                  ledger.Entries[original].Lines
                  |> List.map (fun l ->
                      { l with
                          Side =
                              match l.Side with
                              | Debit m -> Credit m
                              | Credit m -> Debit m })

              if mirrored <> e.Lines then violation "reversals-mirror-their-entry" id $"it does not mirror {original}"
          | None -> ()
      for original, reversals in
          ledger.Entries
          |> Map.toList
          |> List.choose (fun (_, e) -> e.Reverses |> Option.map (fun o -> o, e.Id))
          |> List.groupBy fst do
          if reversals.Length > 1 then violation "one-reversal-per-entry" original $"it is reversed {reversals.Length} times" ]

let private debitTo (ledger: Ledger) (entryId: string) =
    ledger.Entries.TryFind entryId
    |> Option.bind (fun e -> e.Lines |> List.tryPick (fun l -> match l.Side with Debit m -> Some(l.AccountId, m) | _ -> None))

let private invoiceRules (r: Receivables) =
    let books = r.Books

    [ for KeyValue(id, i) in books.Invoices do
          match books.Ledger.Entries.TryFind i.JournalEntryId with
          | None -> violation "issued-invoice-has-entry" id $"its journal entry {i.JournalEntryId} is missing"
          | Some _ ->
              match debitTo books.Ledger i.JournalEntryId with
              | Some(_, amount) when amount = i.Total -> ()
              | _ -> violation "issued-invoice-has-entry" id "its journal entry does not debit receivables by the invoice total"

          match books.Obligations.TryFind i.ObligationId with
          | None -> violation "issued-invoice-has-obligation" id $"its obligation {i.ObligationId} is missing"
          | Some o when o.OriginalAmount <> i.Total -> violation "issued-invoice-has-obligation" id "its obligation is not for the invoice total"
          | Some _ -> ()

          if i.Total <> add i.Subtotal (sum i.Currency i.Adjustments) then
              violation "invoice-totals-add-up" id "subtotal and adjustments do not make the total"

          if (outstanding r i).Minor < 0L then violation "no-negative-receivable" id $"it is overpaid by {subtract (amountPaid r i) i.Total}"
      for number, invoices in books.Invoices |> Map.toList |> List.groupBy (fun (_, i) -> i.Number) do
          if invoices.Length > 1 then violation "unique-invoice-numbers" number $"{invoices.Length} invoices share it" ]

let private paymentRules (r: Receivables) =
    [ for KeyValue(id, p) in r.Payments do
          if not (isPositive p.Amount) then violation "positive-amounts" id "the payment is not positive"
          if (unallocated r p).Minor < 0L then violation "allocation-within-payment" id "more is allocated than was received"
      for a in r.Allocations do
          if not (r.Payments.ContainsKey a.PaymentId) then violation "allocation-references" a.Id $"payment {a.PaymentId} is missing"
          if not (r.Books.Invoices.ContainsKey a.InvoiceId) then violation "allocation-references" a.Id $"invoice {a.InvoiceId} is missing"

          match debitTo r.Books.Ledger a.JournalEntryId with
          | None -> violation "allocation-has-entry" a.Id $"its journal entry {a.JournalEntryId} is missing"
          | Some(_, amount) when amount <> a.Amount -> violation "allocation-has-entry" a.Id "its journal entry is not for the allocated amount"
          | Some _ -> () ]

/// Every invariant the books break, in a stable order; empty when they hold.
let check (r: Receivables) : Violation list =
    entryRules r.Books.Ledger @ invoiceRules r @ paymentRules r
