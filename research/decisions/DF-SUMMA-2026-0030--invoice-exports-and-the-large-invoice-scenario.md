---
id: DF-SUMMA-2026-0030
title: Invoice exports that say what they are, and the defined large-invoice scenario
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
  - research/decisions/DF-SUMMA-2026-0020--command-telemetry-exports-and-business-dates.md
  - research/decisions/DF-SUMMA-2026-0029--invoice-data-line-identity-details-references-rounding.md
tags: [exports, performance, portability]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T12:09:51.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record invoice export and scale decisions (WI-0029 slice 10)"
---

# DF-SUMMA-2026-0030 — Invoice exports and the large-invoice scenario

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-EXP asks for invoice exports in CSV, versioned JSON, HTML and PDF,
each saying what it is, with no lock-in. Summa exported only the trial
balance and the journal. INV-PERF asks for a defined, tested large-invoice
scenario. INV-INTL asks that the locale change presentation only, and that
no document claims compliance.

## Decisions

1. **Invoices CSV.** `Reports.invoicesCsv` writes one row per invoice:
   the issued facts and the balances derived now (paid, written off,
   outstanding, status), plus template, rounding and journal entry.
   - The CPA workspace offers all invoices issued in the year, under the
     self-describing header of DF-SUMMA-2026-0020.
2. **One invoice as JSON or HTML.** `Exports.invoiceJson` wraps the
   canonical customer document, unchanged, in `summa.invoice-export/1`.
   The envelope carries:
   - when it was made;
   - the books' data version;
   - the invoice id, number and version;
   - its record's schema version;
   - the template and the rounding policy.

   `Exports.invoiceHtml` puts the same facts in a comment before the
   canonical HTML. The invoice page offers both.
   - The stored artifacts are not touched: they are deterministic and
     hash-checked, so the export's time lives only in the export.
   - PDF remains the browser print of the Folio document (INV-DOC-002).
3. **The large-invoice scenario** is 2,000 lines over 40 projects
   (`InvoiceScaleTests.largeDraft`).
   - It must issue, post one revenue line per invoice line, render to HTML
     and JSON, and total exactly.
   - It takes about 0.1 s in the tests. The bound is set at 20 s so that
     slow CI cannot make it flaky.
4. **Locale and compliance.** The document and its JSON must be identical
   under any culture, and must contain no claim of tax or e-invoicing
   compliance. Tests assert both.

## Consequences

- INV-EXP and INV-PERF are tested.
- INV-INTL is partial: jurisdiction-specific legal fields (001) remain.
