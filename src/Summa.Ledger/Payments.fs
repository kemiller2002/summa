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

/// The unallocated part of a payment, recognised as the customer's credit
/// rather than revenue (v0.2 §4): Debit Cash, Credit Customer Credits.
type CustomerCredit =
    { Id: string
      CustomerId: string
      SourcePaymentId: string
      Amount: Money
      Date: DateOnly
      JournalEntryId: string }

/// Money received before it is earned (v0.2 §5): Debit Cash, Credit
/// Customer Deposits. Distinguishable from invoice payments.
type Deposit =
    { Id: string
      CustomerId: string
      DateReceived: DateOnly
      Amount: Money
      Method: PaymentMethod
      Reference: string
      JournalEntryId: string }

/// An approved credit with its own identifier (v0.2 §9): Debit the revenue
/// it reduces, Credit Customer Credits, until applied.
type CreditMemo =
    { Id: string
      CustomerId: string
      /// The invoice it was raised against, if any.
      InvoiceId: string option
      Amount: Money
      RevenueAccountId: string
      Reason: string
      IssueDate: DateOnly
      JournalEntryId: string }

/// Where an application or a refund takes its money from.
type CreditSource =
    | FromCredit of creditId: string
    | FromDeposit of depositId: string
    | FromCreditMemo of memoId: string

/// Part of a credit, deposit or credit memo applied to an invoice: Debit the
/// liability it came from, Credit Accounts Receivable.
type Application =
    { Id: string
      Source: CreditSource
      InvoiceId: string
      Amount: Money
      Date: DateOnly
      JournalEntryId: string }

/// Money that actually leaves the business (v0.2 §10): Debit the liability
/// it came from, Credit Cash. It never deletes the original payment.
type Refund =
    { Id: string
      CustomerId: string
      Source: CreditSource
      Amount: Money
      Date: DateOnly
      Method: PaymentMethod
      Reference: string
      JournalEntryId: string }

/// A payment that failed after it was recorded (v0.2 §11): the payment stays
/// in history, its effects are reversed by compensating entries.
type PaymentReversal =
    { PaymentId: string
      Reason: string
      Date: DateOnly
      /// The reversing entries, one per allocation or credit of the payment.
      JournalEntryIds: string list }

/// An explicit write-off of an uncollectible balance (v0.2 §12): Debit Bad
/// Debt Expense, Credit Accounts Receivable.
type WriteOff =
    { Id: string
      InvoiceId: string
      Amount: Money
      Reason: string
      Date: DateOnly
      JournalEntryId: string }

/// An issued invoice that was voided (v0.2 §8): the invoice stays, its
/// journal entry is reversed by `JournalEntryId` and its obligation is
/// cancelled. Keyed by the invoice.
type InvoiceVoid =
    { InvoiceId: string
      Reason: string
      Date: DateOnly
      JournalEntryId: string }

type Receivables =
    { Books: Books
      Payments: Map<string, Payment>
      Allocations: Allocation list
      Credits: Map<string, CustomerCredit>
      Deposits: Map<string, Deposit>
      CreditMemos: Map<string, CreditMemo>
      Applications: Application list
      Refunds: Map<string, Refund>
      Reversals: Map<string, PaymentReversal>
      WriteOffs: Map<string, WriteOff>
      Voids: Map<string, InvoiceVoid> }

let start (books: Books) =
    { Books = books
      Payments = Map.empty
      Allocations = []
      Credits = Map.empty
      Deposits = Map.empty
      CreditMemos = Map.empty
      Applications = []
      Refunds = Map.empty
      Reversals = Map.empty
      WriteOffs = Map.empty
      Voids = Map.empty }

/// Allocations that still count: those of payments that were not reversed.
let liveAllocations (r: Receivables) =
    r.Allocations |> List.filter (fun a -> not (r.Reversals.ContainsKey a.PaymentId))

let private allocatedTo (r: Receivables) (pick: Allocation -> bool) (currency: string) =
    liveAllocations r |> List.filter pick |> List.map _.Amount |> sum currency

/// What has been paid against an invoice: live allocations and applied
/// credits, deposits and credit memos.
let amountPaid (r: Receivables) (invoice: IssuedInvoice) =
    add
        (allocatedTo r (fun a -> a.InvoiceId = invoice.InvoiceId) invoice.Currency)
        (r.Applications |> List.filter (fun a -> a.InvoiceId = invoice.InvoiceId) |> List.map _.Amount |> sum invoice.Currency)

/// What has been written off against an invoice.
let writtenOff (r: Receivables) (invoice: IssuedInvoice) =
    r.WriteOffs |> Map.toList |> List.map snd |> List.filter (fun w -> w.InvoiceId = invoice.InvoiceId) |> List.map _.Amount |> sum invoice.Currency

/// What an invoice still owes; nothing once it is voided.
let outstanding (r: Receivables) (invoice: IssuedInvoice) =
    if r.Voids.ContainsKey invoice.InvoiceId then
        zero invoice.Currency
    else
        subtract (subtract invoice.Total (amountPaid r invoice)) (writtenOff r invoice)

/// What of a payment is neither allocated nor recognised as a credit. A
/// reversed payment has nothing left to allocate.
let unallocated (r: Receivables) (payment: Payment) =
    if r.Reversals.ContainsKey payment.Id then
        zero payment.Amount.Currency
    else
        let credited =
            r.Credits |> Map.toList |> List.map snd |> List.filter (fun c -> c.SourcePaymentId = payment.Id) |> List.map _.Amount |> sum payment.Amount.Currency

        subtract (subtract payment.Amount (allocatedTo r (fun a -> a.PaymentId = payment.Id) payment.Amount.Currency)) credited

type InvoiceStatus =
    | Issued
    | PartiallyPaid
    | Paid
    /// Nothing is outstanding because the rest was written off (v0.2 §12).
    | WrittenOff
    /// Voided: it stays visible, but owes nothing (v0.2 §8).
    | Voided

/// Paid only when nothing is outstanding (§13).
let status (r: Receivables) (invoice: IssuedInvoice) =
    let paid = amountPaid r invoice

    if r.Voids.ContainsKey invoice.InvoiceId then
        Voided
    elif (outstanding r invoice).Minor = 0L then
        if (writtenOff r invoice).Minor > 0L then WrittenOff else Paid
    elif paid.Minor > 0L then
        PartiallyPaid
    else
        Issued

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
        | Paid
        | WrittenOff -> Settled
        | PartiallyPaid -> PartiallySettled
        | Issued -> Open
        | Voided -> Cancelled

/// Overdue is derived, never set (v0.2 §19): something is still outstanding
/// and the date is past the due date.
let isOverdue (today: DateOnly) (r: Receivables) (invoice: IssuedInvoice) =
    (outstanding r invoice).Minor > 0L && today > invoice.DueDate

/// The date an invoice became paid: the latest payment or application that
/// counts towards it, once nothing is outstanding by payment. Derived.
let paidAt (r: Receivables) (invoice: IssuedInvoice) : DateOnly option =
    match status r invoice with
    | Paid ->
        let payments =
            liveAllocations r
            |> List.filter (fun a -> a.InvoiceId = invoice.InvoiceId)
            |> List.choose (fun a -> r.Payments.TryFind a.PaymentId |> Option.map _.DateReceived)

        let applications = r.Applications |> List.filter (fun a -> a.InvoiceId = invoice.InvoiceId) |> List.map _.Date
        payments @ applications |> List.sort |> List.tryLast
    | _ -> None

/// The dates of one invoice, kept apart because they mean different things
/// (v0.2 §27). Business dates are `DateOnly`; the moments Summa recorded
/// something are `DateTimeOffset`.
type InvoiceDates =
    { /// When its draft was first saved.
      CreatedAt: DateTimeOffset option
      /// When Summa issued it.
      IssuedAt: DateTimeOffset
      /// The date it counts in the books (its issue date).
      AccountingDate: DateOnly
      DueDate: DateOnly
      /// When its journal entry was posted.
      PostedAt: DateTimeOffset option
      /// The business date it became paid.
      PaidAt: DateOnly option }

let dates (r: Receivables) (invoice: IssuedInvoice) =
    let draftId = r.Books.IssuedFrom |> Map.tryFindKey (fun _ id -> id = invoice.InvoiceId)

    { CreatedAt =
        draftId
        |> Option.bind (fun d -> r.Books.Ledger.Audit |> List.tryFind (fun a -> a.What = "invoice-created" && a.Subject = d))
        |> Option.map _.When
      IssuedAt = invoice.IssuedAt
      AccountingDate = invoice.IssueDate
      DueDate = invoice.DueDate
      PostedAt = r.Books.Ledger.Entries.TryFind invoice.JournalEntryId |> Option.map _.PostedAt
      PaidAt = paidAt r invoice }

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
