---
id: DF-SUMMA-2026-0034
title: Architectural ownership is asserted by tests where each owner shows
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
  - research/decisions/DF-SUMMA-2026-0033--reads-pinned-to-the-observed-commit.md
tags: [architecture, ownership, conformance]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T13:34:41.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record how INV-ARCH is asserted (WI-0029 slice 13)"
---

# DF-SUMMA-2026-0034 — Architectural ownership asserted by tests

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-ARCH-001 to 010 say who owns what:

- Summa owns the invoice's financial state;
- Chrona and the other source applications own their source facts;
- Ordo's principles govern transitions;
- Praxis governs engineering;
- Limen owns the browser boundary;
- Forma owns interactive presentation and Folio owns print;
- GitHub is authoritative;
- large binaries stay outside it.

Most of this was already true. None of it was asserted under the requirement's name.

## Decisions

1. **Assert each requirement where its owner shows.** No new mechanism.
   - Record-level facts are tested on the records and the provider (001,
     009, 010).
   - Source ownership and transitions are tested in the domain (002, 003,
     004).
   - Repository and page composition are tested on the files (005 to 008).
2. **"No external system may mutate" (001)** is enforced as detection.
   - GitHub cannot stop a person with write access from editing a file.
   - So an issued invoice rewritten outside Summa is an integrity failure
     that blocks, through the existing audit.
   - It is not presented as prevented.
3. **Source facts Summa itself records** (expenses, engagements,
   milestones) are not claimed as another application's. INV-ARCH-003 is
   asserted for Chrona time, the external source Summa consumes. Expenses
   and engagements are Summa's own records until a source application
   for them exists.
4. **Ordo (004)** is asserted as the principles in Summa's own domain.
   - The principles are: state derived from facts, actions equal to legal
     transitions, obligations, version checks and audit evidence.
   - Summa does not run an Ordo engine at run time. Ordo is a host tool
     here (`docs/ordo-observation.md`).
5. **Limen (006)** is asserted by what the browser runs. The page's only
   script starts the shared kernel, the kernel does no arithmetic on
   amounts, and the rules are the F# engine's.

## Classification

Checked against each requirement's full text, the row is **partial**.

- **Fully asserted:** 002, 005, 006 and 010.
- **Narrower than the text:**
  - 001: detection, not prevention, of outside mutation.
  - 003: only Chrona time has an external owner.
  - 004: explicit effects and no-bypass of the commands are not asserted.
  - 007: the app's tables are not Forma's data grid, and it has no Forma
    dialog or flyout.
  - 008: deterministic PDF output is not asserted.
  - 009: the deployment configuration is not in the data repository, and
    local books live in the browser.

Each remains open under WI-0029.

## Consequences

- If a later change moves an owner's responsibility (for example, browser
  code computing a total, a PDF stored in a record, or Forma gaining
  Summa terms), a test named for the requirement fails.
