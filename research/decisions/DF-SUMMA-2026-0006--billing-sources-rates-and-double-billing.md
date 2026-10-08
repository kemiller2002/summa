---
id: DF-SUMMA-2026-0006
title: Billing sources, rate precedence and double-billing protection
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/contracts/chrona-billing.md
  - research/decisions/DF-SUMMA-2026-0001--summa-owns-the-chrona-contract-and-stores-through-arca.md
  - research/decisions/DF-SUMMA-2026-0003--chrona-billing-contract-v1-shape.md
tags: [billing, chrona, rates, proposals]
provenance:
  contributions:
    EXE-20261008T171643684Z-a5cb7cbd:
      operations: [created]
      at: 2026-10-08T17:16:44.208Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose how billing sources, rates and double-billing protection work"
---

# DF-SUMMA-2026-0006 — Billing sources, rate precedence and double-billing protection

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0025

v0.2 §14-17 and the invoice-generation requirements (INV-SOURCE, INV-CHR,
INV-RATE, INV-TERM) say what billing from time, fees, milestones and
expenses must guarantee. They leave these choices open.

## Decisions

1. **Imported time is Summa's snapshot, keyed by Chrona's ids.**
   - Each publication is stored once, as published, as an immutable
     `summa.time` record. The record holds Chrona's organization, activity
     and revision ids, its minutes, and its origin verbatim.
   - A correction from Chrona arrives as a new publication that supersedes
     the old one, so a snapshot is never rewritten.
   - Chrona's `clientId` is read as Summa's customer id. Both apps use the
     same reference data, so no copy of either app's records is shared
     (SUM0-024..026).
2. **Time is consumed per Chrona activity, not per publication.**
   - An activity on an issued invoice that is not voided cannot be billed
     again, under any later revision.
   - A correction or withdrawal of invoiced time raises a billing review.
     It never changes the invoice. A person settles the review with a
     credit memo, a supplemental invoice or no action (INV-CHR-012).
   - Voiding the invoice releases the time, and the feedback asks Chrona
     to review it.
3. **Reservations and consumption are derived, never stored as flags.**
   - A Proposed or ReadyForReview proposal reserves its sources.
   - An issued invoice that is not voided consumes them.
   - Accepting a proposal issues the invoice in the same transition. A
     failed issue therefore changes nothing (INV-CHR-006, INV-CHR-007).
   - Each command commits once with a change token and is decided again
     on a conflict, so two concurrent proposals cannot both reserve the
     same time.
4. **Rate precedence, most specific agreement first:**
   - invoice override;
   - engagement agreement;
   - project agreement;
   - customer default;
   - person rate;
   - role rate;
   - system default.

   Negotiated agreements beat internal standard rates. A person's own
   rate beats their role's. A scope holds exactly one rate, so resolution
   never chooses arbitrarily (INV-RATE-002). When nothing applies the
   line stays unpriced and the proposal cannot become ReadyForReview
   (INV-RATE-005). A zero price is allowed only when the caller's policy
   says so (INV-RATE-006).
5. **Hours are minutes over 60, in thousandths, rounded half away from
   zero.** Each line keeps the exact minutes of every entry it groups, so
   billed time reconciles exactly to its sources (INV-CHR-011). Lines are
   always split by rate. The policy dimensions (project, engagement,
   person, activity type, service month) only group within a rate, and
   the policy is stored on the proposal (INV-CHR-009).
6. **Terms precedence adds the engagement:** invoice, then engagement,
   then customer, then the organization default (INV-TERM-002).
7. **Fees.** An engagement bills its fixed fee either whole or by
   milestones, never both. Milestone shares cannot exceed the fee.
   Completing a milestone and invoicing it are separate commands.
8. **Expenses** are posted when recorded (Debit the expense, Credit what
   paid it). Billing one is separate revenue in the reimbursement account
   and is never netted against the expense.
9. **New capabilities:**
   - bookkeeper: `ImportSourceTime`, `RecordExpense`, `ProposeInvoice`;
   - accountant: `OverrideRate`, `ManageBilling`.

   Accepting a proposal needs `IssueInvoice`, which an agent may only
   prepare for a person.
10. **Feedback to Chrona is derived** from invoices, voids and open
    reviews. Every feedback message passes the contract's validation, so
    sending it again is always safe.
