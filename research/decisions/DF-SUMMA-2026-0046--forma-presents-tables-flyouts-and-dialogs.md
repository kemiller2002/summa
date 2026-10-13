---
id: DF-SUMMA-2026-0046
title: Forma presents every screen table as its data grid, line details as its flyout, and the new-books confirmation as its dialog
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
  - research/decisions/DF-SUMMA-2026-0034--architectural-ownership-asserted-by-tests.md
  - research/decisions/DF-SUMMA-2026-0043--the-editor-keeps-what-it-does-not-show.md
tags: [interface, forma, architecture, accessibility]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T20:25:31.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Adopt Forma's data grid, flyout and dialog (WI-0029 slice 27, INV-ARCH-007)"
---

# DF-SUMMA-2026-0046 — Forma's data grid, flyout and dialog

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-ARCH-007 asks that the invoice interface be presented by Forma.
DF-SUMMA-2026-0034 classed it partial: the page used Forma's fields,
buttons, status and faults, but its tables were plain tables, and it used
no Forma dialog or flyout.

Forma 0.6.1 provides:

- `.ef-data-grid`, a labelled, keyboard-scrollable region around a table.
  Each cell's `data-label` lets the grid show each row as a card on narrow
  screens.
- `.ef-flyout` and `.ef-dialog`, both native `<dialog>` elements that the
  browser opens and closes through `commandfor` and `command`, with no
  script.

## Decisions

1. **Every table on screen is Forma's data grid.**
   - There are 41 such tables. Each is wrapped in
     `<div class="ef-data-grid" role="region" tabindex="0" aria-label="…">`,
     labelled by its caption, and every cell has the `data-label` of its
     column, footers included.
   - The tables of the printed invoice, the credit memo and the issue
     preview are inside `<ef-print-table>` and stay Folio's, so print is
     unchanged (the captures are byte-identical).
   - A table hidden in print is hidden with its frame: `summa-screen-only`
     goes on the grid, not on the table inside it. The credit memo's
     "Applied to" grid first printed its border because of this. The print
     comparison caught it, and `ArchitectureTests` now forbids it.
2. **Line details open in Forma's right-hand flyout.**
   - The row's "Details" button opens it natively
     (`command="show-modal"`) and tells the engine which line it shows.
   - The flyout's close button, "Done" and Escape all close it natively.
     The dialog's `close` event tells the engine.
   - Modality, focus, inertness and focus return belong to the browser. The
     engine decides what the flyout shows.
   - This keeps one details panel for every line (DF-SUMMA-2026-0043).
3. **Starting new books is confirmed in Forma's dialog.**
   - Replacing damaged books in this browser cannot be undone. "Start new
     books…" opens a dialog that says so.
   - Only its "Start new books" button sends the reset. "Keep the damaged
     books" closes it and changes nothing.
4. **No control sits inside a label hidden for screen readers.** Forma's
   `.ef-visually-hidden` clips everything inside it. The editor's line
   fields and the reference rows were inside such labels, so on screen they
   were one pixel. This defect was found by looking at the page while
   making this change. Their labels now hide only their text.
   `ArchitectureTests` forbids the pattern, and `editor.spec.js` checks
   that a line's fields are hit where they are drawn.

## Consequences

- INV-ARCH-007 is tested.
- INV-ARCH stays partial for 001, 003, 004, 008 and 009.
- `ArchitectureTests` asserts:
  - every screen table is a labelled data grid with labelled cells;
  - the flyout and the dialog are present;
  - no control sits inside a hidden label.
- The browser tests drive the flyout (open, Done, Escape) and the dialog
  (cancel, then confirm).
- The screens change in appearance: tables gain Forma's grid frame and its
  narrow-screen cards. There are no screen baselines; screenshots were
  reviewed by hand. Printed documents are unchanged.
