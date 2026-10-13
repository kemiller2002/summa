---
id: DF-SUMMA-2026-0040
title: Each integration's state, in one vocabulary, on the Work page
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
tags: [integrations, outages, operations]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T15:19:43.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record integration states in the app (WI-0029 slice 19)"
---

# DF-SUMMA-2026-0040 — Integration states on the Work page

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM3-032 asks Summa not to corrupt its state when an integration is
unavailable, and to use explicit integration states.

DF-SUMMA-2026-0021 defined one vocabulary (`Integrations`): Pending,
Succeeded, Retryable and PermanentFailure. It classifies queued GitHub
changes and delivery attempts, but the application did not show it.

## Decisions

1. **One row per integration Summa has**, on the Work page beside what
   needs attention.
   - **Where the books are kept.**
     - GitHub, from the browser's unsent changes. Changes waiting are
       Pending. The change that blocks the others takes its state from
       `Integrations.ofQueued`, with what it was and why.
     - Or this browser, for local books.
   - **Invoice delivery**, from each invoice's latest attempt
     (`Integrations.ofDelivery`). The worst state is shown, with a link to
     the invoice.
   - **Time from Chrona.** An unresolved correction to invoiced time needs
     a person. Otherwise it has succeeded.
2. **What to do, in words.**
   - Each row says what is waiting, what may pass on its own (Retryable)
     and what needs a person (PermanentFailure, shown as "Needs a person").
   - Each row links to where to act.
3. **No invented integrations.** Summa has no payment processor or bank
   integration, so none is listed. One is added to this list when it
   exists.
4. **The store reports the blocked change's state.** `Unsent` gains
   `BlockedState`, so the engine shows Integrations' classification rather
   than reading it from text.

## Consequences

- An outage changes neither invoices nor entries: a failed delivery and an
  unreachable GitHub are tested to leave both as they were.
