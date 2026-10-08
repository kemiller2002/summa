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
open Summa.Ledger.Sources

type PaymentTerms =
    | DueOnReceipt
    | Net of days: int
    | CustomDate of DateOnly

let dueDate (issueDate: DateOnly) =
    function
    | DueOnReceipt -> issueDate
    | Net days -> issueDate.AddDays days
    | CustomDate date -> date

/// Where an invoice's terms came from (v0.2 §18, INV-TERM-002): the
/// invoice's own terms, then its engagement's, then the customer's default,
/// then the organization's default.
type TermsSource =
    | InvoiceTerms
    | EngagementTerms
    | CustomerTerms
    | SystemTerms

/// The terms that apply, and where they came from.
let resolveTerms
    (invoiceTerms: PaymentTerms option)
    (engagementTerms: PaymentTerms option)
    (customerTerms: PaymentTerms option)
    (systemTerms: PaymentTerms)
    =
    [ invoiceTerms, InvoiceTerms; engagementTerms, EngagementTerms; customerTerms, CustomerTerms ]
    |> List.tryPick (fun (terms, source) -> terms |> Option.map (fun t -> t, source))
    |> Option.defaultValue (systemTerms, SystemTerms)

/// What a milestone bills: a share of the engagement's fixed fee, or an amount.
type MilestoneAmount =
    | ShareOfFee of basisPoints: int
    | MilestoneFixed of Money

/// A billing milestone (v0.2 §17). Completing it and invoicing it are
/// separate actions.
type Milestone =
    { Id: string
      Label: string
      Amount: MilestoneAmount
      CompletedOn: DateOnly option }

/// A billing agreement with a customer (v0.2 §16-17): its fixed fee, its
/// milestones and its terms. Summa owns it; Chrona refers to it by id.
type Engagement =
    { Id: string
      CustomerId: string
      Name: string
      Currency: string
      FixedFee: Money option
      Milestones: Milestone list
      Terms: PaymentTerms option }

/// Calculated, never stored (§9).
let daysUntilDue (today: DateOnly) (due: DateOnly) = max 0 (due.DayNumber - today.DayNumber)
let daysPastDue (today: DateOnly) (due: DateOnly) = max 0 (today.DayNumber - due.DayNumber)

type Customer =
    { Id: string
      Name: string
      BillingName: string
      BillingAddress: string
      Email: string
      /// None falls back to the system default (v0.2 §18).
      DefaultTerms: PaymentTerms option
      Active: bool }

/// An explicit discount (v0.2 §13). A discount is never a silently lowered
/// unit price: the price stays and the discount is shown and posted.
type Discount =
    /// Basis points of the amount it applies to: 1000 is 10%.
    | Percent of basisPoints: int
    | Fixed of Money

/// What a discount takes off an amount, rounded half away from zero to the
/// minor unit, exactly.
let discountOn (amount: Money) =
    function
    | Percent basisPoints ->
        { amount with Minor = int64 (Decimal.Round(decimal amount.Minor * decimal basisPoints / 10000m, 0, MidpointRounding.AwayFromZero)) }
    | Fixed m -> m

/// A discount on the whole invoice, with the label shown on it.
type InvoiceDiscount = { Label: string; Rule: Discount }

type InvoiceLine =
    { Description: string
      /// Thousandths, so 7.5 hours is 7500.
      QuantityThousandths: int64
      UnitPrice: Money
      RevenueAccountId: string
      Project: string option
      WorkItem: string option
      /// A discount on this line only.
      Discount: Discount option
      /// Where the line's billing fact came from (INV-SOURCE-007).
      Source: LineSource
      /// Where its rate came from; None for a line priced by hand (INV-RATE-001).
      Rate: RateProvenance option }

/// Quantity times unit price, before the line's discount.
let lineGross (line: InvoiceLine) = extend line.QuantityThousandths line.UnitPrice

let lineDiscount (line: InvoiceLine) =
    let gross = lineGross line
    line.Discount |> Option.map (discountOn gross) |> Option.defaultValue (zero gross.Currency)

/// What the line charges: gross less its own discount.
let lineAmount (line: InvoiceLine) = subtract (lineGross line) (lineDiscount line)

/// What invoice-level discounts take off a subtotal; each applies to the
/// subtotal, never to another discount, so the order does not matter.
let discountTotal (currency: string) (subtotal: Money) (discounts: InvoiceDiscount list) =
    discounts |> List.map (fun d -> discountOn subtotal d.Rule) |> sum currency

/// A draft may be edited freely; it has an internal id, not a number (§5).
type DraftInvoice =
    { DraftId: string
      CustomerId: string
      Currency: string
      Lines: InvoiceLine list
      /// Signed adjustments (a discount is negative).
      Adjustments: Money list
      Discounts: InvoiceDiscount list
      Terms: PaymentTerms option
      DueDate: DateOnly option
      /// The issued invoice this one corrects or reissues (v0.2 §7).
      Corrects: string option
      /// The engagement it bills, whose terms apply before the customer's.
      EngagementId: string option }

let subtotal (draft: DraftInvoice) = draft.Lines |> List.map lineAmount |> sum draft.Currency

let total (draft: DraftInvoice) =
    let net = subtotal draft
    add (subtract net (discountTotal draft.Currency net draft.Discounts)) (sum draft.Currency draft.Adjustments)

type IssuedInvoice =
    { InvoiceId: string
      Number: string
      CustomerId: string
      Currency: string
      IssueDate: DateOnly
      DueDate: DateOnly
      Terms: PaymentTerms
      /// Which terms were used, kept with the due date they produced (v0.2 §18).
      TermsSource: TermsSource
      Lines: InvoiceLine list
      /// The lines' amounts after their own discounts.
      Subtotal: Money
      Discounts: InvoiceDiscount list
      Adjustments: Money list
      Total: Money
      /// The issued invoice this one corrects or reissues (v0.2 §7).
      Corrects: string option
      EngagementId: string option
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
    /// A discount that is not 0.01% to 100%, not positive, in another
    /// currency, or larger than what it applies to.
    | InvalidDiscount of string
    /// A correcting invoice must name an issued invoice of the same customer.
    | InvalidCorrection of string
    | InvalidEngagement of string

/// Where an invoice proposal stands (v0.1 §17, v0.2 §15): approved time
/// becomes a proposal, a person reviews it, and only then an invoice.
type ProposalState =
    | Proposed
    /// Every line is priced and every source is current (INV-RATE-005, INV-CHR-008).
    | ReadyForReview
    /// Cancelled; its sources are free again (INV-CHR-005).
    | Abandoned
    | Accepted of invoiceId: string

/// A proposed line: an invoice line, and whether its price is known yet.
/// An unpriced line keeps a zero unit price that is never issued.
type ProposalLine = { Line: InvoiceLine; Priced: bool }

/// An invoice proposal. While it is Proposed or ReadyForReview it reserves
/// its sources, so another proposal cannot bill them (INV-CHR-004).
type Proposal =
    { Id: string
      CustomerId: string
      EngagementId: string option
      Currency: string
      Lines: ProposalLine list
      /// The grouping used for its time, kept so it can be reproduced (INV-CHR-009).
      Grouping: GroupBy list
      State: ProposalState
      CreatedAt: DateTimeOffset }

type Books =
    { Ledger: Ledger
      Customers: Map<string, Customer>
      Drafts: Map<string, DraftInvoice>
      Invoices: Map<string, IssuedInvoice>
      Obligations: Map<string, Obligation>
      /// Draft id -> the invoice it became.
      IssuedFrom: Map<string, string>
      Engagements: Map<string, Engagement>
      Expenses: Map<string, Expense>
      /// Imported time by publication id.
      Time: Map<string, SourceTime>
      Withdrawals: Map<string, TimeWithdrawal>
      Proposals: Map<string, Proposal>
      Rates: RateCard
      Reviews: Map<string, BillingReview> }

let openBooks (ledger: Ledger) =
    { Ledger = ledger
      Customers = Map.empty
      Drafts = Map.empty
      Invoices = Map.empty
      Obligations = Map.empty
      IssuedFrom = Map.empty
      Engagements = Map.empty
      Expenses = Map.empty
      Time = Map.empty
      Withdrawals = Map.empty
      Proposals = Map.empty
      Rates = noRates
      Reviews = Map.empty }

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
      /// The organization's default terms, used when neither the draft nor
      /// the customer has terms (v0.2 §18).
      SystemTerms: PaymentTerms
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
          | _ -> InvalidRevenueAccount line.RevenueAccountId

          match line.Discount with
          | Some(Percent bp) when bp < 1 || bp > 10000 -> InvalidDiscount $"'{line.Description}': a percentage must be 0.01%% to 100%%"
          | Some(Fixed m) when m.Currency <> draft.Currency || not (isPositive m) ->
              InvalidDiscount $"'{line.Description}': a fixed discount must be positive and in {draft.Currency}"
          | Some _ when (lineAmount line).Minor < 0L -> InvalidDiscount $"'{line.Description}': the discount is larger than the line"
          | _ -> ()
      for d in draft.Discounts do
          match d.Rule with
          | Percent bp when bp < 1 || bp > 10000 -> InvalidDiscount $"'{d.Label}': a percentage must be 0.01%% to 100%%"
          | Fixed m when m.Currency <> draft.Currency || not (isPositive m) -> InvalidDiscount $"'{d.Label}': a fixed discount must be positive and in {draft.Currency}"
          | _ -> ()
      if
          draft.Lines |> List.forall (fun l -> (lineAmount l).Minor >= 0L)
          && discountTotal draft.Currency (subtotal draft) draft.Discounts > subtotal draft
      then
          InvalidDiscount "the invoice discounts are larger than the subtotal"
      match draft.EngagementId |> Option.map (fun id -> id, books.Engagements.TryFind id) with
      | Some(id, None) -> InvalidEngagement $"engagement {id} does not exist"
      | Some(id, Some e) when e.CustomerId <> draft.CustomerId -> InvalidEngagement $"engagement {id} is another customer's"
      | _ -> ()
      match draft.Corrects with
      | Some original ->
          match books.Invoices.TryFind original with
          | None -> InvalidCorrection $"invoice {original} is not issued"
          | Some i when i.CustomerId <> draft.CustomerId -> InvalidCorrection $"invoice {original} is another customer's"
          | Some _ -> ()
      | None -> () ]

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
                let engagementTerms = draft.EngagementId |> Option.bind books.Engagements.TryFind |> Option.bind _.Terms
                let terms, termsSource = resolveTerms draft.Terms engagementTerms customer.DefaultTerms request.SystemTerms
                let due = draft.DueDate |> Option.defaultValue (dueDate request.IssueDate terms)
                let invoiceTotal = total draft
                let dims project = { noDimensions with Client = Some customer.Id; Project = project }

                // Each line credits its revenue net of its own discount.
                // Adjustments reduce (or add to) revenue on the first line's
                // account; invoice discounts debit it, one line each, so the
                // entry always balances to the invoice total and every
                // discount stays visible in the books (v0.2 §13).
                let net = subtotal draft
                let firstAccount = draft.Lines.Head.RevenueAccountId

                let revenueLines =
                    draft.Lines
                    |> List.mapi (fun i line ->
                        let amount = if i = 0 then add (lineAmount line) (sum draft.Currency draft.Adjustments) else lineAmount line
                        { AccountId = line.RevenueAccountId; Side = Credit amount; Memo = Some line.Description; Dimensions = dims line.Project })

                let discountLines =
                    draft.Discounts
                    |> List.map (fun d ->
                        { AccountId = firstAccount; Side = Debit(discountOn net d.Rule); Memo = Some $"Discount: {d.Label}"; Dimensions = dims None })

                let nonZero =
                    List.filter (fun l ->
                        match l.Side with
                        | Credit m
                        | Debit m -> m.Minor <> 0L)

                let entry =
                    { Date = request.IssueDate
                      Description = $"Invoice {number}"
                      Lines =
                        { AccountId = request.ReceivableAccountId; Side = Debit invoiceTotal; Memo = Some number; Dimensions = dims None }
                        :: nonZero (revenueLines @ discountLines)
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
                          TermsSource = termsSource
                          Lines = draft.Lines
                          Subtotal = net
                          Discounts = draft.Discounts
                          Adjustments = draft.Adjustments
                          Total = invoiceTotal
                          Corrects = draft.Corrects
                          EngagementId = draft.EngagementId
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
