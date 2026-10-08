/// The Summa side of the Chrona billing contract (DF-SUMMA-2026-0001,
/// `EchelonFoundry.Summa.Contracts` 0.1.0, wire version 1.0).
///
/// Chrona's publications become Summa's own snapshots of the time. Only
/// Chrona's identifiers, revision and minutes are kept, and its origin is
/// carried verbatim, never inferred (INV-PROV-003). Importing never needs
/// Chrona to be reachable: it works on messages already received
/// (INV-PROV-005). Summa's feedback is derived from its invoices, voids and
/// reviews, so sending it again is always safe.
module Summa.Ledger.ChronaImport

open System
open Summa.Contracts.ChronaBilling.V1
open Summa.Ledger.Ledger

let private entryMethod =
    function
    | Manual -> "manual"
    | Timer -> "timer"
    | Imported system -> $"imported:{system}"

/// Summa's snapshot of published time.
let toSourceTime (t: BillableTime) : Sources.SourceTime =
    { PublicationId = t.PublicationId
      OrganizationId = t.Source.OrganizationId
      ActivityId = t.Source.ActivityId
      Revision = t.Source.Revision
      PerformerId = t.PerformerId
      BusinessDate = t.Service.BusinessDate
      ProjectId = t.Classification.ProjectId
      ClientId = t.Classification.ClientId
      EngagementId = t.Classification.EngagementId
      ActivityTypeId = t.Classification.ActivityTypeId
      Description = t.Classification.Description
      ExactMinutes = t.ExactMinutes
      BillableMinutes = t.BillableMinutes
      // Chrona publishes only time that is approved, or whose organization
      // does not require approval.
      Approved =
        (match t.Approval with
         | ApprovedBy _
         | ApprovalNotRequired -> true)
      RateReference = t.BillingReference.RateReference
      Origin =
        (match t.Origin with
         | Unknown -> Sources.OriginUnknown
         | Known(method, observation, execution) ->
             Sources.OriginKnown(entryMethod method, observation |> Option.map (fun o -> $"{o.SourceSystem}:{o.ObservationId}"), execution))
      Lineage = t.Lineage
      WorkItem = t.WorkItemReference
      Supersedes = t.Supersedes
      PublishedAt = t.PublishedAt }

let private reasonText =
    function
    | Voided -> "voided in Chrona"
    | Replaced by -> "replaced by " + String.Join(", ", by)
    | NoLongerBillable -> "no longer billable"

let toWithdrawal (w: Withdrawal) : Sources.TimeWithdrawal =
    { PublicationId = w.PublicationId
      Revision = w.Source.Revision
      Reason = reasonText w.Reason
      WithdrawnAt = w.WithdrawnAt }

/// Why a message was not taken in.
type ImportProblem =
    | InvalidMessage of Summa.Contracts.Problem list
    | Refused of Billing.BillingProblem list

/// Takes in one message from Chrona, validated against the contract first.
let receive (context: Context) (message: Publication) (r: Payments.Receivables) =
    match Validate.publication message with
    | _ :: _ as problems -> Error(InvalidMessage problems)
    | [] ->
        match message with
        | BillableTimePublished t -> Billing.importTime context (toSourceTime t) r |> Result.map fst |> Result.mapError Refused
        | PublicationWithdrawn w -> Billing.withdrawTime context (toWithdrawal w) r |> Result.mapError Refused

/// What Summa tells Chrona, derived from the books:
/// - invoiced time, for every time reference on an issued invoice;
/// - an adjustment request for time on a voided invoice;
/// - an adjustment request for every open billing review.
let feedback (r: Payments.Receivables) : Feedback list =
    let books = r.Books

    let voidedAt invoiceId =
        books.Ledger.Audit
        |> List.tryFind (fun a -> a.What = "invoice-voided" && a.Subject = invoiceId)
        |> Option.map _.When
        |> Option.defaultValue books.Invoices[invoiceId].IssuedAt

    let timeOn (invoice: Invoicing.IssuedInvoice) =
        invoice.Lines
        |> List.collect (fun l ->
            match l.Source with
            | Sources.TimeSource refs -> refs
            | _ -> [])
        |> List.choose (fun ref -> books.Time.TryFind ref.PublicationId |> Option.map (fun t -> ref, t))

    [ for KeyValue(id, invoice) in books.Invoices do
          for ref, time in timeOn invoice do
              InvoicedExternally
                  { OrganizationId = time.OrganizationId
                    PublicationId = ref.PublicationId
                    ActivityId = ref.ActivityId
                    Revision = ref.Revision
                    InvoiceReference = invoice.Number
                    At = invoice.IssuedAt }

              if r.Voids.ContainsKey id then
                  AdjustmentNeeded
                      { OrganizationId = time.OrganizationId
                        PublicationId = ref.PublicationId
                        ActivityId = ref.ActivityId
                        Revision = ref.Revision
                        Reason = $"Invoice {invoice.Number} was voided; the time is billable again"
                        At = voidedAt id }
      for KeyValue(_, review) in books.Reviews do
          match review.Resolution, books.Time.TryFind review.PublicationId with
          | None, Some time ->
              AdjustmentNeeded
                  { OrganizationId = time.OrganizationId
                    PublicationId = time.PublicationId
                    ActivityId = time.ActivityId
                    Revision = time.Revision
                    Reason = review.Reason
                    At = review.RaisedAt }
          | _ -> () ]
