---
id: DF-SUMMA-2026-0005
title: Discount posting, voids and pre-release record schemas
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
  - docs/requirements/implementation-gap-analysis.md
  - research/decisions/DF-SUMMA-2026-0001--summa-owns-the-chrona-contract-and-stores-through-arca.md
tags: [ledger, invoicing, discounts, voids, schema]
provenance:
  contributions:
    EXE-20261008T161945018Z-19388ec7:
      operations: [created]
      at: 2026-10-08T16:50:13.184Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose how discounts, voids and corrections are posted and stored"
---

# DF-SUMMA-2026-0005 — Discount posting, voids and pre-release record schemas

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0024

v0.2 §7, §8, §13 and §18 settle what corrections, voids, discounts and
terms must do, but not how they are posted or stored. These are the
choices.

## Decisions

1. **Discounts are posted against revenue.**
   - A line discount reduces that line's revenue credit. The unit price
     and the discount are both kept on the line.
   - Each invoice-level discount is a separate debit to the first line's
     revenue account, labelled with the discount.
   - A percentage is stored in basis points and rounded half away from
     zero to the minor unit.
   - Every invoice-level discount applies to the subtotal, never to
     another discount, so their order does not matter.
   - There is no separate contra-revenue account yet. A chart that wants
     one can add it later without changing stored invoices.
2. **A void needs an invoice nothing has settled.**
   - Payments, applied credits and write-offs must be reversed first, or
     the change handled with a credit memo. A void never strands money in
     Accounts Receivable.
   - The void reverses the invoice's journal entry, cancels its obligation
     and is stored as its own immutable `summa.invoice-void` record. The
     invoice is never rewritten.
3. **Corrections name the invoice they correct.** A reissued or correcting
   invoice carries `corrects`. A credit adjustment uses a credit memo
   against the invoice (WI-0024 part 1).
4. **Terms keep their source.** An issued invoice stores its terms, the due
   date they produced, and whether they came from the invoice, the
   customer or the system default. The system default comes from the
   organization manifest at issue time.
5. **Record schemas stay at version 1 until the first deployment.**
   - Summa has no deployment yet (WI-0036), so no stored data exists.
     This change adds fields to `summa.customer`, `summa.draft` and
     `summa.invoice` under schema version 1 instead of bumping it.
   - After the first deployment, any change to a stored shape bumps its
     schema version and ships with a migration (WI-0038), as SUM0-031
     requires.
