/// After issue: delivery, follow-up, the invoice's independent state
/// dimensions, the actions they allow, and the documents related to it
/// (INV-STATE, INV-DEL, INV-COR-005).
///
/// Issuing is a financial operation and delivering is a communication
/// operation. A failed delivery never touches the invoice or the ledger, and
/// a resend is a new attempt against the same invoice (INV-DEL-001,
/// INV-DEL-006). Disputes and collection are operational state beside the
/// financial state, never in it (INV-STATE-006, INV-STATE-007).
module Summa.Ledger.Lifecycle

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

// ---- Delivery ----------------------------------------------------------------------------

type DeliveryProblem =
    | UnknownInvoiceToDeliver of string
    | VoidedInvoice of string
    /// The policy needs the PDF and it is not generated yet (INV-DOC-015).
    | PdfNotReady of ArtifactStatus option
    | NoRecipients
    | InvalidRecipient of string
    | UnknownAttempt of string
    | AttemptIdReused of string
    | OutcomeNotAllowed of from: DeliveryOutcome * ``to``: DeliveryOutcome

type DeliveryRequest =
    { AttemptId: string
      InvoiceId: string
      Recipients: Recipients
      Channel: DeliveryChannel
      Policy: DeliveryPolicy
      MessageTemplate: string
      RetryOf: string option }

let private validAddress (address: string) =
    let at = address.IndexOf '@'
    at > 0 && at < address.Length - 1 && not (address.Contains ' ')

let private needsPdf =
    function
    | AttachPdf
    | AttachAndLink -> true
    | LinkOnly -> false

let private save (context: Context) what subject (attempt: DeliveryAttempt) (r: Receivables) =
    { r with
        Books =
            { r.Books with
                Deliveries = r.Books.Deliveries.Add(attempt.Id, attempt)
                Ledger = audit context what subject r.Books.Ledger } }

/// Queues a delivery of an issued invoice. Retrying the same attempt id
/// changes nothing (INV-DEL-009).
let send (context: Context) (request: DeliveryRequest) (r: Receivables) =
    match r.Books.Deliveries.TryFind request.AttemptId, r.Books.Invoices.TryFind request.InvoiceId with
    | Some existing, _ when existing.InvoiceId = request.InvoiceId && existing.Recipients = request.Recipients -> Ok r
    | Some _, _ -> Error [ AttemptIdReused request.AttemptId ]
    | None, None -> Error [ UnknownInvoiceToDeliver request.InvoiceId ]
    | None, Some invoice ->
        let pdf = r.Books.Artifacts.TryFind $"{invoice.InvoiceId}-pdf" |> Option.map _.Status
        let everyone = request.Recipients.To @ request.Recipients.Cc

        let problems =
            [ if r.Voids.ContainsKey invoice.InvoiceId then VoidedInvoice invoice.InvoiceId
              if request.Recipients.To.IsEmpty then NoRecipients
              for address in everyone @ Option.toList request.Recipients.ReplyTo do
                  if not (validAddress address) then InvalidRecipient address
              if needsPdf request.Policy && pdf <> Some Generated then PdfNotReady pdf ]

        if not problems.IsEmpty then
            Error problems
        else
            Ok(
                save
                    context
                    "invoice-delivery-queued"
                    invoice.InvoiceId
                    { Id = request.AttemptId
                      InvoiceId = invoice.InvoiceId
                      At = context.When
                      Actor = context.Who
                      Recipients = request.Recipients
                      Channel = request.Channel
                      Policy = request.Policy
                      MessageTemplate = request.MessageTemplate
                      Outcome = Queued
                      ProviderReference = None
                      RetryOf = request.RetryOf }
                    r
            )

/// Sends again to the same recipients as a new attempt naming the old one.
let resend (context: Context) (attemptId: string) (fromAttempt: string) (r: Receivables) =
    match r.Books.Deliveries.TryFind fromAttempt with
    | None -> Error [ UnknownAttempt fromAttempt ]
    | Some earlier ->
        send
            context
            { AttemptId = attemptId
              InvoiceId = earlier.InvoiceId
              Recipients = earlier.Recipients
              Channel = earlier.Channel
              Policy = earlier.Policy
              MessageTemplate = earlier.MessageTemplate
              RetryOf = Some fromAttempt }
            r

let private rank =
    function
    | Queued -> 0
    | SentToProvider -> 1
    | ProviderAccepted -> 2
    | Delivered -> 3
    | DeliveryFailed _
    | Bounced _
    | ManuallySent -> 4

/// Records what the provider reported. Outcomes only move forward, and
/// Delivered is recorded only when the provider confirms delivery
/// (INV-DEL-010).
let recordOutcome (context: Context) (attemptId: string) (outcome: DeliveryOutcome) (providerReference: string option) (r: Receivables) =
    match r.Books.Deliveries.TryFind attemptId with
    | None -> Error [ UnknownAttempt attemptId ]
    | Some attempt when attempt.Outcome = outcome -> Ok r
    | Some attempt when outcome = ManuallySent || rank outcome <= rank attempt.Outcome || rank attempt.Outcome = 4 ->
        Error [ OutcomeNotAllowed(attempt.Outcome, outcome) ]
    | Some attempt ->
        Ok(
            save
                context
                "invoice-delivery-updated"
                attempt.InvoiceId
                { attempt with
                    Outcome = outcome
                    ProviderReference = providerReference |> Option.orElse attempt.ProviderReference }
                r
        )

/// Records a delivery made outside Summa, as such (INV-DEL-005).
let recordManual (context: Context) (attemptId: string) (invoiceId: string) (recipients: Recipients) (how: string) (r: Receivables) =
    match r.Books.Deliveries.TryFind attemptId, r.Books.Invoices.TryFind invoiceId with
    | Some existing, _ when existing.Outcome = ManuallySent && existing.InvoiceId = invoiceId -> Ok r
    | Some _, _ -> Error [ AttemptIdReused attemptId ]
    | None, None -> Error [ UnknownInvoiceToDeliver invoiceId ]
    | None, Some _ when recipients.To.IsEmpty -> Error [ NoRecipients ]
    | None, Some _ ->
        Ok(
            save
                context
                "invoice-delivered-manually"
                invoiceId
                { Id = attemptId
                  InvoiceId = invoiceId
                  At = context.When
                  Actor = context.Who
                  Recipients = recipients
                  Channel = Manual how
                  Policy = AttachPdf
                  MessageTemplate = "manual"
                  Outcome = ManuallySent
                  ProviderReference = None
                  RetryOf = None }
                r
        )

/// The attempts for an invoice, oldest first.
let attempts (r: Receivables) (invoiceId: string) =
    r.Books.Deliveries |> Map.toList |> List.map snd |> List.filter (fun d -> d.InvoiceId = invoiceId) |> List.sortBy (fun d -> d.At, d.Id)

/// The delivery dimension of an invoice (INV-STATE-005), from its latest attempt.
type DeliveryState =
    /// The PDF the delivery needs is not generated yet.
    | NotPrepared
    | ReadyToSend
    | Pending
    | Sent
    | DeliveredByProvider
    | Failed
    | BouncedBack
    | SentManually

let deliveryState (r: Receivables) (invoiceId: string) =
    match attempts r invoiceId |> List.tryLast with
    | None ->
        match r.Books.Artifacts.TryFind $"{invoiceId}-pdf" with
        | Some { Status = Generated } -> ReadyToSend
        | _ -> NotPrepared
    | Some latest ->
        match latest.Outcome with
        | Queued -> Pending
        | SentToProvider
        | ProviderAccepted -> Sent
        | Delivered -> DeliveredByProvider
        | DeliveryFailed _ -> Failed
        | Bounced _ -> BouncedBack
        | ManuallySent -> SentManually

/// What needs a person's attention (INV-DEL-008, INV-DOC-015): a failed or
/// bounced delivery, or a PDF that failed.
let attention (r: Receivables) =
    [ for KeyValue(id, _) in r.Books.Invoices do
          match deliveryState r id with
          | Failed -> id, "the last delivery failed"
          | BouncedBack -> id, "the last delivery bounced"
          | _ -> ()

          match r.Books.Artifacts.TryFind $"{id}-pdf" with
          | Some { Status = ArtifactStatus.Failed why } -> id, $"the PDF failed: {why}"
          | _ -> () ]

// ---- Follow-up: disputes and collection ------------------------------------------------

let followUp (r: Receivables) (invoiceId: string) =
    r.Books.FollowUps.TryFind invoiceId
    |> Option.defaultValue
        { InvoiceId = invoiceId
          Dispute = NotDisputed
          Collection = NoFollowUp }

let private setFollowUp (context: Context) what (f: FollowUp) (r: Receivables) =
    if not (r.Books.Invoices.ContainsKey f.InvoiceId) then
        Error [ UnknownInvoiceToDeliver f.InvoiceId ]
    else
        Ok
            { r with
                Books =
                    { r.Books with
                        FollowUps = r.Books.FollowUps.Add(f.InvoiceId, f)
                        Ledger = audit context what f.InvoiceId r.Books.Ledger } }

/// Marks an invoice disputed. The invoice, its balance and the ledger are
/// unchanged (INV-STATE-006).
let dispute (context: Context) (invoiceId: string) (reason: string) (on: DateOnly) (r: Receivables) =
    setFollowUp context "invoice-disputed" { followUp r invoiceId with Dispute = Disputed(reason, on) } r

let resolveDispute (context: Context) (invoiceId: string) (resolution: string) (on: DateOnly) (r: Receivables) =
    setFollowUp context "invoice-dispute-resolved" { followUp r invoiceId with Dispute = DisputeResolved(resolution, on) } r

let setCollection (context: Context) (invoiceId: string) (stage: CollectionStage) (r: Receivables) =
    setFollowUp context "invoice-collection-changed" { followUp r invoiceId with Collection = stage } r

// ---- State dimensions and actions ------------------------------------------------------------

/// Preparation and issuance (INV-STATE-002).
type Preparation =
    | ProposalStage
    | DraftStage
    | ReviewStage
    | IssuedStage
    | VoidedStage

/// The independent dimensions of an issued invoice (INV-STATE-001).
type InvoiceView =
    { Preparation: Preparation
      Settlement: InvoiceStatus
      Timing: Timing
      Delivery: DeliveryState
      Dispute: DisputeState
      Collection: CollectionStage }

let view (today: DateOnly) (r: Receivables) (invoice: IssuedInvoice) =
    let f = followUp r invoice.InvoiceId

    { Preparation = if r.Voids.ContainsKey invoice.InvoiceId then VoidedStage else IssuedStage
      Settlement = status r invoice
      Timing = timing today invoice.DueDate (outstanding r invoice)
      Delivery = deliveryState r invoice.InvoiceId
      Dispute = f.Dispute
      Collection = f.Collection }

/// Where a draft or proposal stands.
let draftPreparation (draft: DraftInvoice) =
    match draft.Review with
    | Editing -> DraftStage
    | SubmittedForReview _ -> ReviewStage

/// What may be done with an issued invoice now (INV-STATE-008), derived
/// from the same rules the commands enforce.
type InvoiceAction =
    | Deliver
    | RecordPaymentFor
    | ApplyCredit
    | CreateCreditMemo
    | OpenDispute
    | ResolveOpenDispute
    | VoidIt
    | WriteOffBalance
    | RefundCredit

let actions (r: Receivables) (invoice: IssuedInvoice) =
    let voided = r.Voids.ContainsKey invoice.InvoiceId
    let owed = (outstanding r invoice).Minor > 0L
    let settled = add (amountPaid r invoice) (writtenOff r invoice)
    let f = followUp r invoice.InvoiceId

    let refundable =
        r.CreditMemos
        |> Map.exists (fun id m ->
            m.InvoiceId = Some invoice.InvoiceId
            && Credits.remaining r (FromCreditMemo id) |> Option.exists (fun left -> left.Minor > 0L))

    [ if not voided then Deliver
      if owed then
          RecordPaymentFor
          ApplyCredit
          WriteOffBalance
      if not voided then CreateCreditMemo
      match f.Dispute with
      | Disputed _ -> ResolveOpenDispute
      | _ when not voided -> OpenDispute
      | _ -> ()
      if not voided && settled.Minor = 0L then VoidIt
      if refundable then RefundCredit ]

/// What may be done with a draft now.
type DraftAction =
    | EditDraft
    | DeleteDraft
    | SubmitForReview
    | ReturnToDraft
    | Issue

let draftActions (draft: DraftInvoice) =
    match draft.Review with
    | Editing -> [ EditDraft; DeleteDraft; SubmitForReview ]
    | SubmittedForReview _ -> [ EditDraft; ReturnToDraft; Issue ]

// ---- Related documents (INV-COR-005) -------------------------------------------------------

type Relation =
    | CreditedBy of creditMemoId: string
    | Corrects of invoiceId: string
    | CorrectedBy of invoiceId: string
    | ReissueOf of invoiceId: string
    | ReissuedAs of invoiceId: string
    /// The void, by the entry that reversed the invoice.
    | VoidedBy of journalEntryId: string
    | PaidBy of paymentId: string
    | RefundOf of refundId: string

/// Every document related to an invoice, in a stable order.
let related (r: Receivables) (invoiceId: string) =
    match r.Books.Invoices.TryFind invoiceId with
    | None -> []
    | Some invoice ->
        let voided id = r.Voids.ContainsKey id

        let memos =
            r.CreditMemos |> Map.toList |> List.filter (fun (_, m) -> m.InvoiceId = Some invoiceId) |> List.map fst

        [ for m in memos do
              yield CreditedBy m
          match invoice.Corrects with
          | Some original when voided original -> yield ReissueOf original
          | Some original -> yield Corrects original
          | None -> ()
          for KeyValue(id, other) in r.Books.Invoices do
              if other.Corrects = Some invoiceId then
                  yield (if voided invoiceId then ReissuedAs id else CorrectedBy id)
          match r.Voids.TryFind invoiceId with
          | Some v -> yield VoidedBy v.JournalEntryId
          | None -> ()
          for payment in liveAllocations r |> List.filter (fun a -> a.InvoiceId = invoiceId) |> List.map _.PaymentId |> List.distinct do
              yield PaidBy payment
          for KeyValue(id, refund) in r.Refunds do
              match refund.Source with
              | FromCreditMemo m when List.contains m memos -> yield RefundOf id
              | _ -> () ]
