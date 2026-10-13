---
id: DF-SUMMA-2026-0022
title: Delivery authority, one audit event per outcome, and attention until delivered or resolved
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0021--sensitive-data-integration-states-and-retention.md
tags: [delivery, access, audit, defects]
provenance:
  contributions:
    EXE-20261013T083907443Z-4a0e1018:
      operations: [created]
      at: 2026-10-13T08:51:06.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the delivery defect fixes (WI-0047)"
---

# DF-SUMMA-2026-0022 — Delivery authority, outcome events and attention

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0047

## Context

The program lead's independent acceptance of summa@50cde4a found three
escaped defects in the delivery lifecycle that WI-0027 introduced (PR #39):

- DEL-A-021: a manual send was recorded with no authority check.
- DEL-A-046: every outcome change was audited as the same
  `invoice-delivery-updated` event.
- DEL-P-007: queuing a new send after a failure cleared the attention
  queue before anything was delivered.

The user decided to fix them as their own work item.

## Decisions

1. **One delivery capability.** `Access.DeliverInvoice` covers sending,
   resending, recording a manual send and resolving a delivery problem.
   - It is in the bookkeeper grant, and so in the accountant and
     administrator grants.
   - It is not an approval gate: delivery is a communication operation,
     not a financial one (INV-DEL-001).
2. **Authority is an input to every delivery action an actor takes.**
   `Lifecycle.DeliveryAuthority` wraps whether an actor may deliver.
   - `Commands.deliveryAuthority roster` builds it from the roster.
   - `send`, `resend`, `recordManual` and `resolveDeliveryProblem`
     check it against `Context.Who`. They refuse with
     `NotAuthorizedToDeliver`, and nothing is written.
   - `recordOutcome` is the provider's report, not an action, so it takes
     no authority. It still cannot record a manual send.
   - The Ledger stays independent of `Summa.Access`, which is why
     authority is passed in rather than looked up.
3. **One audit event per outcome**, in the existing `invoice-delivery-*`
   vocabulary:
   - `queued`;
   - `sent-to-provider`;
   - `accepted-by-provider`;
   - `delivered`;
   - `failed`;
   - `bounced`;
   - `invoice-delivered-manually`;
   - `invoice-delivery-problem-resolved`.

   Each outcome event also carries its change (`outcome: A -> B`), as in
   INV-AUD-002.
4. **The attention item is the obligation, not the latest attempt.**
   `Lifecycle.deliveryProblem` is the latest failed or bounced attempt.
   - It stays open until a later attempt is Delivered or ManuallySent, or
     a person resolves it with `resolveDeliveryProblem`.
   - Queued, sent to provider and accepted by provider do not clear it.
   - The delivery dimension (`deliveryState`) still shows the latest
     attempt.
   - A later failure is a new problem.
5. **The resolution is follow-up state.** `FollowUp.DeliveryResolved`
   names the attempt it resolves, the note, the person and the time.
   - It is stored as `deliveryResolved` in `summa.follow-up`, at schema 2
     only when present.
   - Other follow-ups keep their schema 1 bytes.

## Consequences

- Signatures change. Old, then new:
  - `send ctx request r` → `send ctx authority request r`
  - `resend ctx attemptId fromAttempt r` → `resend ctx authority attemptId fromAttempt r`
  - `recordManual ctx attemptId invoiceId recipients how r` → `recordManual ctx authority attemptId invoiceId recipients how r`
  - New: `resolveDeliveryProblem ctx authority invoiceId note r` and `deliveryProblem r invoiceId`.
- Existing stored rosters do not hold `DeliverInvoice` until an
  administrator grants it. Delivery is not yet served by the application,
  so no live action is affected.
- SUM3-031 is tested. INV-DEL stays tested, with these fixes.
