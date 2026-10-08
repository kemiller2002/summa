/// Where billing facts come from (v0.2 §14-17, INV-SOURCE, INV-CHR,
/// INV-RATE, SUM0-024..026).
///
/// Summa owns invoices and their accounting. It never owns another
/// application's records: imported time is Summa's snapshot of what Chrona
/// published, referenced by Chrona's stable identifiers and the exact
/// revision used, never a shared object (SUM0-024..026). Every invoice line
/// says where it came from and where its rate came from.
module Summa.Ledger.Sources

open System
open Summa.Ledger.Money

/// One Chrona activity, at the revision an invoice line used.
type TimeReference =
    { PublicationId: string
      ActivityId: string
      Revision: int
      /// Billable minutes this reference contributes; never adjusted.
      Minutes: int }

/// Where an invoice line's billing fact came from (INV-SOURCE-007: each
/// line keeps its own provenance).
type LineSource =
    /// Typed by a person, with no source record (INV-SOURCE-001).
    | ManualLine
    /// Approved Chrona time; a grouped line names every contributing entry (INV-CHR-010).
    | TimeSource of TimeReference list
    /// The fixed fee of an engagement, not determined by hours (INV-SOURCE-003).
    | FixedFeeSource of engagementId: string
    /// One completed milestone of an engagement (INV-SOURCE-004).
    | MilestoneSource of engagementId: string * milestoneId: string
    /// A billable expense recorded earlier as its own accounting event (INV-SOURCE-005).
    | ExpenseSource of expenseId: string

/// Where a rate came from (INV-RATE-001). The order of the cases is the
/// precedence, most specific agreement first (INV-RATE-002).
type RateSource =
    | InvoiceOverride
    | EngagementAgreement
    | ProjectAgreement
    | CustomerDefault
    | PersonRate
    | RoleRate
    | SystemDefault

/// A person's change to a proposed rate before issue (INV-RATE-004). The
/// new rate is the line's unit price.
type RateOverride =
    { Previous: Money option
      Reason: string
      Actor: string
      At: DateTimeOffset }

/// The provenance of a line's rate, snapshotted on the issued invoice
/// (INV-RATE-003).
type RateProvenance =
    { RateSource: RateSource
      /// What the rate came from, for example `engagement:ENG-1`.
      Reference: string
      Override: RateOverride option }

/// Who a rate applies to.
type RateScope =
    | ForEngagement of string
    | ForProject of string
    | ForCustomer of string
    | ForPerson of string
    | ForRole of string
    | Everyone

/// The organization's hourly rates and the roles people bill under.
type RateCard =
    { Rates: (RateScope * Money) list
      /// Person id -> role id.
      Roles: Map<string, string> }

let noRates = { Rates = []; Roles = Map.empty }

/// Resolves an hourly rate by explicit precedence: engagement, project,
/// customer, person, role, then the system default. Never guesses: None when
/// nothing applies (INV-RATE-005). Deterministic: a scope holds one rate.
let resolveRate (card: RateCard) (customerId: string) (engagementId: string option) (projectId: string option) (personId: string option) =
    let find scope = card.Rates |> List.tryFind (fun (s, _) -> s = scope) |> Option.map snd

    let candidates =
        [ engagementId |> Option.map (fun id -> EngagementAgreement, $"engagement:{id}", ForEngagement id)
          projectId |> Option.map (fun id -> ProjectAgreement, $"project:{id}", ForProject id)
          Some(CustomerDefault, $"customer:{customerId}", ForCustomer customerId)
          personId |> Option.map (fun id -> PersonRate, $"person:{id}", ForPerson id)
          personId
          |> Option.bind card.Roles.TryFind
          |> Option.map (fun role -> RoleRate, $"role:{role}", ForRole role)
          Some(SystemDefault, "system", Everyone) ]
        |> List.choose id

    candidates
    |> List.tryPick (fun (source, reference, scope) ->
        find scope |> Option.map (fun rate -> rate, { RateSource = source; Reference = reference; Override = None }))

/// Where imported time came from (INV-PROV-003), as Chrona said; never inferred.
type TimeOrigin =
    | OriginUnknown
    | OriginKnown of entryMethod: string * observation: string option * executionId: string option

/// Summa's snapshot of approved billable time Chrona published: identifiers
/// and minutes, exactly as published (INV-CHR-002, INV-SOURCE-008).
type SourceTime =
    { PublicationId: string
      OrganizationId: string
      ActivityId: string
      Revision: int
      PerformerId: string
      BusinessDate: DateOnly
      ProjectId: string
      /// Chrona's client reference id; Summa's customer id by shared reference data (SUM0-025).
      ClientId: string option
      EngagementId: string option
      ActivityTypeId: string
      Description: string
      ExactMinutes: int
      BillableMinutes: int
      Approved: bool
      RateReference: string option
      Origin: TimeOrigin
      Lineage: string list
      WorkItem: string option
      Supersedes: string option
      PublishedAt: DateTimeOffset }

/// Chrona withdrew published time: it is no longer billable as published.
type TimeWithdrawal =
    { PublicationId: string
      Revision: int
      Reason: string
      WithdrawnAt: DateTimeOffset }

/// An expense incurred on a customer's behalf (v0.2 §14). It is posted
/// when recorded (Debit the expense, Credit what paid it); billing it later
/// is a separate revenue event, never netted against it.
type Expense =
    { Id: string
      Date: DateOnly
      Description: string
      Amount: Money
      ExpenseAccountId: string
      PaidFromAccountId: string
      CustomerId: string option
      ProjectId: string option
      EngagementId: string option
      Billable: bool
      JournalEntryId: string }

/// How time is grouped into invoice lines (INV-CHR-009). Lines are always
/// split by rate as well, so no line mixes rates.
type GroupBy =
    | ByProject
    | ByEngagement
    | ByPerson
    | ByActivityType
    | ByServiceMonth

/// Something to review after an invoice consumed source time that later
/// changed (INV-CHR-012): a credit, a supplemental invoice, or no action.
type BillingReview =
    { Id: string
      PublicationId: string
      InvoiceId: string
      Reason: string
      RaisedAt: DateTimeOffset
      Resolution: string option }

/// What a generated invoice artifact is.
type ArtifactKind =
    /// The semantic HTML document, printed through Folio (INV-DOC-010).
    | InvoiceHtml
    /// The PDF for delivery and archival (INV-DOC-011).
    | InvoicePdf
    /// The versioned machine-readable document (INV-DOC-012).
    | InvoiceJson

type ArtifactStatus =
    | Generated
    /// To be produced after issue; delivery that needs it waits (INV-DOC-015).
    | Pending
    | Failed of reason: string

/// Metadata of a generated invoice artifact (INV-DOC-008, INV-DOC-009,
/// SUM0-036). The bytes live outside the financial records: HTML and JSON
/// are reproduced from the invoice's immutable snapshot and checked by
/// hash, and a PDF is stored in the environment's artifact storage and
/// referenced here (INV-DOC-013, SUM0-035).
type InvoiceArtifact =
    { Id: string
      InvoiceId: string
      Kind: ArtifactKind
      MediaType: string
      /// A stable reference: `summa:` for reproducible documents, or the
      /// artifact storage location of a stored file.
      Reference: string
      /// Lower-case hex SHA-256 of the bytes.
      Sha256: string option
      Size: int64 option
      CreatedAt: DateTimeOffset
      TemplateId: string
      TemplateVersion: string
      Renderer: string option
      Status: ArtifactStatus }
