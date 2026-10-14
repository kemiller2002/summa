---
id: DF-SUMMA-2026-0052
title: Ordinary workflows speak business language; each posting offers its entry, and each entry says why it was posted
status: accepted
version: 1.0.0
created: 2026-10-14
updated: 2026-10-14
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0048--high-impact-actions-ask-first.md
tags: [interface, accounting, language]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-14T00:23:30.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record accounting on demand (WI-0044, SUM4-021, SUM4-033, SUM4-034)"
---

# DF-SUMMA-2026-0052 — Accounting on demand, in business language

- **Date:** 2026-10-14
- **Status:** accepted
- **Work item:** WI-0044

## Context

- v0.4 §21 asks every generated journal entry to explain itself: "Why was
  this posted? Invoice INV-1023 was issued for $6,050. Debit Accounts
  Receivable $6,050 because the customer now owes the business…". The
  explanation should be available on demand.
- §33 asks ordinary workflows to use business language and not require
  debit, credit, control account or journal posting. Those terms stay
  available in ledger and audit contexts.
- §34 asks for progressive disclosure: "Invoice issued successfully" with
  an optional "View Accounting Entry".
- INV-ACC-006 already explained entries line by line on the journal entry
  page.

## Decisions

1. **Every entry says the business event it records.**
   `PostedFor.why receivables entry` reads the entry's source and the
   books. For example:
   - "Invoice EF-2026-0001 was issued to ABC Corp for 6,050.00 USD."
   - "ABC Corp paid 1,000.00 USD (ACH 4471), and it was applied to what
     they owe: …"

   There is a sentence for each generated kind: invoice, payment,
   customer credit, deposit, credit memo, credit applied, refund, payment
   reversal, write-off, expense, reversal, opening balances, adjustment
   and year-end close. Anything else falls back to its description.
2. **Each line takes the requirement's form.** For example, "Debit
   Accounts Receivable (1100) 6,050.00 USD for EF-2026-0001, because the
   business owns, or is owed, that much more." The reason comes from the
   account type and side. The explanation ends by saying that debits and
   credits are equal.
3. **On demand.** The explanation is on the journal entry page under "Why
   was this posted?". That page is reached only by asking: the "View
   accounting entry" link, the invoice's History tab, the ledger or
   search.
4. **After posting, the entry is one link away.** Issuing an invoice
   (directly or from a proposal), recording a payment and issuing a credit
   memo show their business notice ("Invoice … issued.") with a "View
   accounting entry" link to the entry just posted. The link belongs to
   that notice and disappears with it.
5. **Business language in ordinary workflows.** The confirmations for
   issuing an invoice, approving a proposal and issuing a credit memo
   now say "recorded in the books", not "posted". A test reads the
   page's visible text and fails if "debit", "journal", "posted",
   "posting" or "control account" appears outside the accounting
   contexts. Those contexts are the ledger, journal entry, reports
   (trial balance, income statement, balance sheet), periods, the CPA
   workspace, and the invoice's History tab, which is its audit trail.
   "Credit" is not on the list, because "credit memo" and "available
   credit" are business language.

## Consequences

- SUM4-021, SUM4-033 and SUM4-034 are tested:
  - `InvoiceAccountingTests`: an invoice, a payment and a reversal
    explained;
  - `AccountingOnDemandTests`: the entry link after issuing, after a
    payment and after a credit memo; the link disappears with its
    notice; the explanation's form; the page-language guard and the
    confirmations;
  - `editor.spec.js`: issuing, following "View accounting entry", and
    reading why it was posted.
