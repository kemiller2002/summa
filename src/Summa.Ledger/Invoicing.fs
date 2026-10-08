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
      /// The payment instructions profile shown on this customer's invoices;
      /// None uses the organization's (INV-PAYINST-001).
      PaymentProfileId: string option
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

/// A discount on the whole invoice, with the label shown on it and, for a
/// material discount, why it was given (INV-ADJ-002).
type InvoiceDiscount =
    { Label: string
      Rule: Discount
      Reason: string option }

/// What an adjustment is (INV-ADJ-003). Discounts, credits and deposits
/// have their own concepts; an adjustment always adds to the invoice.
type AdjustmentKind =
    | Surcharge
    | Fee
    /// A tax the person entered, posted to the named liability account.
    /// Summa never calculates or infers tax (INV-ADJ-005).
    | Tax of code: string * accountId: string

type Adjustment =
    { Kind: AdjustmentKind
      Label: string
      Amount: Money }

let adjustmentTotal (currency: string) (adjustments: Adjustment list) =
    adjustments |> List.map _.Amount |> sum currency

/// Who an invoice is sent to (INV-DEL-002).
type Recipients =
    { To: string list
      Cc: string list
      ReplyTo: string option }

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

/// What an invoice says beyond its lines (INV-DATA-006, INV-DATA-013,
/// INV-DATA-014): references the customer needs, the service period, and
/// notes for the customer kept apart from internal ones.
type InvoiceDetails =
    { PurchaseOrder: string option
      ClientReference: string option
      ServicePeriod: (DateOnly * DateOnly) option
      CustomerNotes: string option
      InternalNotes: string option }

let noDetails =
    { PurchaseOrder = None
      ClientReference = None
      ServicePeriod = None
      CustomerNotes = None
      InternalNotes = None }

/// Where a draft stands before issue (INV-STATE-002). Any change to the
/// draft returns it to Editing, so a review applies to one version only
/// (INV-REV-006).
type DraftReview =
    | Editing
    | SubmittedForReview of version: int

/// A draft may be edited freely; it has an internal id, not a number (§5).
type DraftInvoice =
    { DraftId: string
      CustomerId: string
      Currency: string
      Lines: InvoiceLine list
      Adjustments: Adjustment list
      Discounts: InvoiceDiscount list
      Terms: PaymentTerms option
      DueDate: DateOnly option
      /// The issued invoice this one corrects or reissues (v0.2 §7).
      Corrects: string option
      /// The engagement it bills, whose terms apply before the customer's.
      EngagementId: string option
      Details: InvoiceDetails
      /// What an agent or integration assumed in preparing it, shown at
      /// review (INV-REV-003).
      Assumptions: string list
      Recipients: Recipients option
      /// Increases with every saved change; a save must name the version it
      /// changes (INV-DRAFT-004). A new draft is version 0 until saved.
      Version: int
      Review: DraftReview }

let subtotal (draft: DraftInvoice) = draft.Lines |> List.map lineAmount |> sum draft.Currency

let total (draft: DraftInvoice) =
    let net = subtotal draft
    add (subtract net (discountTotal draft.Currency net draft.Discounts)) (adjustmentTotal draft.Currency draft.Adjustments)

/// The issuer as shown on the invoice at issue (INV-DATA-005).
type IssuerSnapshot =
    { LegalName: string
      Address: string
      TaxId: string option
      Email: string
      /// Remittance instructions exactly as shown (INV-PAYINST-003).
      PaymentInstructions: string
      /// Payment methods shown, for example ACH or check (INV-PAYINST-004).
      PaymentMethods: string list
      /// The profile and version the instructions came from, `id@version`.
      PaymentProfile: string option }

/// The customer as billed at issue (INV-DATA-004): later changes to the
/// customer never change an issued invoice.
type CustomerSnapshot =
    { CustomerId: string
      Name: string
      BillingName: string
      BillingAddress: string
      Email: string }

/// The print template an invoice is rendered with (INV-DOC-007).
type TemplateRef =
    { Id: string
      Version: string
      /// The Folio renderer tier the template targets, for example `P0`.
      Profile: string }

/// Who committed the invoice, when, and which draft version (INV-REV-007).
type Approval =
    { By: string
      At: DateTimeOffset
      DraftVersion: int
      CorrelationId: string option
      Reason: string option }

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
      Adjustments: Adjustment list
      Total: Money
      /// The issued invoice this one corrects or reissues (v0.2 §7).
      Corrects: string option
      EngagementId: string option
      Details: InvoiceDetails
      Assumptions: string list
      Issuer: IssuerSnapshot
      Customer: CustomerSnapshot
      Template: TemplateRef
      Approval: Approval
      JournalEntryId: string
      ObligationId: string
      IssuedAt: DateTimeOffset }

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
    /// The draft changed since the version this request names (INV-DRAFT-004).
    | StaleDraft of current: int
    /// No line charges anything (INV-ISS-008).
    | NoSubstantiveLine
    /// A negative quantity or price; corrections use credit memos (INV-ADJ-004).
    | NegativeLine of string
    | InvalidAdjustment of string

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

/// A versioned payment instructions profile (INV-PAYINST-001). A version
/// is never changed; editing makes a new version, so invoices keep what
/// they showed (INV-PAYINST-003).
type PaymentProfile =
    { Id: string
      Version: int
      Label: string
      Methods: string list
      Instructions: string }

/// How an invoice is delivered.
type DeliveryChannel =
    | EmailChannel
    | SecureLink
    /// Sent outside Summa and recorded by a person (INV-DEL-005).
    | Manual of how: string

/// What a delivery carries (INV-DEL-004).
type DeliveryPolicy =
    | AttachPdf
    | LinkOnly
    | AttachAndLink

/// How far a delivery attempt got. Delivered only when the provider
/// confirms it (INV-DEL-010).
type DeliveryOutcome =
    | Queued
    | SentToProvider
    | ProviderAccepted
    | Delivered
    | DeliveryFailed of classification: string
    | Bounced of reason: string
    | ManuallySent

/// One attempt to deliver an issued invoice (INV-DEL-007).
type DeliveryAttempt =
    { Id: string
      InvoiceId: string
      At: DateTimeOffset
      Actor: string
      Recipients: Recipients
      Channel: DeliveryChannel
      Policy: DeliveryPolicy
      /// The subject and body template used (INV-DEL-003).
      MessageTemplate: string
      Outcome: DeliveryOutcome
      ProviderReference: string option
      /// The attempt this one retries or resends.
      RetryOf: string option }

type DisputeState =
    | NotDisputed
    | Disputed of reason: string * since: DateOnly
    | DisputeResolved of resolution: string * on: DateOnly

type CollectionStage =
    | NoFollowUp
    | Reminded of times: int
    | Escalated of note: string
    | OnHold of reason: string

/// The operational follow-up of an invoice (INV-STATE-006, INV-STATE-007):
/// it never changes the invoice or the ledger.
type FollowUp =
    { InvoiceId: string
      Dispute: DisputeState
      Collection: CollectionStage }

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
      Reviews: Map<string, BillingReview>
      /// Generated invoice artifacts by artifact id.
      Artifacts: Map<string, InvoiceArtifact>
      /// Payment profiles by `id@version`.
      PaymentProfiles: Map<string, PaymentProfile>
      Deliveries: Map<string, DeliveryAttempt>
      FollowUps: Map<string, FollowUp> }

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
      Reviews = Map.empty
      Artifacts = Map.empty
      PaymentProfiles = Map.empty
      Deliveries = Map.empty
      FollowUps = Map.empty }

let saveCustomer (context: Context) (customer: Customer) (books: Books) =
    { books with
        Customers = books.Customers.Add(customer.Id, customer)
        Ledger = audit context "customer-saved" customer.Id books.Ledger }

/// Saves a draft. The draft names the version it changes: 0 for a new
/// draft, otherwise the version that was read; a stale save is refused
/// rather than overwriting newer work (INV-DRAFT-004, INV-DRAFT-005).
/// Saving the same content again changes nothing; any change returns the
/// draft to Editing.
let saveDraft (context: Context) (draft: DraftInvoice) (books: Books) =
    match books.IssuedFrom.TryFind draft.DraftId, books.Drafts.TryFind draft.DraftId with
    | Some invoiceId, _ -> Error [ AlreadyIssued invoiceId ]
    | None, Some stored when stored = draft -> Ok books
    | None, Some stored when stored.Version <> draft.Version -> Error [ StaleDraft stored.Version ]
    | None, None when draft.Version <> 0 -> Error [ StaleDraft 0 ]
    | None, stored ->
        let what = if stored.IsSome then "invoice-changed" else "invoice-created"
        let saved = { draft with Version = draft.Version + 1; Review = Editing }
        Ok { books with Drafts = books.Drafts.Add(draft.DraftId, saved); Ledger = audit context what draft.DraftId books.Ledger }

/// Whether numbers restart every calendar year or run on (INV-NUM-004).
type NumberScope =
    | Yearly
    | Continuous

/// How invoice numbers are formed (INV-NUM-004, INV-NUM-005): the prefix
/// names the series, the scope says whether the year is part of it.
/// Numbers are unique and never reused; gaps are allowed (INV-NUM-008).
type NumberingPolicy =
    { Prefix: string
      Scope: NumberScope
      Digits: int }

let defaultNumbering prefix = { Prefix = prefix; Scope = Yearly; Digits = 4 }

/// The next number in the series: `EF-2026-0001` for a yearly series,
/// `EF-0001` for a continuous one. It is one more than the highest number
/// ever issued in the series, voided invoices included (INV-NUM-007).
let nextNumber (policy: NumberingPolicy) (issueDate: DateOnly) (books: Books) =
    let stem =
        match policy.Scope with
        | Yearly -> $"{policy.Prefix}-{issueDate.Year:D4}-"
        | Continuous -> $"{policy.Prefix}-"

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

    stem + (highest + 1).ToString(String('0', max 1 policy.Digits))

/// Ids and accounts the issuing step needs from its caller.
type IssueRequest =
    { DraftId: string
      IssueDate: DateOnly
      /// None takes the next sequential number.
      /// A number chosen by a person, checked for collisions (INV-NUM-009).
      NumberOverride: string option
      Numbering: NumberingPolicy
      /// The draft version being issued; None issues whatever version is
      /// current (INV-REV-006).
      ExpectedVersion: int option
      Issuer: IssuerSnapshot
      Template: TemplateRef
      /// Why the person issuing approved it, when a policy asks (INV-REV-007).
      ApprovalReason: string option
      /// The organization's default terms, used when neither the draft nor
      /// the customer has terms (v0.2 §18).
      SystemTerms: PaymentTerms
      ReceivableAccountId: string
      InvoiceId: string
      JournalEntryId: string
      ObligationId: string }

let private validateDraft (books: Books) (draft: DraftInvoice) =
    [ if draft.Lines.IsEmpty then NoLines
      elif draft.Lines |> List.forall (fun l -> (lineAmount l).Minor = 0L) then NoSubstantiveLine
      for line in draft.Lines do
          if line.QuantityThousandths < 0L || line.UnitPrice.Minor < 0L then NegativeLine line.Description
          if line.UnitPrice.Currency <> draft.Currency then InvalidDiscount $"'{line.Description}' is priced in {line.UnitPrice.Currency}, not {draft.Currency}"
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
      for a in draft.Adjustments do
          if a.Amount.Currency <> draft.Currency || not (isPositive a.Amount) then
              InvalidAdjustment $"'{a.Label}' must be positive and in {draft.Currency}; use a discount or a credit memo to reduce"

          match a.Kind with
          | Tax(_, account) ->
              match books.Ledger.Accounts.TryFind account with
              | Some found when found.Type = Liability && found.Active -> ()
              | _ -> InvalidAdjustment $"tax '{a.Label}' needs an active liability account, not {account}"
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

/// Surcharges and fees add to revenue; taxes post to their own account.
let private revenueAdjustments (draft: DraftInvoice) =
    draft.Adjustments
    |> List.filter (fun a ->
        match a.Kind with
        | Tax _ -> false
        | _ -> true)
    |> adjustmentTotal draft.Currency

/// Issues a draft: invoice, AR/revenue entry and obligation, all or none.
/// Retrying with the same draft id returns the invoice already issued.
let issue (context: Context) (request: IssueRequest) (books: Books) : Result<Books * IssuedInvoice, InvoiceProblem list> =
    match books.IssuedFrom.TryFind request.DraftId with
    | Some invoiceId -> Ok(books, books.Invoices[invoiceId])
    | None ->
        match books.Drafts.TryFind request.DraftId with
        | None -> Error [ UnknownDraft request.DraftId ]
        | Some draft ->
            let number = request.NumberOverride |> Option.defaultValue (nextNumber request.Numbering request.IssueDate books)

            let problems =
                (match request.ExpectedVersion with
                 | Some expected when expected <> draft.Version -> [ StaleDraft draft.Version ]
                 | _ -> [])
                @ validateDraft books draft
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
                        let amount = if i = 0 then add (lineAmount line) (revenueAdjustments draft) else lineAmount line
                        { AccountId = line.RevenueAccountId; Side = Credit amount; Memo = Some line.Description; Dimensions = dims line.Project })

                let discountLines =
                    draft.Discounts
                    |> List.map (fun d ->
                        { AccountId = firstAccount; Side = Debit(discountOn net d.Rule); Memo = Some $"Discount: {d.Label}"; Dimensions = dims None })

                let taxLines =
                    draft.Adjustments
                    |> List.choose (fun a ->
                        match a.Kind with
                        | Tax(code, account) -> Some { AccountId = account; Side = Credit a.Amount; Memo = Some $"{a.Label} ({code})"; Dimensions = dims None }
                        | _ -> None)

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
                        :: nonZero (revenueLines @ discountLines @ taxLines)
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
                          Details = draft.Details
                          Assumptions = draft.Assumptions
                          Issuer = request.Issuer
                          Customer =
                            { CustomerId = customer.Id
                              Name = customer.Name
                              BillingName = customer.BillingName
                              BillingAddress = customer.BillingAddress
                              Email = customer.Email }
                          Template = request.Template
                          Approval =
                            { By = context.Who
                              At = context.When
                              DraftVersion = draft.Version
                              CorrelationId = context.CorrelationId
                              Reason = request.ApprovalReason }
                          JournalEntryId = posted.Id
                          ObligationId = request.ObligationId
                          IssuedAt = context.When }

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
/// The record is a manual delivery attempt: Summa says it was sent, by
/// whom and to whom, never that it was delivered.
let markSent (context: Context) (invoiceId: string) (sentTo: string) (books: Books) =
    match books.Invoices.TryFind invoiceId with
    | None -> Error [ UnknownInvoice invoiceId ]
    | Some _ ->
        let n = books.Deliveries |> Map.filter (fun _ d -> d.InvoiceId = invoiceId) |> Map.count
        let id = $"{invoiceId}-sent-{n + 1}"

        let attempt =
            { Id = id
              InvoiceId = invoiceId
              At = context.When
              Actor = context.Who
              Recipients = { To = [ sentTo ]; Cc = []; ReplyTo = None }
              Channel = Manual "recorded as sent"
              Policy = AttachPdf
              MessageTemplate = "manual"
              Outcome = ManuallySent
              ProviderReference = None
              RetryOf = None }

        Ok
            { books with
                Deliveries = books.Deliveries.Add(id, attempt)
                Ledger = audit context "invoice-sent" invoiceId books.Ledger }

/// The invariant of §6.1, checkable over any books: every issued invoice has
/// its posted journal entry and its obligation.
let issuedInvoicesHaveEntries (books: Books) =
    books.Invoices
    |> Map.forall (fun _ invoice ->
        books.Ledger.Entries.ContainsKey invoice.JournalEntryId
        && books.Obligations.ContainsKey invoice.ObligationId)
