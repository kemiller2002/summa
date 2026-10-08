/// Version 1 of the Chrona-to-Summa billing contract (DF-SUMMA-2026-0001).
///
/// Chrona owns time; Summa owns the financial consequence (Chrona
/// requirements expansion 17 and 45, Summa v0.1 §17, INV-CHR). Chrona
/// publishes approved billable time; Summa reports back when it has invoiced
/// published time or needs it adjusted. Identifiers and minutes only: rates,
/// amounts and invoices stay with Summa.
///
/// A new major version is a new namespace (`ChronaBilling.V2`) beside this
/// one, so incompatible versions can be read side by side.
namespace Summa.Contracts.ChronaBilling.V1

open System

/// The contract's name and version on the wire.
module Contract =
    [<Literal>]
    let Name = "summa.chrona-billing"

    [<Literal>]
    let Major = 1

    [<Literal>]
    let Minor = 0

    /// `1.0`.
    let Version = $"{Major}.{Minor}"

/// The Chrona activity a message is about, at one exact revision
/// (INV-CHR-002: identity plus a concurrency reference).
type SourceReference =
    { OrganizationId: string
      ActivityId: string
      /// Chrona's optimistic-concurrency revision, starting at 1.
      Revision: int }

/// When the work happened, as Chrona recorded it.
type ServicePeriod =
    { /// The business date the time belongs to.
      BusinessDate: DateOnly
      /// IANA time-zone id the business date is in.
      Zone: string
      /// Clock times, when the entry has them (both or neither).
      Interval: (DateTimeOffset * DateTimeOffset) option }

/// What the time was spent on, by reference-data id (Chrona expansion 4, 9).
type Classification =
    { ProjectId: string
      ClientId: string option
      EngagementId: string option
      ActivityTypeId: string
      Description: string
      BusinessPurpose: string
      Tags: string list }

/// The billing rule Chrona applied: billable minutes name the policy and
/// its version (Chrona expansion 7).
type PolicyReference = { PolicyId: string; Version: int }

/// Identifiers Chrona may keep beside billability (Chrona expansion 8).
/// Never rates or amounts.
type BillingReference =
    { RateReference: string option
      BillingClass: string option
      ContractReference: string option }

/// Whether and by whom the time was approved (INV-CHR-001). Approval is
/// organization-configurable in Chrona.
type Approval =
    | ApprovedBy of approver: string * at: DateTimeOffset
    | ApprovalNotRequired

/// How the time entered Chrona.
type EntryMethod =
    | Manual
    | Timer
    | Imported of sourceSystem: string

/// The observation an imported activity was accepted from.
type ObservationReference =
    { SourceSystem: string
      ObservationId: string
      ExternalUrl: string option }

/// Where the work came from (INV-PROV-003), carried verbatim. `Unknown`
/// when Chrona did not say: never inferred from the approver, the publisher
/// or Git authorship.
type Origin =
    | Unknown
    | Known of method: EntryMethod * observation: ObservationReference option * executionId: string option

/// Approved billable time Chrona publishes (Chrona 17, scenario 35).
type BillableTime =
    { /// Idempotency key: publishing the same id again is a retry.
      PublicationId: string
      Source: SourceReference
      /// The person who did the work (Chrona actor id).
      PerformerId: string
      Service: ServicePeriod
      Classification: Classification
      /// Exact recorded minutes; billing never changes them.
      ExactMinutes: int
      /// Billable minutes under `Policy`.
      BillableMinutes: int
      Policy: PolicyReference
      BillingReference: BillingReference
      Approval: Approval
      Origin: Origin
      /// Chrona activities this one was split from or merged from
      /// (INV-CHR-010 traceability).
      Lineage: string list
      WorkItemReference: string option
      /// The earlier publication of the same activity this one replaces,
      /// after Chrona corrected published time (Chrona 17, scenario 37).
      Supersedes: string option
      PublishedAt: DateTimeOffset }

/// Why published time is no longer billable as published.
type WithdrawalReason =
    | Voided
    /// Split or merged into these activities.
    | Replaced of by: string list
    | NoLongerBillable

/// Chrona withdraws a publication because the activity no longer exists as
/// published (Chrona 17: an explicit downstream obligation, never a silent
/// rewrite).
type Withdrawal =
    { PublicationId: string
      /// The activity at the revision that made it no longer billable.
      Source: SourceReference
      Reason: WithdrawalReason
      WithdrawnAt: DateTimeOffset }

/// What Chrona sends Summa.
type Publication =
    | BillableTimePublished of BillableTime
    | PublicationWithdrawn of Withdrawal

/// Summa has invoiced published time (Chrona 6.3 InvoicedExternally).
/// Field for field what Chrona records as its `InvoiceReport`.
type Invoiced =
    { OrganizationId: string
      PublicationId: string
      ActivityId: string
      Revision: int
      /// Summa's invoice identifier; never an amount.
      InvoiceReference: string
      At: DateTimeOffset }

/// Summa needs Chrona to review published time (Chrona 6.3
/// AdjustmentRequired), for example when the invoice that consumed it was
/// voided or a source changed after invoicing (INV-CHR-012).
type AdjustmentRequired =
    { OrganizationId: string
      PublicationId: string
      ActivityId: string
      Revision: int
      Reason: string
      At: DateTimeOffset }

/// What Summa sends Chrona.
type Feedback =
    | InvoicedExternally of Invoiced
    | AdjustmentNeeded of AdjustmentRequired
