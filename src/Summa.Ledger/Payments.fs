/// Payments, allocation and the states derived from them (v0.1 §10-14,
/// §19).
///
/// A payment is recorded once (idempotent by payment id). Allocating part
/// of it to an invoice posts Cash/AR for exactly the allocated amount, once
/// per allocation id. What an invoice has been paid, what it still owes and
/// whether it is Paid are calculated from allocations, never edited (§13).
module Summa.Ledger.Payments

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing

type PaymentMethod =
    | Ach
    | Check
    | Wire
    | CreditCard
    | Cash
    | Other

type Payment =
    { Id: string
      CustomerId: string
      DateReceived: DateOnly
      Amount: Money
      Method: PaymentMethod
      Reference: string
      Memo: string option }

type Allocation =
    { Id: string
      PaymentId: string
      InvoiceId: string
      Amount: Money
      JournalEntryId: string }

type PaymentProblem =
    | PaymentNotPositive
    | UnknownPaymentCustomer of string
    | PaymentIdReused of string
    | UnknownPayment of string
    | UnknownInvoiceToAllocate of string
    | CustomerMismatch
    | AllocationNotPositive
    | ExceedsUnallocated of available: Money
    | ExceedsOutstanding of outstanding: Money
    | AllocationLedgerProblems of Problem list

type Receivables =
    { Books: Books
      Payments: Map<string, Payment>
      Allocations: Allocation list }

let start (books: Books) =
    { Books = books
      Payments = Map.empty
      Allocations = [] }

let private allocatedTo (r: Receivables) (pick: Allocation -> bool) (currency: string) =
    r.Allocations |> List.filter pick |> List.map _.Amount |> sum currency

let amountPaid (r: Receivables) (invoice: IssuedInvoice) =
    allocatedTo r (fun a -> a.InvoiceId = invoice.InvoiceId) invoice.Currency

let outstanding (r: Receivables) (invoice: IssuedInvoice) = subtract invoice.Total (amountPaid r invoice)

let unallocated (r: Receivables) (payment: Payment) =
    subtract payment.Amount (allocatedTo r (fun a -> a.PaymentId = payment.Id) payment.Amount.Currency)

type InvoiceStatus =
    | Issued
    | PartiallyPaid
    | Paid

/// Paid only when nothing is outstanding (§13).
let status (r: Receivables) (invoice: IssuedInvoice) =
    let paid = amountPaid r invoice
    if (outstanding r invoice).Minor = 0L then Paid elif paid.Minor > 0L then PartiallyPaid else Issued

type Timing =
    | Current
    | DueSoon
    | Overdue

/// Orthogonal to status: an issued invoice may also be overdue (§4.1).
let timing (today: DateOnly) (due: DateOnly) (outstandingAmount: Money) =
    if outstandingAmount.Minor <= 0L then Current
    elif today > due then Overdue
    elif due.DayNumber - today.DayNumber <= 7 then DueSoon
    else Current

type ObligationStatus =
    | Open
    | PartiallySettled
    | Settled
    | Cancelled

let obligationStatus (r: Receivables) (obligation: Obligation) =
    if obligation.Cancelled then
        Cancelled
    else
        let invoice = r.Books.Invoices |> Map.toList |> List.map snd |> List.find (fun i -> i.ObligationId = obligation.Id)

        match status r invoice with
        | Paid -> Settled
        | PartiallyPaid -> PartiallySettled
        | Issued -> Open

/// Records a payment. Retrying the same payment is a no-op; reusing its id
/// for a different payment is refused.
let recordPayment (context: Context) (payment: Payment) (r: Receivables) =
    match r.Payments.TryFind payment.Id with
    | Some existing when existing = payment -> Ok r
    | Some _ -> Error [ PaymentIdReused payment.Id ]
    | None ->
        let problems =
            [ if not (isPositive payment.Amount) then PaymentNotPositive
              if not (r.Books.Customers.ContainsKey payment.CustomerId) then UnknownPaymentCustomer payment.CustomerId ]

        if not problems.IsEmpty then
            Error problems
        else
            Ok
                { r with
                    Payments = r.Payments.Add(payment.Id, payment)
                    Books = { r.Books with Ledger = audit context "payment-recorded" payment.Id r.Books.Ledger } }

type AllocationRequest =
    { AllocationId: string
      PaymentId: string
      InvoiceId: string
      Amount: Money
      JournalEntryId: string
      CashAccountId: string
      ReceivableAccountId: string }

/// Applies part of a payment to an invoice and posts Cash/AR for exactly
/// that amount (§12, §13). Idempotent by allocation id.
let allocate (context: Context) (request: AllocationRequest) (r: Receivables) =
    match r.Allocations |> List.tryFind (fun a -> a.Id = request.AllocationId) with
    | Some _ -> Ok r
    | None ->
        match r.Payments.TryFind request.PaymentId, r.Books.Invoices.TryFind request.InvoiceId with
        | None, _ -> Error [ UnknownPayment request.PaymentId ]
        | _, None -> Error [ UnknownInvoiceToAllocate request.InvoiceId ]
        | Some payment, Some invoice ->
            let available = unallocated r payment
            let owed = outstanding r invoice

            let problems =
                [ if payment.CustomerId <> invoice.CustomerId then CustomerMismatch
                  if not (isPositive request.Amount) then AllocationNotPositive
                  elif request.Amount > available then ExceedsUnallocated available
                  elif request.Amount > owed then ExceedsOutstanding owed ]

            if not problems.IsEmpty then
                Error problems
            else
                let dims = { noDimensions with Client = Some invoice.CustomerId }

                let entry =
                    { Date = payment.DateReceived
                      Description = $"Payment {payment.Id} applied to {invoice.Number}"
                      Lines =
                        [ { AccountId = request.CashAccountId; Side = Debit request.Amount; Memo = Some payment.Reference; Dimensions = dims }
                          { AccountId = request.ReceivableAccountId; Side = Credit request.Amount; Memo = Some invoice.Number; Dimensions = dims } ]
                      Source = $"payment:{payment.Id}" }

                match post context $"allocate:{request.AllocationId}" request.JournalEntryId entry r.Books.Ledger with
                | Error problems -> Error [ AllocationLedgerProblems problems ]
                | Ok(ledger, posted) ->
                    Ok
                        { r with
                            Allocations =
                                r.Allocations
                                @ [ { Id = request.AllocationId
                                      PaymentId = payment.Id
                                      InvoiceId = invoice.InvoiceId
                                      Amount = request.Amount
                                      JournalEntryId = posted.Id } ]
                            Books = { r.Books with Ledger = audit context "payment-allocated" request.AllocationId ledger } }

// ---------------------------------------------------------------------------
// AR aging (§14).
// ---------------------------------------------------------------------------

type AgingBucket =
    | NotYetDue
    | Days1To30
    | Days31To60
    | Days61To90
    | Over90

let bucket (today: DateOnly) (due: DateOnly) =
    match daysPastDue today due with
    | 0 -> NotYetDue
    | d when d <= 30 -> Days1To30
    | d when d <= 60 -> Days31To60
    | d when d <= 90 -> Days61To90
    | _ -> Over90

type AgingRow =
    { CustomerId: string
      Current: Money
      Days1To30: Money
      Days31To60: Money
      Days61To90: Money
      Over90: Money
      Total: Money
      /// Drill-down: the invoices behind the row, with what each still owes.
      Invoices: (string * AgingBucket * Money) list }

/// Aging by outstanding amount, never the original total (v0.2 §20).
let aging (currency: string) (today: DateOnly) (r: Receivables) =
    r.Books.Invoices
    |> Map.toList
    |> List.map snd
    |> List.choose (fun i ->
        let owed = outstanding r i
        if owed.Minor > 0L then Some(i, bucket today i.DueDate, owed) else None)
    |> List.groupBy (fun (i, _, _) -> i.CustomerId)
    |> List.sortBy fst
    |> List.map (fun (customer, items) ->
        let inBucket b = items |> List.filter (fun (_, x, _) -> x = b) |> List.map (fun (_, _, m) -> m) |> sum currency

        { CustomerId = customer
          Current = inBucket NotYetDue
          Days1To30 = inBucket Days1To30
          Days31To60 = inBucket Days31To60
          Days61To90 = inBucket Days61To90
          Over90 = inBucket Over90
          Total = items |> List.map (fun (_, _, m) -> m) |> sum currency
          Invoices = items |> List.map (fun (i, b, m) -> i.Number, b, m) |> List.sortBy (fun (n, _, _) -> n) })
