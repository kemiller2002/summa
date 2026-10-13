---
id: DF-SUMMA-2026-0031
title: Invoice search, timeline, drill-down and attention items
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
  - research/decisions/DF-SUMMA-2026-0028--entry-references-settlement-and-explanations.md
  - research/decisions/DF-SUMMA-2026-0029--invoice-data-line-identity-details-references-rounding.md
tags: [operations, search, attention]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T12:31:31.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record invoice operational view decisions (WI-0029 slice 11)"
---

# DF-SUMMA-2026-0031 — Invoice search, timeline, drill-down and attention items

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-OPS asks that invoices be found by number, customer, amount, date,
project, engagement, PO or reference, source id and delivery recipient;
that receivables and aging use what is still owed; that each invoice has
one readable activity timeline; that a person can drill from an invoice
to its journal entry and lines, its sources, and what settled it; and
that invoice obligations feed the attention queue. The invoice list
searched only the number and customer name. The History tab showed only
the invoice's own audit events. The work queue had no proposal or source
conflict items.

## Decisions

1. **Search is derived, never stored.** `Lifecycle.searchTexts` lists
   what an invoice is found by, from the issued invoice and its delivery
   attempts: number and id, customer id, name and billing name, the total
   as shown and as typed, issue and due dates (ISO), line projects,
   engagement, PO, client reference and typed references, source ids
   (Chrona publication and activity, engagement, milestone, expense), and
   the To and Cc recipients. The list matches the search text against
   these, ignoring case. Nothing is indexed or persisted, so there is no
   index to drift from the records.
2. **Receivables and aging are unchanged.** They already use the
   outstanding amount (`Payments.aging`); tests now assert INV-OPS-002 and
   003 by id.
3. **One timeline from the audit trail.** `Lifecycle.timeline` gathers the
   audit events whose subject is the invoice, its drafts, its allocations
   and their payments, applications, write-offs and credit memos against
   it, oldest first. A payment that settles nothing of the invoice stays
   out. No second event log is kept.
4. **Drill-down by address.** Each settlement row links to the payment,
   the credit memo, or otherwise the journal entry it posted. The invoice
   already links to its journal entry, whose lines link to their account
   ledgers. The History tab lists each line by its stable id with where it
   came from.
5. **Attention items.** The work queue adds a proposal ready for review
   and an unresolved billing review (Chrona corrected or withdrew invoiced
   time). Approval required (a draft submitted for review), PDF failed,
   delivery failed or bounced, overdue and disputed were already there.
   Every item carries the address where it is acted on.

## Consequences

- INV-OPS-001 to 005 are tested. INV-OPS-006 is partial: Summa has no
  model for a customer that requires a PO, nor for a payment promise, so
  "Missing PO" and "Payment promise missed" cannot be raised yet. Both
  stay open under WI-0029.
- Search scans every invoice's texts on each change. This has not been
  measured at scale; an index, if one is needed, is a later decision made
  from a measurement.
