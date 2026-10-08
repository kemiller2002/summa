---
id: DF-SUMMA-2026-0007
title: Invoice documents, artifacts, numbering and the issue command
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
  - research/decisions/DF-SUMMA-2026-0005--discounts-voids-and-pre-release-record-schemas.md
tags: [invoicing, documents, folio, artifacts, numbering]
provenance:
  contributions:
    EXE-20261008T172746682Z-be397865:
      operations: [created]
      at: 2026-10-08T17:46:20.342Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose how invoice documents, artifacts, numbering and issuance work"
---

# DF-SUMMA-2026-0007 — Invoice documents, artifacts, numbering and the issue command

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0027

## Context

- v0.1 §7, set 0 §0.35-0.37, v0.3 §30 and the invoice-generation requirements (INV-DOC, INV-ISS, INV-NUM, INV-DRAFT, INV-REV) ask for a customer document that never changes once issued, with artifacts kept outside the financial records.
- Folio 0.3.0 provides passive light-DOM print components and `print.css`, used through the portable browser profile (P0). It does not yet ship the deterministic server-side renderer (P2) that an application could call to produce a PDF.

## Decisions

1. **The issued invoice is the snapshot.**
   - It stores its lines and totals, terms and their source, and details (purchase order, client reference, service period, customer and internal notes).
   - It also stores the issuer and customer as they were at issue, the template (id, version, Folio profile), and who issued which draft version.
   - The document is derived from this snapshot alone.
2. **HTML and JSON are reproduced, not stored.**
   - Template `summa.invoice` 1.0.0 renders semantic HTML with `ef-print-document`, a repeating `ef-print-header` and `ef-print-footer`, `ef-print-table`, `ef-print-keep` and `ef-print-page-number`.
   - Folio owns pagination. Summa owns only the content and its order.
   - `summa.invoice-document` version 1 is the machine-readable form: fixed member order and no whitespace.
   - Issuing records each document's SHA-256 and size as a `summa.artifact`. An invariant re-renders every issued document and fails the books if a hash no longer matches.
   - A released template version is never changed; a new look is a new version. v0.3 §30 allows this in place of keeping the rendered snapshot. The reproduction is proven on every load, and it keeps the repository free of rendered copies.
3. **The PDF comes after issue.**
   - Issuing creates a `Pending` PDF artifact. The PDF is printed from the same HTML through Folio, stored in the environment's artifact storage, and recorded with its reference, SHA-256, size and renderer.
   - If it fails, the invoice stays issued and the artifact reads `Failed`. That artifact is the obligation to try again (INV-DOC-015).
   - Recording a different file for a generated PDF is refused, so a retry never makes a second document.
   - When Folio ships a P2 renderer, a server can fill these artifacts without any change to the records.
4. **Numbering.**
   - A policy sets the prefix (the series), the scope (yearly or continuous) and the number of digits.
   - The number is assigned inside the issue transition. It is one more than the highest number ever issued in the series, voided invoices included, so numbers are never reused.
   - Gaps are allowed (INV-NUM-008).
   - Two concurrent issues cannot share a number: the change-token commit makes the second decide again.
   - Choosing a number needs the `OverrideInvoiceNumber` capability, which is gated for a person's approval, and the number is checked for collisions.
5. **Drafts, review and issue.**
   - Every saved change increments the draft's version, and a save must name the version it changes. A stale save is refused rather than overwriting newer work.
   - Submitting for review checks readiness. Every blocker has a code, an explanation and a resolution. A change after submission returns the draft to Editing.
   - Issuing requires the submitted version. The person issuing, holding `IssueInvoice`, is the approval. The invoice records who issued it, when, which draft version and the correlation id. An agent can only prepare the issue (INV-REV-005).
6. **Defaults come from the organization manifest.** The prefix, the default terms, the issuer and payment instructions, and the receivables account (resolved from its code) fill the issue request.
