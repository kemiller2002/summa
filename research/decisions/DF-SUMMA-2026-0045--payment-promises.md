---
id: DF-SUMMA-2026-0045
title: Payment promises are follow-up, judged kept or missed from the books
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
tags: [receivables, follow-up, operations]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T20:00:30.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record payment promises (WI-0029 slice 26, SUM4-016, INV-OPS-006)"
---

# DF-SUMMA-2026-0045 — Payment promises

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM4-016 asks Summa to record payment promises. INV-OPS-006 lists "Payment
promise missed" among the obligations the work queue shows. SUM4-014 and
SUM4-015 ask for the promise-to-pay date and a "payment promised" status
in follow-up.

Follow-up (disputes, reminders) is operational: it never changes an
invoice or the ledger (INV-STATE-006).

## Decisions

1. **A promise is part of an invoice's follow-up.**
   - `FollowUp.Promise` holds the amount, the date it is promised by, the
     day it was made, what the invoice owed then, and a note on who
     promised and how.
   - It is stored as `promise` on `summa.follow-up`, written only while
     there is one; the key is a schema-2 mark.
   - The invoice, its balance and the ledger are never changed by it.
2. **Recording validates against the books.** A promise is refused when:
   - nothing is owed (paid, written off or voided), and in that case this
     is the only reason given;
   - the amount is not positive, is more than is owed, or is in another
     currency;
   - the date is before the day it is recorded.

   Each refusal is a sentence. A new promise replaces the current one.
   Recording and withdrawing are audited (`payment-promise-recorded`,
   `payment-promise-withdrawn`), so earlier promises stay in the
   invoice's history.
3. **Kept or missed is worked out, never typed.**
   - A promise is **kept** once what the invoice owes has fallen by at
     least the promised amount since it was made. Any reduction counts: a
     payment applied, a credit, a credit memo or a write-off.
   - It is **due** until its date.
   - It is **missed** after its date if not kept. A late payment that
     covers it makes it kept.
   - No status is stored, so it can never disagree with the books.
4. **Where it shows.**
   - On the invoice's follow-up: "Promised 250.00 USD by December 20, 2026
     (Jane, AP, by phone): due.", with record and withdraw.
   - In the follow-up list, beside reminders and disputes.
   - In the work queue, a missed promise appears as "Payment promise
     missed" with the invoice's address.
   - Recording and withdrawing are book commands that need `RecordPayment`,
     the same as reminders and disputes.

## Consequences

- INV-OPS-006 is complete. INV-OPS is classed tested, since every
  obligation its text lists is in the work queue.
- SUM4-016 is **partial**:
  - one current promise per invoice;
  - for the whole invoice, not a schedule of instalments;
  - no promise spanning a customer's several invoices.
- SUM4-014 and SUM4-015 stay partial for the contact fields and the
  escalated status.
- The tests are:
  - `LifecycleTests` "SUM4-016": recording, refusals, and due, kept and
    missed;
  - `FinancialRecordsTests`: the round trip, schema and bytes;
  - `AttentionTests`: the invoice, the follow-up list, the work queue and
    withdrawal, plus the book command;
  - `attention.spec.js`: recording and withdrawing in a browser.
