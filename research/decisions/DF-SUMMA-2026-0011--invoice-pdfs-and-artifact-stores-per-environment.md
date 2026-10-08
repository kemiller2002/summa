---
id: DF-SUMMA-2026-0011
title: Invoice PDFs in the application and an artifact store per environment
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
  - research/decisions/DF-SUMMA-2026-0007--invoice-documents-artifacts-and-issuance.md
  - research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md
  - docs/deployment-configuration.md
tags: [artifacts, pdf, folio, environments, limen]
provenance:
  contributions:
    EXE-20261008T184655871Z-69753eb4:
      operations: [created]
      at: 2026-10-08T21:33:25.037Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide how the application stores invoice PDFs and isolates artifact stores per environment (WI-0030 slice 4)"
---

# DF-SUMMA-2026-0011 — Invoice PDFs in the application and an artifact store per environment

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0030 (slice 4)

## Context

DF-SUMMA-2026-0007 made the PDF a post-issue artifact. It is printed through Folio's portable profile (P0), stored in the environment's artifact storage, and recorded with its reference, SHA-256, size and renderer. A browser cannot produce a PDF file without the person: only the print dialog's "Save as PDF" does. Summa's static deployment (GitHub Pages) has no server to hold one. Set 0 §0.38 asks for each environment's artifact storage to be configured and isolated.

## Decisions

1. **The person saves the PDF; Summa keeps and records it.**
   - "Print or save as PDF" opens the print dialog for the Folio document.
   - The person then attaches the file they saved, through Limen's files pack: a real file input the person operates.
   - The engine reads it in 1 MiB slices. It refuses anything over 10 MB or not starting with `%PDF-`.
   - It fingerprints the file (SHA-256) and stores the bytes through Limen's store pack. Only then does it record the artifact (`Issuance.recordPdf`).
   - Recording the same file again changes nothing. A different file for a recorded PDF is refused, so an issued document never changes.
2. **An artifact store per environment, named in the deployment configuration.**
   - `"artifacts": {"store": "browser", "database": "summa-artifacts-<environment>[-suffix]"}`. Without it, the database is `summa-artifacts-<environment>`.
   - Any name that does not start with the environment's own prefix is refused. Two environments served from one origin therefore never share a database.
   - `Environments.check` refuses two deployments that name the same store.
3. **A reference names its store** (`browser-db:<database>/<sha256>`). A PDF recorded in another environment's store is reported as foreign, never served as this one's.
4. **The bytes can be lost; the record cannot.** Clearing the browser's site data removes the stored PDF. The record keeps its fingerprint, and attaching the same file again restores the bytes. The GitHub store (WI-0037) can give artifacts a durable home with no change to the records.
5. **The browser checks multipage printing** (INV-DOC-005) under print media: repeated table headers, whole rows, totals kept together, repeating page header and footer, page margins, nothing overflowing.

## Consequences

- The accounting page selects `limen.files` and `limen.store` when offered. Without them, the page says a PDF cannot be attached, and the invoice keeps its pending artifact.
- A Folio P2 server renderer, when it exists, can fill the same artifacts with no change to the records.
