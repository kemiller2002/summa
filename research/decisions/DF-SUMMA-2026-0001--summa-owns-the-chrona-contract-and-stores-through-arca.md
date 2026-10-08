---
id: DF-SUMMA-2026-0001
title: Summa owns the Chrona-to-Summa contract as a versioned contracts package, is built after Chrona, stores data through Arca without Strata, and signs in through Fides
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
  - docs/requirements/SUMMA-DATA-LOCATION.md
tags: [contracts, chrona, build-order, storage, strata, encryption]
provenance:
  contributions:
    EXE-20261008T074602101Z-3aa35119:
      operations: [created]
      at: 2026-10-08T07:47:02.355Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# DF-SUMMA-2026-0001 — Summa owns the Chrona contract; storage through Arca

- **Date:** 2026-10-08
- **Status:** accepted (user decisions of 2026-10-08)

## Decisions

1. **Contract ownership.** The application that accepts the data owns the
   contract, so **Summa owns the Chrona-to-Summa contract**. Summa publishes
   it as a small, versioned contracts package: pure F# types plus a canonical
   JSON codec. The package is released through Summa's release workflow and
   registered in echelon-registry. Chrona consumes it through `conditor.json`.
   Requirements served: SUM0-024, SUM0-025, SUM0-026, SUM1-017, SUM2-031,
   INV-CHR, INV-CONTRACT and INV-SOURCE.
2. **Build order.** Chrona first, then Summa, then the others; Helix runs in
   parallel. The contracts package is pulled forward because Chrona's
   integration item needs it.
3. **Storage.** Summa's authoritative GitHub data store (Set 0) is
   implemented through Arca (`kemiller2002/arca`) at a per-deployment
   location, in a Summa-owned namespace (SUM-DATALOC-001..005). Summa needs
   different permissions from Chrona, so production Summa data lives in its
   own repository, because GitHub permissions are per repository. Summa does
   **not** use Strata. Only applications that use a database use Strata.
4. **Encryption.** There is no application-level at-rest encryption for now
   (Arca DF-ARCA-2026-0002). SUM3-024 ("encrypted storage where supported")
   is met by encrypted transport, encrypted backups and repository
   permissions; per-application encryption is a deferred, possible future
   Arca item.
5. **Sign-in.** Summa signs in through Fides (`kemiller2002/fides`), GitHub
   only for now.
6. **PDFs.** Invoices and reports render through Folio.
