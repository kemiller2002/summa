---
id: DF-SUMMA-2026-0043
title: The draft editor keeps what it does not show, refuses a stale save, and edits line details and typed references
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
  - research/decisions/DF-SUMMA-2026-0029--invoice-data-line-identity-details-references-rounding.md
  - research/decisions/DF-SUMMA-2026-0014--books-on-github-through-arca.md
tags: [invoicing, editor, interface, data]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T18:11:17.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record how the draft editor saves (WI-0029 slice 23)"
---

# DF-SUMMA-2026-0043 — The editor keeps what it does not show

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

The draft editor shows a few things: the customer, each line's
description, quantity, rate and taxable mark, one tax, the purchase order
and the note to the customer. A draft can hold much more:

- per line: its source, rate provenance, project, work item, discount and
  details;
- per draft: terms, due date, discounts, engagement, recipients,
  assumptions, the client's reference, the service period, internal notes
  and typed references.

A proposal, an agent or a recurring template can set any of these.

Saving from the editor built a new draft from the form alone, so all of
that was lost. A line that came from published time or an expense was
saved as a hand-priced manual line. The save also wrote over whatever
version was stored, so a change someone made after the draft was opened
was silently undone.

The editor also had no inputs for line details or typed references
(INV-DATA-008, INV-DATA-014), and the printed invoice did not show them.

## Decisions

1. **A save overlays the form on the draft as stored.** Each row the editor
   opened remembers which stored line it came from. Saving replaces only
   what the editor shows:
   - on that line: description, quantity, unit price, taxable mark and
     details;
   - on the draft: customer, purchase order, note, typed references and the
     one tax.

   Everything else is kept as stored. A row added in the editor is a new
   manual line, as before.

   A line whose stored tax treatment is not "taxable" (for example exempt)
   keeps it unless the person marks the line taxable. Unmarking a taxable
   line makes it not assessed.
2. **A save made on an older version is refused.** The form records the
   draft's version when it was opened. If the stored draft has a newer
   version, the save is refused, nothing is written, and the person is
   told:

   > Someone else changed this draft after you opened it. Discard your
   > changes to see theirs, then make yours again.

   After a successful save, the editor holds the draft as stored, at its new
   version. The check holds when the command is decided again on newer
   books (DF-SUMMA-2026-0014).
3. **Line details are edited in one panel.**
   - Each line has a "Details" button, whose label says when details are
     given. It opens a single details panel below the lines for that line.
   - The fields are detailed description, unit, service period (first and
     last day), category and customer code.
   - One panel serves every line, so a 60-line invoice binds six detail
     inputs, not 360. A first version put a disclosure with all six inputs
     in every row. With 60 lines, the .NET WASM runtime then reported
     "Zero page optimizations are enabled but garbage appeared in memory" on
     save (pdf.spec.js). Removing the per-row inputs, with the same view
     values, made the report go away.
   - Empty fields are absent, and a line with none has no details.
   - A service period needs both days, as dates, with the first on or
     before the last. Otherwise the save is refused with the reason.
4. **Typed references are rows.** Each row has a kind (contract,
   engagement, vendor, cost center, matter) and a value.
   - A kind may be used any number of times, as the data model allows.
   - An empty row is not saved.
   - An unknown kind is refused with the reason.
   - Kinds are named by `Invoicing.clientReferenceKinds`, which storage and
     the editor share.
5. **The printed invoice shows them only when given.** After the purchase
   order it shows the client's reference, the service period and each typed
   reference. Under each line it shows that line's details.
   - `Documents.referenceFacts` and `Documents.lineDetailsLines` are shared
     by the HTML document and the printed document.
   - An invoice without them prints exactly as before (the print captures
     are byte-identical).
6. **Kept unsaved forms stay readable.** A form kept in the tab before this
   change has no details, references or base version, and it is read with
   none of them rather than dropped.

## Consequences

- INV-DATA's editor note is closed.
- INV-UI-007 gains refusal of conflicting saves. Showing both versions so a
  person can reconcile them remains, and so does showing a line's source in
  the editor.
- `DraftEditorTests` covers:
  - keeping what the editor does not show;
  - the stale save;
  - entering details and references;
  - refusals;
  - an older kept form;
  - the printed values.
- `editor.spec.js` covers entry through to the issued invoice in a browser.
