/// Correcting invoices (v0.2 §6-8).
///
/// Before issue a draft is edited freely and nothing is posted (§6). After
/// issue the invoice is never edited (§7). A material change is made in one
/// of three ways, and the original invoice always stays visible:
/// - Void and reissue: void the original, then issue a new draft that names it.
/// - Credit adjustment: a credit memo against the invoice (`Credits`).
/// - Correcting invoice: a further invoice that names the one it corrects.
///
/// A void reverses the invoice's journal entry, cancels its receivable and
/// marks it Voided. It never deletes anything (§8).
module Summa.Ledger.Corrections

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

type CorrectionProblem =
    | UnknownInvoiceToCorrect of string
    | VoidReasonRequired
    /// Payments, credits or write-offs count against it: reverse or
    /// correct them by credit memo first.
    | HasSettlements of settled: Money
    | AlreadyVoided of invoiceId: string
    | VoidPosting of Problem list
    | DraftRefused of InvoiceProblem list

type VoidRequest =
    { InvoiceId: string
      Reason: string
      Date: DateOnly
      /// The id of the entry that reverses the invoice's entry.
      JournalEntryId: string }

/// Voids an issued invoice: Debit Revenue, Credit AR by reversing its entry,
/// and cancels its obligation. Idempotent: the same void again changes
/// nothing; a different void of a voided invoice is refused.
let voidInvoice (context: Context) (request: VoidRequest) (r: Receivables) : Result<Receivables, CorrectionProblem list> =
    let wanted: InvoiceVoid =
        { InvoiceId = request.InvoiceId
          Reason = request.Reason
          Date = request.Date
          JournalEntryId = request.JournalEntryId }

    match r.Voids.TryFind request.InvoiceId, r.Books.Invoices.TryFind request.InvoiceId with
    | Some existing, _ when existing = wanted -> Ok r
    | Some _, _ -> Error [ AlreadyVoided request.InvoiceId ]
    | None, None -> Error [ UnknownInvoiceToCorrect request.InvoiceId ]
    | None, Some invoice ->
        let settled = add (amountPaid r invoice) (writtenOff r invoice)

        let problems =
            [ if String.IsNullOrWhiteSpace request.Reason then VoidReasonRequired
              if settled.Minor > 0L then HasSettlements settled ]

        if not problems.IsEmpty then
            Error problems
        else
            match reverse context invoice.JournalEntryId request.JournalEntryId request.Date r.Books.Ledger with
            | Error problems -> Error [ VoidPosting problems ]
            | Ok(ledger, _) ->
                let obligations =
                    r.Books.Obligations
                    |> Map.change invoice.ObligationId (Option.map (fun o -> { o with Cancelled = true }))

                Ok
                    { r with
                        Books =
                            { r.Books with
                                Ledger = audit context "invoice-voided" invoice.InvoiceId ledger
                                Obligations = obligations }
                        Voids = r.Voids.Add(invoice.InvoiceId, wanted) }

/// A new draft that corrects an issued invoice: same customer and currency,
/// no lines yet, naming the invoice it corrects. Pure.
let correctingDraft (draftId: string) (invoice: IssuedInvoice) : DraftInvoice =
    { DraftId = draftId
      CustomerId = invoice.CustomerId
      Currency = invoice.Currency
      Lines = []
      Adjustments = []
      Discounts = []
      Terms = None
      DueDate = None
      Corrects = Some invoice.InvoiceId }

/// A draft copied from an issued invoice, to be changed and issued in its
/// place. Terms the invoice set itself are kept; inherited terms are
/// resolved again when the new draft is issued. Pure.
let reissueDraft (draftId: string) (invoice: IssuedInvoice) : DraftInvoice =
    { correctingDraft draftId invoice with
        Lines = invoice.Lines
        Adjustments = invoice.Adjustments
        Discounts = invoice.Discounts
        Terms = (if invoice.TermsSource = InvoiceTerms then Some invoice.Terms else None) }

/// Void and reissue: voids the invoice and saves a draft copied from it, to
/// be corrected and issued. Both or neither.
let voidAndReissue (context: Context) (request: VoidRequest) (draftId: string) (r: Receivables) =
    voidInvoice context request r
    |> Result.bind (fun voided ->
        let invoice = voided.Books.Invoices[request.InvoiceId]

        match voided.Books.Drafts.TryFind draftId with
        | Some existing when existing.Corrects = Some invoice.InvoiceId -> Ok(voided, existing)
        | _ ->
            let draft = reissueDraft draftId invoice

            saveDraft context draft voided.Books
            |> Result.mapError (DraftRefused >> List.singleton)
            |> Result.map (fun books -> { voided with Books = books }, draft))
