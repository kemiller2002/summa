/// Integration state (SUM3-031..033): what Summa knows about every exchange
/// with an outside system, in one vocabulary. An unavailable integration
/// never changes the books: an invoice stays issued when its email fails,
/// and a change GitHub could not take stays queued, not lost.
///
/// Pure.
module Summa.Storage.Integrations

open Arca
open Summa.Ledger.Invoicing

type IntegrationState =
    /// Not sent yet, or sent with no answer yet.
    | Pending
    | Succeeded
    /// Failed in a way that may pass: send again.
    | Retryable of reason: string
    /// Failed in a way sending again will not fix: a person must act.
    | PermanentFailure of reason: string

/// An invoice delivery attempt's state. A failure classified `permanent`
/// (or a bounce) is final for that attempt; any other failure may be
/// retried with `Lifecycle.resend`, which never re-issues the invoice.
let ofDelivery (outcome: DeliveryOutcome) =
    match outcome with
    | Queued
    | SentToProvider
    | ProviderAccepted -> Pending
    | Delivered
    | ManuallySent -> Succeeded
    | DeliveryFailed classification when classification = "permanent" -> PermanentFailure classification
    | DeliveryFailed classification -> Retryable classification
    | Bounced reason -> PermanentFailure reason

/// A change queued for GitHub: pending until sent; a conflict or an
/// unknown outcome is decided again or reconciled; a refusal needs a person.
let ofQueued (state: EntryState) =
    match state with
    | EntryState.Synchronized _ -> Succeeded
    | EntryState.Conflicted _ -> Retryable "someone else changed the same records first"
    | EntryState.OutcomeUnknown _ -> Retryable "GitHub did not say whether it was saved; it is reconciled before anything is sent again"
    | EntryState.Refused reason -> PermanentFailure reason
    | EntryState.Abandoned reason -> PermanentFailure reason
    | _ -> Pending
