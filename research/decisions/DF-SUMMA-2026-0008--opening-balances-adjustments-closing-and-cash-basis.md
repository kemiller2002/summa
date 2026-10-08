---
id: DF-SUMMA-2026-0008
title: Opening balances, adjusting entries, year-end closing and cash basis
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
tags: [ledger, periods, closing, reporting]
provenance:
  contributions:
    EXE-20261008T181823413Z-519cd941:
      operations: [created]
      at: 2026-10-08T18:18:23.840Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose how opening balances, adjustments, closing and cash basis work"
---

# DF-SUMMA-2026-0008 — Opening balances, adjusting entries, year-end closing and cash basis

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0026

## Decisions

1. **An entry's kind comes from its source.** These prefixes mark the kinds:
   - `opening-balance:<date>`;
   - `adjustment:<kind>[:<reference>]`;
   - `year-end-close:<year>`.

   Any other source is operational, and a reversal names the entry it reverses. No new record type is needed: the kind is derived, and stored entries keep their meaning.
2. **Opening balances** are one entry on the migration date, for balance sheet accounts only. Income before migration is already in retained earnings, so no history is recreated. The entry must balance before it is posted. It must come before any other activity, and there is only one per organization.
3. **CPA adjusting entries** have a kind: depreciation, accrual, prepaid, tax, reclassification or other. They can carry a workpaper reference. The person who posted one is read from the audit trail.
4. **Year-end closing** is proposed, never automatic (v0.2 §23).
   - `closingEntry` moves every revenue and expense balance into a named equity account. `postClosing` posts it once per year, when a person decides.
   - The income statement leaves closing entries out, so a closed year still reports what it earned.
5. **Cash basis is a projection over the one accrual ledger.**
   - Cash received for an invoice recognizes that invoice's revenue in proportion. The split is exact: rounding remainders go to the largest fractions.
   - The share of a payment that covered tax is not revenue.
   - Credits and deposits that came from cash count when applied. Credit memos and write-offs move no cash.
   - Cash expenses are expense debits in entries that credit a cash account.
