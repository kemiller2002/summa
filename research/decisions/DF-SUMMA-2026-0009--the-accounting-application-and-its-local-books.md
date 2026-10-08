---
id: DF-SUMMA-2026-0009
title: The accounting application and its local books
status: accepted
version: 1.2.0
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
      operations: [created, modified]
      at: 2026-10-08T18:46:56.261Z
      last: 2026-10-08T21:10:08.225Z
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
6. **A credit memo is issued from the invoice it corrects** (slice 2).
   - Issuing and applying it to that invoice is one transition: if applying
     fails (more than is owed), nothing is issued.
   - Its customer document (`CreditMemoDocuments`, template
     `summa.credit-memo` 1.0.0) takes its parties from the credited
     invoice's issued snapshot, so it is reproduced from the records alone.
   - A memo with no invoice has no issued snapshot of its parties, and so no
     document. The application does not issue one; the domain still allows it.
7. **Local books name their credit accounts by the default chart.** The
   manifest names cash, receivables and revenue, but not customer credits,
   deposits or bad debt. `LocalSnapshot.receivableAccounts` uses the default
   chart's 2100, 2200 and 6500, which every local organization starts with.
   Books on GitHub (WI-0037) need the manifest to name them.
8. **A select whose options come from the books marks its chosen option.**
   The kernel cannot set a select's value before its options exist. Each
   option therefore carries `selected`, so a link that names a customer
   opens with that customer chosen.
9. **The books' screens use Summa's own reports** (slice 3). The ledger,
   trial balance, balance sheet and income statement (accrual, or
   `Periods.cashBasis` for cash) are computed by `Summa.Ledger`, never in the
   page. Every account, date and basis is in the link.
10. **An entry is traced to its origin by the id it was posted under.** Each
    invoice, allocation, credit memo, application and write-off records its
    journal entry id, and the entry page links back through it, rather than
    parsing the entry's source text.
11. **What blocks a period close.** A trial balance that does not balance at
    the month's end, or a payment received in the month that is not fully
    applied, blocks closing: the page lists the blockers and the close
    command refuses. Unissued drafts are reported but do not block, because
    they post nothing.
12. **Reopening a period is permitted in local books**, whose single person is
    their owner. With sign-in (WI-0035), reopening needs the `ReopenPeriod`
    capability.
