/// Customers, invoices, numbering, issuing, payment terms and receivable
/// obligations (v0.1 §3-6, §8-10, §19).
///
/// Issuing is one explicit transition that produces the issued invoice, its
/// posted AR/revenue journal entry and its receivable obligation together,
/// or none of them (§6.1). It is idempotent by draft id: retrying it five
/// times leaves one invoice, one obligation and one journal entry (§19).
/// Financial states such as Overdue are derived from facts and dates, never
/// stored as editable fields (§13.1).
module Summa.Ledger.Invoicing

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger

type PaymentTerms =
    | DueOnReceipt
    | Net of days: int
    | CustomDate of DateOnly

let dueDate (issueDate: DateOnly) =
    function
    | DueOnReceipt -> issueDate
    | Net days -> issueDate.AddDays days
    | CustomDate date -> date

/// Calculated, never stored (§9).
let daysUntilDue (today: DateOnly) (due: DateOnly) = max 0 (due.DayNumber - today.DayNumber)
let daysPastDue (today: DateOnly) (due: DateOnly) = max 0 (today.DayNumber - due.DayNumber)

type Customer =
    { Id: string
      Name: string
      BillingName: string
      BillingAddress: string
      Email: string
      DefaultTerms: PaymentTerms
      Active: bool }

type InvoiceLine =
    { Description: string
      /// Thousandths, so 7.5 hours is 7500.
      QuantityThousandths: int64
      UnitPrice: Money
      RevenueAccountId: string
      Project: string option
      WorkItem: string option }

let lineAmount (line: InvoiceLine) = extend line.QuantityThousandths line.UnitPrice

/// A draft may be edited freely; it has an internal id, not a number (§5).
type DraftInvoice =
    { DraftId: string
      CustomerId: string
      Currency: string
      Lines: InvoiceLine list
      /// Signed adjustments (a discount is negative).
      Adjustments: Money list
      Terms: PaymentTerms option
      DueDate: DateOnly option }

let subtotal (draft: DraftInvoice) = draft.Lines |> List.map lineAmount |> sum draft.Currency
let total (draft: DraftInvoice) = add (subtotal draft) (sum draft.Currency draft.Adjustments)

type IssuedInvoice =
    { InvoiceId: string
      Number: string
      CustomerId: string
      Currency: string
      IssueDate: DateOnly
      DueDate: DateOnly
      Terms: PaymentTerms
      Lines: InvoiceLine list
      Subtotal: Money
      Adjustments: Money list
      Total: Money
      JournalEntryId: string
      ObligationId: string
      IssuedAt: DateTimeOffset
      SentAt: DateTimeOffset option
      SentTo: string option }

type ObligationKind =
    | Receivable
    | Payable

/// A first-class receivable (§10), shaped so payables fit later. The
/// outstanding amount is derived from allocations (`Payments`), not stored.
type Obligation =
    { Id: string
      Kind: ObligationKind
      Source: string
      Party: string
      OriginalAmount: Money
      DueDate: DateOnly
      Cancelled: bool }

type InvoiceProblem =
    | NoLines
    | UnknownCustomer of string
    | InactiveCustomer of string
    | TotalNotPositive
    | InvalidRevenueAccount of string
    | DuplicateInvoiceNumber of string
    | UnknownDraft of string
    | UnknownInvoice of string
    /// Issued invoices are not drafts any more; correct them by credit memo.
    | AlreadyIssued of invoiceId: string
    | LedgerProblems of Problem list

type Books =
    { Ledger: Ledger
      Customers: Map<string, Customer>
      Drafts: Map<string, DraftInvoice>
      Invoices: Map<string, IssuedInvoice>
      Obligations: Map<string, Obligation>
      /// Draft id -> the invoice it became.
      IssuedFrom: Map<string, string> }

let openBooks (ledger: Ledger) =
    { Ledger = ledger
      Customers = Map.empty
      Drafts = Map.empty
      Invoices = Map.empty
      Obligations = Map.empty
      IssuedFrom = Map.empty }

let saveCustomer (context: Context) (customer: Customer) (books: Books) =
    { books with
        Customers = books.Customers.Add(customer.Id, customer)
        Ledger = audit context "customer-saved" customer.Id books.Ledger }

let saveDraft (context: Context) (draft: DraftInvoice) (books: Books) =
    match books.IssuedFrom.TryFind draft.DraftId with
    | Some invoiceId -> Error [ AlreadyIssued invoiceId ]
    | None ->
        let what = if books.Drafts.ContainsKey draft.DraftId then "invoice-changed" else "invoice-created"
        Ok { books with Drafts = books.Drafts.Add(draft.DraftId, draft); Ledger = audit context what draft.DraftId books.Ledger }

/// The next sequential number for a year: `EF-2026-0001`, `EF-2026-0002`...
let nextNumber (prefix: string) (year: int) (books: Books) =
    let stem = $"{prefix}-{year:D4}-"

    let highest =
        books.Invoices
        |> Map.toList
        |> List.choose (fun (_, i) ->
            if i.Number.StartsWith stem then
                match Int32.TryParse(i.Number.Substring stem.Length) with
                | true, n -> Some n
                | _ -> None
            else
                None)
        |> List.fold max 0

    $"{stem}{highest + 1:D4}"

/// Ids and accounts the issuing step needs from its caller.
type IssueRequest =
    { DraftId: string
      IssueDate: DateOnly
      /// None takes the next sequential number.
      NumberOverride: string option
      Prefix: string
      ReceivableAccountId: string
      InvoiceId: string
      JournalEntryId: string
      ObligationId: string }

let private validateDraft (books: Books) (draft: DraftInvoice) =
    [ if draft.Lines.IsEmpty then NoLines
      match books.Customers.TryFind draft.CustomerId with
      | None -> UnknownCustomer draft.CustomerId
      | Some c when not c.Active -> InactiveCustomer draft.CustomerId
      | Some _ -> ()
      if not (isPositive (total draft)) then TotalNotPositive
      for line in draft.Lines do
          match books.Ledger.Accounts.TryFind line.RevenueAccountId with
          | Some a when a.Type = Revenue && a.Active -> ()
          | _ -> InvalidRevenueAccount line.RevenueAccountId ]

/// Issues a draft: invoice, AR/revenue entry and obligation, all or none.
/// Retrying with the same draft id returns the invoice already issued.
let issue (context: Context) (request: IssueRequest) (books: Books) : Result<Books * IssuedInvoice, InvoiceProblem list> =
    match books.IssuedFrom.TryFind request.DraftId with
    | Some invoiceId -> Ok(books, books.Invoices[invoiceId])
    | None ->
        match books.Drafts.TryFind request.DraftId with
        | None -> Error [ UnknownDraft request.DraftId ]
        | Some draft ->
            let number = request.NumberOverride |> Option.defaultValue (nextNumber request.Prefix request.IssueDate.Year books)

            let problems =
                validateDraft books draft
                @ (if books.Invoices |> Map.exists (fun _ i -> i.Number = number) then [ DuplicateInvoiceNumber number ] else [])

            if not problems.IsEmpty then
                Error problems
            else
                let customer = books.Customers[draft.CustomerId]
                let terms = draft.Terms |> Option.defaultValue customer.DefaultTerms
                let due = draft.DueDate |> Option.defaultValue (dueDate request.IssueDate terms)
                let invoiceTotal = total draft
                let dims project = { noDimensions with Client = Some customer.Id; Project = project }

                // Adjustments reduce (or add to) revenue on the first line's
                // account, so the entry always balances to the invoice total.
                let revenueLines =
                    draft.Lines
                    |> List.mapi (fun i line ->
                        let amount = if i = 0 then add (lineAmount line) (sum draft.Currency draft.Adjustments) else lineAmount line
                        { AccountId = line.RevenueAccountId; Side = Credit amount; Memo = Some line.Description; Dimensions = dims line.Project })
                    |> List.filter (fun l -> match l.Side with Credit m -> m.Minor <> 0L | Debit _ -> true)

                let entry =
                    { Date = request.IssueDate
                      Description = $"Invoice {number}"
                      Lines =
                        { AccountId = request.ReceivableAccountId; Side = Debit invoiceTotal; Memo = Some number; Dimensions = dims None }
                        :: revenueLines
                      Source = $"invoice:{number}" }

                match post context $"issue:{request.DraftId}" request.JournalEntryId entry books.Ledger with
                | Error problems -> Error [ LedgerProblems problems ]
                | Ok(ledger, posted) ->
                    let invoice =
                        { InvoiceId = request.InvoiceId
                          Number = number
                          CustomerId = customer.Id
                          Currency = draft.Currency
                          IssueDate = request.IssueDate
                          DueDate = due
                          Terms = terms
                          Lines = draft.Lines
                          Subtotal = subtotal draft
                          Adjustments = draft.Adjustments
                          Total = invoiceTotal
                          JournalEntryId = posted.Id
                          ObligationId = request.ObligationId
                          IssuedAt = context.When
                          SentAt = None
                          SentTo = None }

                    let obligation =
                        { Id = request.ObligationId
                          Kind = Receivable
                          Source = $"invoice:{request.InvoiceId}"
                          Party = customer.Id
                          OriginalAmount = invoiceTotal
                          DueDate = due
                          Cancelled = false }

                    Ok(
                        { books with
                            Ledger = audit context "invoice-issued" request.InvoiceId ledger
                            Drafts = books.Drafts.Remove request.DraftId
                            Invoices = books.Invoices.Add(request.InvoiceId, invoice)
                            Obligations = books.Obligations.Add(request.ObligationId, obligation)
                            IssuedFrom = books.IssuedFrom.Add(request.DraftId, request.InvoiceId) },
                        invoice
                    )

/// Records that an issued invoice was sent, and to whom (§8).
let markSent (context: Context) (invoiceId: string) (sentTo: string) (books: Books) =
    match books.Invoices.TryFind invoiceId with
    | None -> Error [ UnknownInvoice invoiceId ]
    | Some invoice ->
        Ok
            { books with
                Invoices = books.Invoices.Add(invoiceId, { invoice with SentAt = Some context.When; SentTo = Some sentTo })
                Ledger = audit context "invoice-sent" invoiceId books.Ledger }

/// The invariant of §6.1, checkable over any books: every issued invoice has
/// its posted journal entry and its obligation.
let issuedInvoicesHaveEntries (books: Books) =
    books.Invoices
    |> Map.forall (fun _ invoice ->
        books.Ledger.Entries.ContainsKey invoice.JournalEntryId
        && books.Obligations.ContainsKey invoice.ObligationId)
