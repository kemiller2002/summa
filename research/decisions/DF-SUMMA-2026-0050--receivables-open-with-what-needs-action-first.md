---
id: DF-SUMMA-2026-0050
title: The invoices list opens with what needs action first, each invoice saying why
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents: []
tags: [interface, receivables, attention]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-13T23:04:30.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record attention-first receivables (WI-0044, SUM4-010)"
---

# DF-SUMMA-2026-0050 — Receivables open with what needs action first

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §10 says the default receivables view should put first overdue
invoices, invoices due this week, large outstanding balances and unusual
payment delays. "The default experience should help the user decide what
requires action, not simply sort by invoice number." Until now the
invoices list (`#/invoices`) opened newest first.

## Decisions

1. **One domain function decides it.** `Attention.reasons today r invoice`
   gives an open invoice's reasons, and `Attention.rank today r` gives
   every open invoice in order. Both are derived from the books; nothing
   is stored.
2. **The four reasons are defined so that each can be checked and
   explained:**

   | Reason | When |
   |---|---|
   | Overdue | past its due date, with something still owed |
   | Due this week | due within the next seven days, today included (the same window as the existing "due soon" timing) |
   | Large balance | it owes at least 25% of everything owed in its currency, among at least three open invoices in that currency. With fewer, every balance is a large share, so the reason says nothing. |
   | Unusual delay | open more than seven days longer than the customer's usual time to pay: the median number of days from issue to paid over at least three paid invoices. With fewer, what is usual is not known. |

   The thresholds are named values in `Attention` (`largeShare`,
   `largeAmongAtLeast`, `usualFromAtLeast`, `unusualAfterDays`).
3. **The order.** An invoice is grouped by its most pressing reason:
   overdue, then due this week, then large balance, then unusual delay.
   Within a group the more pressing comes first: longest overdue, soonest
   due, largest share, furthest past usual. Open invoices with no reason
   follow by due date. Ties go to the larger amount owed, then to the
   number. Settled and voided invoices come last, newest first.
4. **It is the default, and other orders stay a choice away.** The
   invoices list's sort has a new value, `attention` ("Needs attention
   first"), which is the default and is therefore left out of the address.
   `newest`, `oldest`, `due`, `number` and `customer` are unchanged and
   stay in the address when chosen. The committed route inventory
   (`.echelon/routes.json`) records the new default.
5. **Each row says why.** The list has a "Needs attention" column with
   every reason an invoice has, for example "Overdue 15 days · Large
   balance: 76% of everything owed". It is empty when nothing calls for
   attention.

## Consequences

- SUM4-010 is tested:
  - `AttentionRankTests`: the group order, order within a group, multiple
    reasons, the unusual-delay boundary, too little history, too few open
    invoices, and the reason texts;
  - `ReceivablesAttentionTests`: the default list's order and reasons, and
    settled invoices last with another order chosen;
  - `deep-links.spec.js`: the default order and reason in a browser, and
    choosing another order.
- Tests that relied on "newest" being the default now say `sort=newest`,
  or use `attention` where they check that a default is left out of the
  address.
