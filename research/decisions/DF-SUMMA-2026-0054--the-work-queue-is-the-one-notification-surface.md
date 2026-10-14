---
id: DF-SUMMA-2026-0054
title: The work queue is the one notification surface, and each item names one of four triggers
status: accepted
version: 1.0.0
created: 2026-10-14
updated: 2026-10-14
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents: []
tags: [interface, notifications, work-queue]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-14T08:30:20.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the notification philosophy (WI-0044, SUM4-032)"
---

# DF-SUMMA-2026-0054 — The work queue is the one notification surface

- **Date:** 2026-10-14
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §32: "Do not build notification spam." Notifications should be
generated only when action is required, a deadline is approaching, a
financial operation failed, or a meaningful condition changed. "The
dashboard and work queue should remain the primary operational source."

## Decisions

1. **One surface.** Summa raises things only in the work queue
   (`#/work`), whose count the dashboard shows. It sends no e-mail, push
   or other notification to people. Delivering invoices to customers is
   a separate thing, governed by the delivery requirements. A notice
   after a person's own action ("Invoice … issued.") confirms what they
   did. It is not a notification and lasts only until the next action.
2. **Four triggers, typed.** Every work item names its `Trigger`:
   - `ActionRequired`: a draft to finish or issue, a missing PO, an
     overdue invoice, an unapplied payment, a proposal ready, a PDF not
     yet stored, last month still open;
   - `DeadlineApproaching`: a customer's promised payment date within
     `promiseNoticeDays` (3) and not yet kept;
   - `OperationFailed`: a failed or bounced delivery, or a failed PDF;
   - `ConditionChanged`: a dispute, a missed promise, or invoiced time
     corrected at its source.

   The type is closed, so a new kind of item cannot be added without a
   trigger. The queue shows the trigger beside each item.
3. **Derived, so no spam.** Each item is derived from the books while its
   condition holds. It goes when the condition is resolved: paid,
   applied, issued, resolved. There is nothing to acknowledge, nothing is
   stored, a second look raises nothing new, and one condition is one
   item.

## Consequences

- SUM4-032 is tested:
  - `NotificationTests`: every item names one of the four triggers; a
    promise is raised three days out, not ten, and becomes "Changed"
    once missed; items are not repeated and go when resolved;
  - `attention.spec.js`: the trigger is shown on each item.
- A future outbound notification channel must take its items from this
  queue and the same triggers.
