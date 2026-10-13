---
id: DF-SUMMA-2026-0039
title: The Chrona import preview and accept, served on the Proposals page
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
  - research/decisions/DF-SUMMA-2026-0021--sensitive-data-integration-states-and-retention.md
tags: [import, chrona, preview]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T15:03:43.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the served import preview (WI-0029 slice 18)"
---

# DF-SUMMA-2026-0039 — The Chrona import preview, served

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM3-025 asks that imports never mutate records without validation:
import, parse, validate, preview, accept, apply. It also asks for a
summary of what was accepted, rejected, duplicated and warned about.

`ChronaImport.preview` did this in the domain, but the application
offered no way to use it.

## Decisions

1. **On the Proposals page, by paste.**
   - Imported time feeds proposals, so the import sits beside them.
   - A person pastes the batch Chrona exported, one canonical message per
     line. Paste needs no file pack and no new route.
2. **Parse, validate and preview, then accept.**
   - Each line is decoded by the contract's codec. A line that cannot be
     read is rejected by its line number.
   - The readable messages are previewed on the books as they stand.
   - The page lists the accepted, rejected, duplicate and warning entries,
     with a summary line. Nothing changes until the person accepts.
3. **Apply only what was previewed.**
   - Accepting needs the ImportSourceTime capability.
   - It previews the same text again on the books as they stand, and
     applies only if the result is the preview the person saw. Books that
     changed since, for example another tab importing the same time, give
     a different preview: the accept is refused and the person previews
     again.
   - A batch that changes nothing is refused rather than committed.

## Consequences

- The import arrives by paste. A file picker for an exported file, and an
  automatic feed from Chrona, are later decisions.
