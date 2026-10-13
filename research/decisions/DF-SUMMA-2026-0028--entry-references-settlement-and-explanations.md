---
id: DF-SUMMA-2026-0028
title: Entries name what they were posted for; invoices show their settlement; entries are explained in plain words
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
  - research/decisions/DF-SUMMA-2026-0027--invoice-capabilities-and-approval.md
tags: [accounting, settlement, currency]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T11:34:08.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record accounting linkage, settlement and currency decisions (WI-0029 slice 8)"
---

# DF-SUMMA-2026-0028 — Entry references, settlement and explanations

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-ACC, INV-SET and INV-FX were mostly met by the ledger, invoicing,
payments and credits work, but four gaps remained:
- the issue entry named the invoice's number, not its id (INV-ACC-002);
- nothing explained an entry in business language (INV-ACC-006);
- an invoice showed only its live payments, not its whole settlement
  (INV-SET-006);
- a draft with an amount in another currency was refused only by an
  exception (INV-FX-002).

## Decisions

1. **A structured reference on the entry.** `PostedEntry.Reference`
   (`{ Kind; Id }`) names the record an entry was posted for. Issuing
   sets it to the invoice's id.
   - It is stored as `postedFor`, written only when present. An entry
     that has one is written at schema 2. Other entries keep their schema 1
     bytes, and an older Summa refuses, rather than misreads, an entry it
     cannot represent (the existing schema mechanism).
   - `Source` and `Description` are unchanged, so no reader parses
     text.
   - The key is `postedFor`, not `reference`, because other record
     bodies already use `reference` and must not change schema.
   - Only issuing sets it so far. Other posters may adopt it additively.
2. **`Payments.settlement`** lists every event that settled or unsettled
   an invoice, in date order, each with its effect and what was owed
   after: payments applied, payments reversed, credits, deposits and memos
   applied, write-offs, and a void.
   - The last balance is the derived outstanding amount, and tests assert
     it.
   - The invoice's Payments tab shows the list, with "Owed after".
3. **`Explanations.entry`** explains an entry from the entry and the
   chart alone:
   - why it exists (what it was posted for, or what it reverses);
   - a sentence per line, by account type and side;
   - that debits equal credits.

   The journal entry page shows it under "In plain words".
4. **One currency per invoice.** `validateDraft` checks every amount's
   currency first: lines, fixed discounts and adjustments. Any difference
   refuses with `MixedCurrency` (blocker `mixed-currency`) and
   computes no total. A line in another currency was previously
   mislabelled as an invalid discount, and then threw while the total was
   computed. That is an escaped defect from WI-0027 (PR #38, merge
   10bd17b).

## Consequences

- INV-ACC, INV-SET and INV-FX are tested, each requirement by a named
  test.
- Signatures:
  - `PostedEntry` gains `Reference`;
  - `InvoiceProblem` gains `MixedCurrency`;
  - new: `Ledger.referTo`, `Payments.settlement`, `Explanations`.
