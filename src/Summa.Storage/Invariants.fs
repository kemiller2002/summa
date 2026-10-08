/// The accounting invariants Summa checks over any books, wherever they came
/// from (SUM0-029, SUM0-030, SUM3-004, SUM3-019). A violation is an
/// integrity failure, never a warning: a command does not run on books that
/// break one (SUM0-047).
///
/// Pure.
module Summa.Storage.Invariants

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Sources

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

          if i.Subtotal <> (i.Lines |> List.map lineAmount |> sum i.Currency) then
              violation "invoice-totals-add-up" id "the lines do not make the subtotal"

          if i.Total <> add (subtract i.Subtotal (discountTotal i.Currency i.Subtotal i.Discounts)) (sum i.Currency i.Adjustments) then
              violation "invoice-totals-add-up" id "subtotal, discounts and adjustments do not make the total"

          match i.Corrects with
          | Some original when not (books.Invoices.ContainsKey original) -> violation "corrections-name-their-invoice" id $"it corrects {original}, which is not issued"
          | _ -> ()

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

let private creditRules (r: Receivables) =
    let ledger = r.Books.Ledger
    let hasEntry id = ledger.Entries.ContainsKey id

    let sourceCustomer =
        function
        | Summa.Ledger.Payments.FromCredit id -> r.Credits.TryFind id |> Option.map _.CustomerId
        | Summa.Ledger.Payments.FromDeposit id -> r.Deposits.TryFind id |> Option.map _.CustomerId
        | Summa.Ledger.Payments.FromCreditMemo id -> r.CreditMemos.TryFind id |> Option.map _.CustomerId

    [ for KeyValue(id, c) in r.Credits do
          if not (hasEntry c.JournalEntryId) then violation "credit-has-entry" id $"its journal entry {c.JournalEntryId} is missing"
          if not (r.Payments.ContainsKey c.SourcePaymentId) then violation "credit-references" id $"payment {c.SourcePaymentId} is missing"
      for KeyValue(id, d) in r.Deposits do
          if not (hasEntry d.JournalEntryId) then violation "deposit-has-entry" id $"its journal entry {d.JournalEntryId} is missing"
          if not (isPositive d.Amount) then violation "positive-amounts" id "the deposit is not positive"
      for KeyValue(id, m) in r.CreditMemos do
          if not (hasEntry m.JournalEntryId) then violation "credit-memo-has-entry" id $"its journal entry {m.JournalEntryId} is missing"
      for a in r.Applications do
          match sourceCustomer a.Source, r.Books.Invoices.TryFind a.InvoiceId with
          | None, _ -> violation "application-references" a.Id "its credit, deposit or credit memo is missing"
          | _, None -> violation "application-references" a.Id $"invoice {a.InvoiceId} is missing"
          | Some customer, Some invoice when customer <> invoice.CustomerId -> violation "application-references" a.Id "it applies one customer's credit to another's invoice"
          | _ -> ()

          if not (hasEntry a.JournalEntryId) then violation "application-has-entry" a.Id $"its journal entry {a.JournalEntryId} is missing"
      for KeyValue(id, f) in r.Refunds do
          if sourceCustomer f.Source <> Some f.CustomerId then violation "refund-references" id "its credit or deposit is missing or another customer's"
          if not (hasEntry f.JournalEntryId) then violation "refund-has-entry" id $"its journal entry {f.JournalEntryId} is missing"
      for source in
          (r.Credits |> Map.keys |> Seq.map Summa.Ledger.Payments.FromCredit |> List.ofSeq)
          @ (r.Deposits |> Map.keys |> Seq.map Summa.Ledger.Payments.FromDeposit |> List.ofSeq)
          @ (r.CreditMemos |> Map.keys |> Seq.map Summa.Ledger.Payments.FromCreditMemo |> List.ofSeq) do
          match Summa.Ledger.Credits.remaining r source with
          | Some left when left.Minor < 0L -> violation "credit-within-amount" $"%A{source}" "more was applied or refunded than it held"
          | _ -> ()
      for KeyValue(id, v) in r.Reversals do
          if not (r.Payments.ContainsKey id) then violation "reversal-references" id "the reversed payment is missing"

          for e in v.JournalEntryIds do
              if not (hasEntry e) then violation "reversal-has-entries" id $"its journal entry {e} is missing"
      for KeyValue(id, w) in r.WriteOffs do
          if not (r.Books.Invoices.ContainsKey w.InvoiceId) then violation "write-off-references" id $"invoice {w.InvoiceId} is missing"
          if not (hasEntry w.JournalEntryId) then violation "write-off-has-entry" id $"its journal entry {w.JournalEntryId} is missing" ]

let private voidRules (r: Receivables) =
    let books = r.Books

    [ for KeyValue(id, v) in r.Voids do
          match books.Invoices.TryFind id with
          | None -> violation "void-references" id "the voided invoice is missing"
          | Some invoice ->
              match books.Ledger.Entries.TryFind v.JournalEntryId with
              | Some e when e.Reverses = Some invoice.JournalEntryId -> ()
              | _ -> violation "void-reverses-invoice-entry" id $"{v.JournalEntryId} does not reverse the invoice's entry"

              match books.Obligations.TryFind invoice.ObligationId with
              | Some o when o.Cancelled -> ()
              | _ -> violation "void-cancels-obligation" id "its obligation is not cancelled"

              if (amountPaid r invoice).Minor > 0L || (writtenOff r invoice).Minor > 0L then
                  violation "voided-invoice-unsettled" id "payments, credits or write-offs count against a voided invoice"
      for KeyValue(_, i) in books.Invoices do
          match books.Obligations.TryFind i.ObligationId with
          | Some o when o.Cancelled && not (r.Voids.ContainsKey i.InvoiceId) ->
              violation "only-voids-cancel" i.InvoiceId "its obligation is cancelled, but the invoice is not voided"
          | _ -> () ]

let private billingRules (r: Receivables) =
    let books = r.Books
    let live = books.Invoices |> Map.filter (fun id _ -> not (r.Voids.ContainsKey id))

    let liveProposals =
        books.Proposals
        |> Map.filter (fun _ p ->
            match p.State with
            | Proposed
            | ReadyForReview -> true
            | _ -> false)

    let sourceProblems subject (line: InvoiceLine) =
        [ match line.Source with
          | TimeSource refs ->
              for t in refs do
                  match books.Time.TryFind t.PublicationId with
                  | None -> violation "sources-exist" subject $"time {t.PublicationId} was never imported"
                  | Some found when found.ActivityId <> t.ActivityId || found.Revision <> t.Revision ->
                      violation "sources-exist" subject $"time {t.PublicationId} is not the activity revision it names"
                  | Some _ -> ()
          | ExpenseSource x when not (books.Expenses.ContainsKey x) -> violation "sources-exist" subject $"expense {x} is missing"
          | FixedFeeSource e when not (books.Engagements.ContainsKey e) -> violation "sources-exist" subject $"engagement {e} is missing"
          | MilestoneSource(e, m) when
              not (books.Engagements.TryFind e |> Option.exists (fun found -> found.Milestones |> List.exists (fun x -> x.Id = m)))
              ->
              violation "sources-exist" subject $"milestone {e}/{m} is missing"
          | _ -> () ]

    [ for KeyValue(id, i) in books.Invoices do
          for l in i.Lines do
              yield! sourceProblems id l
      for KeyValue(id, p) in books.Proposals do
          for l in p.Lines do
              yield! sourceProblems id l.Line

          match p.State with
          | Accepted invoice when not (books.Invoices.ContainsKey invoice) -> violation "accepted-proposal-has-invoice" id $"invoice {invoice} is missing"
          | _ -> ()
      // INV-CHR-003: nothing contributes to more than one live invoice.
      for key, uses in
          live
          |> Map.toList
          |> List.collect (fun (id, i) -> i.Lines |> List.collect (Summa.Ledger.Billing.sourceKeys books) |> List.distinct |> List.map (fun k -> k, id))
          |> List.groupBy fst do
          if uses.Length > 1 then
              violation "no-double-billing" key $"""billed on {String.Join(", ", uses |> List.map snd)}"""
      // INV-CHR-004: a source is reserved by one live proposal at most.
      for key, uses in
          liveProposals
          |> Map.toList
          |> List.collect (fun (id, p) -> p.Lines |> List.collect (fun l -> Summa.Ledger.Billing.sourceKeys books l.Line) |> List.distinct |> List.map (fun k -> k, id))
          |> List.groupBy fst do
          if uses.Length > 1 then
              violation "one-reservation-per-source" key $"""reserved by {String.Join(", ", uses |> List.map snd)}"""
      for KeyValue(id, x) in books.Expenses do
          if not (books.Ledger.Entries.ContainsKey x.JournalEntryId) then violation "expense-has-entry" id $"its journal entry {x.JournalEntryId} is missing"
      for KeyValue(id, w) in books.Withdrawals do
          if not (books.Time.ContainsKey w.PublicationId) then violation "withdrawal-references" id "the withdrawn time was never imported"
      for KeyValue(id, v) in books.Reviews do
          if not (books.Time.ContainsKey v.PublicationId) then violation "review-references" id $"time {v.PublicationId} was never imported"
          if not (books.Invoices.ContainsKey v.InvoiceId) then violation "review-references" id $"invoice {v.InvoiceId} is missing" ]

let private artifactRules (r: Receivables) =
    [ for KeyValue(id, a) in r.Books.Artifacts do
          if not (r.Books.Invoices.ContainsKey a.InvoiceId) then
              violation "artifacts-reference-invoices" id $"invoice {a.InvoiceId} is missing"
      // INV-DOC-006: an issued document always reproduces to what was issued.
      for invoiceId in r.Books.Artifacts |> Map.toList |> List.map (fun (_, a) -> a.InvoiceId) |> List.distinct do
          for kind, check in Summa.Ledger.Issuance.verify r invoiceId do
              match check with
              | Summa.Ledger.Issuance.Altered(expected, actual) ->
                  violation "documents-reproduce" invoiceId $"%A{kind} hashes to {actual}, not {expected}"
              | _ -> () ]

/// Every invariant the books break, in a stable order; empty when they hold.
let check (r: Receivables) : Violation list =
    entryRules r.Books.Ledger @ invoiceRules r @ paymentRules r @ creditRules r @ voidRules r @ billingRules r @ artifactRules r
