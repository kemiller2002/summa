---
id: DF-SUMMA-2026-0041
title: Financial exports are sensitive files; export schemas 2 add their classification
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
  - research/decisions/DF-SUMMA-2026-0030--invoice-exports-and-the-large-invoice-scenario.md
tags: [exports, security, encryption]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T15:37:43.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record sensitive export handling (WI-0029 slice 20)"
---

# DF-SUMMA-2026-0041 — Financial exports are sensitive files

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM3-024 (v0.3 §24) asks for encrypted transport, encrypted backups and
encrypted storage where supported. It also says *"Financial exports should
be handled as sensitive files."*

Transport, backups and GitHub's storage were covered. Exports were offered
to anyone with access to the books, and said nothing about their
sensitivity.

## Decisions

1. **Only a role that may export makes an export.**
   - On GitHub books, a CPA CSV or an invoice JSON or HTML needs the
     `ExportData` capability.
   - Without it, the buttons are not shown, and a request is refused with
     a reason.
   - Local books have one owner, as for every other capability.
   - The PDF is the customer's own document, printed by the browser, and is
     not treated as a data export.
2. **Each file says what it is.**
   - CSV exports gain a `# Classification: confidential financial data`
     header line, so their schema becomes `summa.export/2`.
   - The invoice JSON gains `"classification"` and the HTML comment
     `classification=`, so their schema becomes
     `summa.invoice-export/2`.
   - Both schemas change when their header or envelope does, by their own
     rule (DF-SUMMA-2026-0020, DF-SUMMA-2026-0030).
   - The canonical customer document inside is unchanged, and so are its
     golden vectors.
3. **Nothing is kept.** An export goes only to the browser's download. It
   is not written to the books, to the tab or to browser storage. A test
   asserts that the download is the only effect.
4. **No customer data in file names.** Files are named by kind and year,
   or by invoice number.

## Consequences

- Earlier decision records keep the versions they named. This record
  supersedes those versions.
- A CPA reading an older `summa.export/1` file still reads the same
  columns; only the header line was added.
- **SUM3-024 stays partial** (coordinator's ruling on #85), with two
  remainders:
  1. **Encrypted storage where supported.**
     - A local or demo deployment keeps its books unencrypted in this
       browser's localStorage.
     - Browsers support app-level encryption at rest (WebCrypto with a
       user-held key over IndexedDB), so this is not met.
     - It needs a key-handling design, brought for approval before
       building: WI-0051.
  2. **Encrypted backups.**
     - `Summa.Operations.Backup` seals the organization folder with
       AES-256-GCM, but only a financial migration takes one today.
     - No routine backup is produced; scheduling it is SUM3-006.
     - Summa produces no export of the books themselves, only the CPA and
       invoice exports this record covers.
  GitHub encrypts GitHub-backed books.
