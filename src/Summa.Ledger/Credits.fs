/// Receivables beyond the simple payment (v0.2 §3-5, §9-12, §25): one
/// payment across several invoices, overpayments kept as customer credit,
/// deposits, credit memos and their application, refunds, bounced
/// payments and write-offs. Every one is explicit, posts deterministic
/// entries through the ledger, is idempotent by its id, and leaves the
/// original records in place: corrections compensate, never rewrite.
module Summa.Ledger.Credits

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// The accounts these operations post to, from the organization's settings.
type ReceivableAccounts =
    { Cash: string
      Receivable: string
      /// Liability: customer credits from overpayments and credit memos.
      CustomerCredits: string
      /// Liability: customer deposits and retainers.
      CustomerDeposits: string
      /// Expense: bad debts written off.
      BadDebt: string }

type CreditProblem =
    | UnknownCustomerFor of string
    | UnknownPaymentFor of string
    | UnknownInvoiceFor of string
    | UnknownSource of CreditSource
    | NotPositive
    | NothingUnallocated of paymentId: string
    | ExceedsRemaining of available: Money
    | ExceedsOutstandingBalance of outstanding: Money
    | WrongCustomer
    | AlreadyReversed of paymentId: string
    /// A credit from this payment was already applied or refunded; undo that first.
    | CreditInUse of creditId: string
    | ReasonRequired
    | InvalidCreditLines of string
    | NotARevenueAccount of string
    | IdReused of string
    | Posting of Problem list

let private line account side memo customer =
    { AccountId = account
      Side = side
      Memo = memo
      Dimensions = { noDimensions with Client = Some customer } }

/// Posts a two-line entry under an idempotency key.
let private postPair (context: Context) key entryId date description source (debit: string) (credit: string) (amount: Money) customer memo (r: Receivables) =
    let draft =
        { Date = date
          Description = description
          Lines = [ line debit (Debit amount) memo customer; line credit (Credit amount) memo customer ]
          Source = source }

    post context key entryId draft r.Books.Ledger
    |> Result.mapError (Posting >> List.singleton)
    |> Result.map (fun (ledger, _) -> { r with Books = { r.Books with Ledger = ledger } })

let private audited (context: Context) what subject (r: Receivables) =
    { r with Books = { r.Books with Ledger = audit context what subject r.Books.Ledger } }

// ---- One payment across several invoices (§3) ------------------------------------

/// Allocates one payment across several invoices: all or none. The sum may
/// be less than the payment; what remains stays visible as unallocated.
let allocateAcross (context: Context) (requests: AllocationRequest list) (r: Receivables) =
    requests |> List.fold (fun state request -> state |> Result.bind (allocate context request)) (Ok r)

// ---- Overpayments become customer credit (§4) ----------------------------------

type CreditRequest = { CreditId: string; PaymentId: string; Date: DateOnly; JournalEntryId: string }

/// Recognises a payment's unallocated remainder as the customer's credit,
/// never as revenue. Idempotent by credit id.
let creditUnapplied (context: Context) (accounts: ReceivableAccounts) (request: CreditRequest) (r: Receivables) =
    match r.Credits.TryFind request.CreditId, r.Payments.TryFind request.PaymentId with
    | Some existing, _ when existing.SourcePaymentId = request.PaymentId -> Ok r
    | Some _, _ -> Error [ IdReused request.CreditId ]
    | None, None -> Error [ UnknownPaymentFor request.PaymentId ]
    | None, Some _ when r.Reversals.ContainsKey request.PaymentId -> Error [ AlreadyReversed request.PaymentId ]
    | None, Some payment ->
        let remaining = unallocated r payment

        if not (isPositive remaining) then
            Error [ NothingUnallocated payment.Id ]
        else
            let credit =
                { Id = request.CreditId
                  CustomerId = payment.CustomerId
                  SourcePaymentId = payment.Id
                  Amount = remaining
                  Date = request.Date
                  JournalEntryId = request.JournalEntryId }

            r
            |> postPair context $"credit:{credit.Id}" request.JournalEntryId request.Date $"Customer credit from {payment.Id}" $"credit:{credit.Id}" accounts.Cash accounts.CustomerCredits remaining payment.CustomerId (Some payment.Reference)
            |> Result.map (fun next -> { next with Credits = next.Credits.Add(credit.Id, credit) } |> audited context "customer-credit-recognised" credit.Id)

// ---- Deposits (§5) ----------------------------------------------------------------

/// Records a deposit or retainer: cash received, owed back as work or
/// applied to a later invoice, never revenue on receipt.
let recordDeposit (context: Context) (accounts: ReceivableAccounts) (deposit: Deposit) (r: Receivables) =
    match r.Deposits.TryFind deposit.Id with
    | Some existing when existing = deposit -> Ok r
    | Some _ -> Error [ IdReused deposit.Id ]
    | None ->
        match [ if not (isPositive deposit.Amount) then NotPositive
                if not (r.Books.Customers.ContainsKey deposit.CustomerId) then UnknownCustomerFor deposit.CustomerId ] with
        | _ :: _ as problems -> Error problems
        | [] ->
            r
            |> postPair context $"deposit:{deposit.Id}" deposit.JournalEntryId deposit.DateReceived $"Deposit {deposit.Id}" $"deposit:{deposit.Id}" accounts.Cash accounts.CustomerDeposits deposit.Amount deposit.CustomerId (Some deposit.Reference)
            |> Result.map (fun next -> { next with Deposits = next.Deposits.Add(deposit.Id, deposit) } |> audited context "deposit-recorded" deposit.Id)

// ---- Credit memos (§9) ----------------------------------------------------------------

/// Issues a credit memo: it reduces revenue and is owed to the customer
/// until applied to an invoice or refunded.
let issueCreditMemo (context: Context) (accounts: ReceivableAccounts) (memo: CreditMemo) (r: Receivables) =
    match r.CreditMemos.TryFind memo.Id with
    | Some existing when existing = memo -> Ok r
    | Some _ -> Error [ IdReused memo.Id ]
    | None ->
        let problems =
            [ if not (isPositive memo.Amount) then NotPositive
              if String.IsNullOrWhiteSpace memo.Reason then ReasonRequired
              if not (r.Books.Customers.ContainsKey memo.CustomerId) then UnknownCustomerFor memo.CustomerId
              match memo.InvoiceId |> Option.map (fun id -> id, r.Books.Invoices.TryFind id) with
              | Some(id, None) -> UnknownInvoiceFor id
              | Some(_, Some invoice) when invoice.CustomerId <> memo.CustomerId -> WrongCustomer
              | Some(_, Some invoice) when not memo.Lines.IsEmpty ->
                  // A partial credit names the lines it credits and never
                  // credits more than they charged (INV-COR-009).
                  if memo.Lines |> List.exists (fun i -> i < 0 || i >= invoice.Lines.Length) || memo.Lines |> List.distinct |> List.length <> memo.Lines.Length then
                      InvalidCreditLines "a credited line does not exist or is named twice"
                  elif memo.Amount > (memo.Lines |> List.map (fun i -> lineAmount invoice.Lines[i]) |> sum invoice.Currency) then
                      InvalidCreditLines "the credit is more than the credited lines charged"
              | None when not memo.Lines.IsEmpty -> InvalidCreditLines "credited lines need the invoice they belong to"
              | _ -> ()
              match r.Books.Ledger.Accounts.TryFind memo.RevenueAccountId with
              | Some a when a.Type = Revenue -> ()
              | _ -> NotARevenueAccount memo.RevenueAccountId ]

        if not problems.IsEmpty then
            Error problems
        else
            r
            |> postPair context $"credit-memo:{memo.Id}" memo.JournalEntryId memo.IssueDate $"Credit memo {memo.Id}: {memo.Reason}" $"credit-memo:{memo.Id}" memo.RevenueAccountId accounts.CustomerCredits memo.Amount memo.CustomerId (Some memo.Reason)
            |> Result.map (fun next -> { next with CreditMemos = next.CreditMemos.Add(memo.Id, memo) } |> audited context "credit-memo-issued" memo.Id)

// ---- What a credit, deposit or memo still holds -------------------------------------

let private used (r: Receivables) (source: CreditSource) (currency: string) =
    add
        (r.Applications |> List.filter (fun a -> a.Source = source) |> List.map _.Amount |> sum currency)
        (r.Refunds |> Map.toList |> List.map snd |> List.filter (fun x -> x.Source = source) |> List.map _.Amount |> sum currency)

/// The source's customer, original amount and liability account.
let private sourceOf (accounts: ReceivableAccounts) (r: Receivables) (source: CreditSource) =
    match source with
    | FromCredit id -> r.Credits.TryFind id |> Option.map (fun c -> c.CustomerId, c.Amount, accounts.CustomerCredits)
    | FromDeposit id -> r.Deposits.TryFind id |> Option.map (fun d -> d.CustomerId, d.Amount, accounts.CustomerDeposits)
    | FromCreditMemo id -> r.CreditMemos.TryFind id |> Option.map (fun m -> m.CustomerId, m.Amount, accounts.CustomerCredits)

/// What a credit, deposit or credit memo still holds.
let remaining (r: Receivables) (source: CreditSource) : Money option =
    let original =
        match source with
        | FromCredit id -> r.Credits.TryFind id |> Option.map _.Amount
        | FromDeposit id -> r.Deposits.TryFind id |> Option.map _.Amount
        | FromCreditMemo id -> r.CreditMemos.TryFind id |> Option.map _.Amount

    original |> Option.map (fun amount -> subtract amount (used r source amount.Currency))

/// A customer's credits and deposits still available, by source.
let available (r: Receivables) (customerId: string) =
    [ for KeyValue(id, c) in r.Credits do
          if c.CustomerId = customerId then FromCredit id
      for KeyValue(id, d) in r.Deposits do
          if d.CustomerId = customerId then FromDeposit id
      for KeyValue(id, m) in r.CreditMemos do
          if m.CustomerId = customerId then FromCreditMemo id ]
    |> List.choose (fun source -> remaining r source |> Option.filter isPositive |> Option.map (fun m -> source, m))

// ---- Applying credits, deposits and memos to invoices -----------------------------------

type ApplicationRequest =
    { ApplicationId: string
      Source: CreditSource
      InvoiceId: string
      Amount: Money
      Date: DateOnly
      JournalEntryId: string }

/// Applies part of a credit, deposit or credit memo to one of the same
/// customer's invoices. Idempotent by application id.
let apply (context: Context) (accounts: ReceivableAccounts) (request: ApplicationRequest) (r: Receivables) =
    match r.Applications |> List.tryFind (fun a -> a.Id = request.ApplicationId) with
    | Some _ -> Ok r
    | None ->
        match sourceOf accounts r request.Source, r.Books.Invoices.TryFind request.InvoiceId with
        | None, _ -> Error [ UnknownSource request.Source ]
        | _, None -> Error [ UnknownInvoiceFor request.InvoiceId ]
        | Some(customer, _, liability), Some invoice ->
            let left = remaining r request.Source |> Option.defaultValue (zero invoice.Currency)
            let owed = outstanding r invoice

            let problems =
                [ if customer <> invoice.CustomerId then WrongCustomer
                  if not (isPositive request.Amount) then NotPositive
                  elif request.Amount > left then ExceedsRemaining left
                  elif request.Amount > owed then ExceedsOutstandingBalance owed ]

            if not problems.IsEmpty then
                Error problems
            else
                let application =
                    { Id = request.ApplicationId
                      Source = request.Source
                      InvoiceId = invoice.InvoiceId
                      Amount = request.Amount
                      Date = request.Date
                      JournalEntryId = request.JournalEntryId }

                r
                |> postPair context $"apply:{application.Id}" request.JournalEntryId request.Date $"Applied to {invoice.Number}" $"application:{application.Id}" liability accounts.Receivable request.Amount customer (Some invoice.Number)
                |> Result.map (fun next -> { next with Applications = next.Applications @ [ application ] } |> audited context "credit-applied" application.Id)

// ---- Refunds (§10) ----------------------------------------------------------------------

/// Pays money back from a credit, deposit or credit memo. The original
/// payment is never touched.
let refund (context: Context) (accounts: ReceivableAccounts) (refund: Refund) (r: Receivables) =
    match r.Refunds.TryFind refund.Id with
    | Some existing when existing = refund -> Ok r
    | Some _ -> Error [ IdReused refund.Id ]
    | None ->
        match sourceOf accounts r refund.Source with
        | None -> Error [ UnknownSource refund.Source ]
        | Some(customer, _, liability) ->
            let left = remaining r refund.Source |> Option.defaultValue (zero refund.Amount.Currency)

            let problems =
                [ if customer <> refund.CustomerId then WrongCustomer
                  if not (isPositive refund.Amount) then NotPositive
                  elif refund.Amount > left then ExceedsRemaining left ]

            if not problems.IsEmpty then
                Error problems
            else
                r
                |> postPair context $"refund:{refund.Id}" refund.JournalEntryId refund.Date $"Refund {refund.Id}" $"refund:{refund.Id}" liability accounts.Cash refund.Amount customer (Some refund.Reference)
                |> Result.map (fun next -> { next with Refunds = next.Refunds.Add(refund.Id, refund) } |> audited context "refund-paid" refund.Id)

// ---- Bounced or reversed payments (§11) -------------------------------------------------

type ReversalRequest =
    { PaymentId: string
      Reason: string
      Date: DateOnly
      /// Prefix for the reversing entries' ids: `<prefix>-1`, `<prefix>-2`...
      JournalEntryPrefix: string }

/// Reverses a bounced, returned or charged-back payment. The payment stays in
/// history; each of its allocations and credits is reversed by a
/// compensating entry, so the invoices it paid are open again. A credit from
/// it that was already applied or refunded must be undone first.
let reversePayment (context: Context) (accounts: ReceivableAccounts) (request: ReversalRequest) (r: Receivables) =
    match r.Payments.TryFind request.PaymentId, r.Reversals.TryFind request.PaymentId with
    | None, _ -> Error [ UnknownPaymentFor request.PaymentId ]
    | Some _, Some existing when existing.Reason = request.Reason && existing.Date = request.Date -> Ok r
    | Some _, Some _ -> Error [ AlreadyReversed request.PaymentId ]
    | Some payment, None ->
        let credits = r.Credits |> Map.toList |> List.map snd |> List.filter (fun c -> c.SourcePaymentId = payment.Id)

        let problems =
            [ if String.IsNullOrWhiteSpace request.Reason then ReasonRequired
              for c in credits do
                  if remaining r (FromCredit c.Id) <> Some c.Amount then CreditInUse c.Id ]

        if not problems.IsEmpty then
            Error problems
        else
            let allocations = liveAllocations r |> List.filter (fun a -> a.PaymentId = payment.Id)

            let effects =
                (allocations |> List.map (fun a -> $"reverse-allocation:{a.Id}", accounts.Receivable, accounts.Cash, a.Amount))
                @ (credits |> List.map (fun c -> $"reverse-credit:{c.Id}", accounts.CustomerCredits, accounts.Cash, c.Amount))

            let ids = effects |> List.mapi (fun i _ -> $"{request.JournalEntryPrefix}-{i + 1}")

            List.zip effects ids
            |> List.fold
                (fun state ((key, debit, credit, amount), id) ->
                    state
                    |> Result.bind (postPair context key id request.Date $"Payment {payment.Id} reversed: {request.Reason}" $"payment-reversal:{payment.Id}" debit credit amount payment.CustomerId (Some payment.Reference)))
                (Ok r)
            |> Result.map (fun next ->
                { next with
                    Reversals =
                        next.Reversals.Add(
                            payment.Id,
                            { PaymentId = payment.Id
                              Reason = request.Reason
                              Date = request.Date
                              JournalEntryIds = ids }
                        ) }
                |> audited context "payment-reversed" payment.Id)

// ---- Write-offs (§12) ---------------------------------------------------------------------

/// Writes off part or all of an invoice's outstanding balance, with a
/// reason. The invoice stays; the amount is reportable as bad debt, apart
/// from collected revenue.
let writeOff (context: Context) (accounts: ReceivableAccounts) (w: WriteOff) (r: Receivables) =
    match r.WriteOffs.TryFind w.Id with
    | Some existing when existing = w -> Ok r
    | Some _ -> Error [ IdReused w.Id ]
    | None ->
        match r.Books.Invoices.TryFind w.InvoiceId with
        | None -> Error [ UnknownInvoiceFor w.InvoiceId ]
        | Some invoice ->
            let owed = outstanding r invoice

            let problems =
                [ if String.IsNullOrWhiteSpace w.Reason then ReasonRequired
                  if not (isPositive w.Amount) then NotPositive
                  elif w.Amount > owed then ExceedsOutstandingBalance owed ]

            if not problems.IsEmpty then
                Error problems
            else
                r
                |> postPair context $"write-off:{w.Id}" w.JournalEntryId w.Date $"Write-off of {invoice.Number}: {w.Reason}" $"write-off:{w.Id}" accounts.BadDebt accounts.Receivable w.Amount invoice.CustomerId (Some w.Reason)
                |> Result.map (fun next -> { next with WriteOffs = next.WriteOffs.Add(w.Id, w) } |> audited context "invoice-written-off" w.Id)

/// Bad debt written off, apart from what was collected (v0.2 §12).
let badDebt (currency: string) (r: Receivables) =
    r.WriteOffs |> Map.toList |> List.map snd |> List.filter (fun w -> w.Amount.Currency = currency) |> List.map _.Amount |> sum currency

// ---- Duplicate detection (§25) ---------------------------------------------------------------

/// Receives a payment, protecting against an accidental duplicate: the same
/// payment id again, or a payment with the same customer, external
/// reference, amount and date under another id, returns the existing payment
/// instead of recording a second one.
let receive (context: Context) (payment: Payment) (r: Receivables) : Result<Receivables * Payment, PaymentProblem list> =
    let duplicate =
        r.Payments
        |> Map.toList
        |> List.map snd
        |> List.tryFind (fun p ->
            p.Id <> payment.Id
            && p.CustomerId = payment.CustomerId
            && not (String.IsNullOrWhiteSpace payment.Reference)
            && p.Reference = payment.Reference
            && p.Amount = payment.Amount
            && p.DateReceived = payment.DateReceived)

    match duplicate with
    | Some existing -> Ok(r, existing)
    | None -> recordPayment context payment r |> Result.map (fun next -> next, next.Payments[payment.Id])
