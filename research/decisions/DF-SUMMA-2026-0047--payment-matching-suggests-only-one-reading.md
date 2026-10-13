---
id: DF-SUMMA-2026-0047
title: Payment matching suggests an application only when one reading fits; otherwise a person chooses
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
tags: [payments, receivables, matching]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-13T21:05:40.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record payment matching (WI-0044, SUM4-012)"
---

# DF-SUMMA-2026-0047 — Payment matching

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §12 gives an example. When a customer owes 5,000 and 2,500 and a
5,000 payment arrives, Summa should suggest applying it to the first.
"If multiple interpretations are plausible, the system should not guess
silently."

The payments inbox's "Apply to oldest invoices" applied any unapplied
payment oldest due first. That is exactly the silent guess the
requirement rules out.

## Decisions

1. **`Matching.suggest` reads a payment's unapplied amount against its
   customer's open invoices.** Open means not voided and still owing,
   earliest due first. The readings are tried in this order:
   - the invoices the payment's reference names by number: one, or
     several that together owe exactly the amount;
   - the one invoice that owes exactly the amount;
   - all the open invoices, when the amount is exactly what they owe
     together;
   - the only open invoice, up to what it owes.
2. **Several readings, or none, is a choice for a person.**
   - Two invoices owing the same amount means Summa names them both.
   - An amount no reading explains means Summa names every open invoice.
   - The reason is given in words in both cases.
3. **A suggestion applies nothing.**
   - "Apply as suggested", in the inbox and on the payment's page, applies
     the suggestion as it stands when clicked. If a person must choose by
     then, it refuses: "Summa does not guess which invoice this pays: …".
   - On the payment's page a person can always choose an open invoice. The
     payment is applied up to what that invoice owes.
   - Every application is an ordinary allocation, so it is audited,
     posted and idempotent.
4. **The oldest-first application is removed.**

## Consequences

- SUM4-012 is tested:
  - `MatchingTests`: the requirement's example, equal amounts, no
    reading, a reference naming an invoice, the whole balance, the only
    invoice, nothing open, and that a suggestion applies nothing;
  - `AttentionTests` "SUM4-012";
  - `attention.spec.js`.
- The inbox test that asserted oldest-first application was rewritten for
  this behaviour. That was a deliberate change of behaviour, not a
  loosened test.
