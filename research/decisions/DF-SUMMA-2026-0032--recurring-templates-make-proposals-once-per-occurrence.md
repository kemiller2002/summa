---
id: DF-SUMMA-2026-0032
title: Recurring templates make proposals, once per scheduled occurrence
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
  - research/decisions/DF-SUMMA-2026-0031--invoice-search-timeline-drill-down-and-attention.md
tags: [recurring, idempotency, records]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T12:49:24.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record recurring template decisions (WI-0029 slice 12)"
---

# DF-SUMMA-2026-0032 — Recurring templates make proposals, once per scheduled occurrence

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-REC keeps recurring billing in the model while recurring invoices stay
out of the first slice:

- a schedule creates proposals or drafts, never issued invoices, unless a
  later policy says otherwise (001);
- a template has its own fields (002);
- template edits affect only future proposals (003);
- a retry or restart never duplicates the proposal for an occurrence (004).

## Decisions

1. **Proposals, not drafts.** `Recurring.generateDue` makes an invoice
   proposal through `Billing.propose`. A person reviews, prices and
   accepts it like any other proposal. No automation policy that issues
   on its own exists, so none is assumed.
2. **One proposal id per occurrence.**
   - The id is `{template id}-{yyyyMMdd}` of the scheduled date.
   - An occurrence counts as generated when its proposal exists, in any
     state. An abandoned occurrence is therefore not proposed again.
   - A retry, a restart, a second scheduler or catching up after downtime
     finds the id taken and makes nothing.
   - Two writers racing to the same occurrence write the same record path.
     `Billing.propose` treats the same content as a no-op and refuses
     different content with `IdReused`.
3. **Versions are immutable records.**
   - Each version is `summa.recurring-template/<id>-v<version>`, written
     once.
   - An edit is the next version; versions only move forward one at a
     time.
   - A proposal records the template id, version and date as
     `recurring`, a marked schema 2 key, written only when present.
   - Proposals that no template made keep their bytes.
4. **Derived, not stored.** The occurrences and the next generation date
   are computed from the template and the proposals already made.
5. **Schedule and pricing kept small.**
   - Schedules are monthly on a day from 1 to 28, so every month has the
     day, or every n weeks from the start date.
   - The pricing rule is the template's lines at their fixed prices.
   - Rate-card pricing and other calendars are later decisions, made when
     needed.
6. **Defaults reach the draft.** `Billing.draftOf`, used by `accept`,
   gives a recurring proposal's draft the terms and delivery recipients of
   the template version that made it.

## Consequences

- Nothing invokes `generateDue` on a schedule yet; that is SUM3-006.
- The app has no screen to edit templates yet. Both stay open under
  WI-0029.
