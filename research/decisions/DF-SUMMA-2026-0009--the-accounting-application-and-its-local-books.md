---
id: DF-SUMMA-2026-0009
title: The accounting application and its local books
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
  - research/decisions/DF-SUMMA-2026-0007--invoice-documents-artifacts-and-issuance.md
tags: [web, limen, forma, folio, storage]
provenance:
  contributions:
    EXE-20261008T184655871Z-69753eb4:
      operations: [created]
      at: 2026-10-08T18:46:56.261Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose how the accounting application runs before sign-in and the GitHub store"
---

# DF-SUMMA-2026-0009 — The accounting application and its local books

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0030

## Context

The product UI (v0.4) needs a page that works before sign-in (WI-0035) and the GitHub store (WI-0037) exist. GitHub Pages (WI-0039) can only serve such a page.

## Decisions

1. **One more Limen page.** The accounting application is `app/`, beside the backlog (`web/`) and hub (`web-hub/`) tools.
   - It has its own engine (`Summa.Web.Engine.Accounting`) and wire (`AccountingWire`), and its own WASM export (`DispatchAccounting`).
   - The engine decides only through Summa's domain commands: readiness, issue, payment and allocation, and the storage format's checks. It never re-implements them.
2. **A deployment without a data location keeps its books in the browser.**
   - Limen's core Storage effect holds a snapshot of the books: the organization manifest and every financial record as canonical text, under the paths Arca would use (`Summa.Storage.LocalSnapshot`).
   - Reading the snapshot back runs every record and invariant check. Books that fail are refused, never used; the person may only start new books in their place.
   - There is one local organization and one local person. Sign-in (WI-0035) and the GitHub store (WI-0037) replace these, with the same engine commands.
3. **A new browser starts demo books.** These have the default chart and demo company details, so the first invoice can be issued at once. The company details can be edited in Settings.
4. **Printing** uses Summa's `summa.print` capability pack, which opens the browser's print dialog for the invoice's Folio document. This is Folio's portable profile (P0), as DF-SUMMA-2026-0007 records. Recording the resulting PDF as an artifact follows with the artifact store.
5. **Every value and event is held to the engine.** A binding test checks both directions, and the browser suite drives the real page.
