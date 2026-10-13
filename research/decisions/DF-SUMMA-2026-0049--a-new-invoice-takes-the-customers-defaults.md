---
id: DF-SUMMA-2026-0049
title: A new invoice takes the customer's defaults, shown on the editor; nothing typed is replaced
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
  - research/decisions/DF-SUMMA-2026-0043--the-editor-keeps-what-it-does-not-show.md
tags: [interface, invoicing, defaults]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-13T22:31:04.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record smart invoice defaults (WI-0044, SUM4-005)"
---

# DF-SUMMA-2026-0049 — A new invoice takes the customer's defaults

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §5 says that choosing a customer should propose the billing address,
email, payment terms, default revenue account, engagement or project, and
billing rate where one applies. "The objective is to minimize repetitive
input while keeping the final invoice review explicit."

## Decisions

1. **One domain function proposes the defaults.**
   `Billing.invoiceDefaults books systemTerms customerId engagementId`
   returns:
   - the customer's billing name, address and email;
   - the customer's engagements;
   - the engagement asked for, or the only one when there is exactly one;
   - the terms, resolved by `resolveTerms`, so engagement terms come
     before the customer's and the organization's;
   - the rate, resolved by `resolveRate` from the rate card for that
     customer and engagement.

   An unknown customer proposes nothing. When no rate applies, no rate is
   proposed and none is invented.
2. **The editor shows what is proposed, so the review stays explicit.**
   Under the customer it shows the bill-to details, the organization's
   revenue account, and where the rate came from, or says that no rate is
   set. When the customer has engagements, it offers an engagement select
   ("No engagement" first). The engagement chosen is saved with the draft
   and survives a refresh.
3. **Defaults fill only what is empty, and only on a new invoice.**
   Choosing a customer or an engagement sets the rate on lines whose rate
   is still empty. A rate the person typed is never replaced. An opened
   draft keeps what it holds: changing its customer applies no defaults.
4. **The billing details are not copied into the form.** Issuing already
   snapshots the customer's billing name and address onto the invoice
   (`Invoicing`), so the editor shows them and does not keep a second copy
   that could drift.
5. **The revenue account is the organization's.** Summa has no
   per-customer revenue account. The editor shows the organization's
   revenue account (`manifest.accounting.revenueAccount`) as the one the
   invoice will post to.
6. **Choosing a project is out of scope for a manual invoice.** Summa holds
   no project records. Projects are classifications on time entries
   imported from Chrona. Invoices made from time carry their projects
   through proposals (SUM4-007), where project rates already resolve. A
   manual invoice proposes an engagement, not a project.

## Consequences

- SUM4-005 is tested:
  - `InvoiceDefaultsTests`: billing details, terms precedence, rate
    resolution, the only engagement, several engagements, and no rate;
  - `SmartDefaultsTests`: rates filled only when empty, the engagement
    chosen when it is the only one, choosing an engagement, an opened draft
    left alone, and the no-rate note;
  - `editor.spec.js`: the bill-to text, the rate note, and the engagement
    select surviving a refresh.
