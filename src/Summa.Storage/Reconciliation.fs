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
