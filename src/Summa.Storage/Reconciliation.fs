/// Reconciliation jobs (SUM3-020): independent checks that the books agree
/// with themselves, run on a schedule and on demand. Each finding is an
/// integrity failure, never a warning.
///
/// Pure.
module Summa.Storage.Reconciliation

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

type Finding = { Check: string; Detail: string }

/// The checks, as of `asOf`, with the organization's receivables account.
let run (receivablesAccount: string) (asOf: DateOnly) (r: Receivables) : Finding list =
    let ledger = r.Books.Ledger

    let currencies =
        ledger.Entries
        |> Map.toList
        |> List.collect (fun (_, e) ->
            e.Lines
            |> List.map (fun l ->
                match l.Side with
                | Debit m
                | Credit m -> m.Currency))
        |> List.distinct

    let posted = ledger.Entries |> Map.toList |> List.map snd |> List.filter (fun e -> e.Date <= asOf)

    [ for currency in currencies do
          // Journal debits = journal credits.
          let debits, credits = posted |> List.collect _.Lines |> List.filter (fun l -> (match l.Side with Debit m | Credit m -> m.Currency) = currency) |> totals currency

          if debits <> credits then
              { Check = "journal-balances"; Detail = $"{currency}: debits {debits}, credits {credits}" }

          // AR control account = open invoice receivables.
          if ledger.Accounts.ContainsKey receivablesAccount then
              let control = balance ledger currency asOf receivablesAccount

              let open' =
                  r.Books.Invoices
                  |> Map.toList
                  |> List.map snd
                  |> List.filter (fun i -> i.Currency = currency && i.IssueDate <= asOf)
                  |> List.map (outstanding r)
                  |> sum currency

              if control <> open' then
                  { Check = "receivables-control"; Detail = $"{currency}: the receivables account holds {control}, open invoices {open'}" }
      // Payments allocated <= payments received.
      for KeyValue(id, p) in r.Payments do
          if (unallocated r p).Minor < 0L then
              { Check = "allocations-within-payments"; Detail = $"{id} has more allocated than received" }
      // Invoice balances reconcile: none below zero.
      for KeyValue(id, i) in r.Books.Invoices do
          if (outstanding r i).Minor < 0L then
              { Check = "invoice-balances"; Detail = $"{id} is overpaid" } ]

/// Report consistency (SUM3-027): the receivable projection, rebuilt from
/// the same records, agrees with the books. The balance sheet's receivables
/// are the control account, which `run` checks against the open invoices,
/// so the balance sheet, the receivables subsidiary and the projection all
/// say the same, or a finding says where they differ.
let agreesWithIndex (receivablesAccount: string) (r: Receivables) (index: Arca.DerivedIndex) : Finding list =
    let ledger = r.Books.Ledger
    let projected = Projection.balances index
    let owed = Projection.outstanding index

    [ for KeyValue((account, currency), minor) in projected do
          if account = receivablesAccount then
              let books = (balance ledger currency DateOnly.MaxValue account).Minor

              if books <> minor then
                  { Check = "projection-receivables"; Detail = $"{currency}: the projection holds {minor}, the books {books}" }
      for KeyValue(id, i) in r.Books.Invoices do
          let books = (outstanding r i).Minor
          let index = owed.TryFind id |> Option.defaultValue 0L

          if books <> index then
              { Check = "projection-outstanding"; Detail = $"{id}: the projection holds {index}, the books {books}" } ]
