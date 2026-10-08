---
id: DF-SUMMA-2026-0012
title: The organization manifest names its credit, deposit and bad-debt accounts (schema 2)
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
  - research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md
  - docs/requirements/implementation-gap-analysis.md
tags: [storage, schema, migration, manifest, arca]
provenance:
  contributions:
    EXE-20261008T221837840Z-607ce088:
      operations: [created]
      at: 2026-10-08T22:34:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the credit, deposit and bad-debt accounts in the organization manifest, with a tested migration (WI-0037)"
---

# DF-SUMMA-2026-0012 — The organization manifest names its credit, deposit and bad-debt accounts (schema 2)

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0037 (part 1)

## Context

Customer credits, unapplied deposits and bad-debt write-offs post to three
accounts. Schema 1 of the organization manifest (`summa.organization`) named
only cash, receivables and revenue, so local books took the default chart's
2100, 2200 and 6500 (DF-SUMMA-2026-0009 §7). Books on GitHub cannot rely on
that: their chart is whatever the organization made it. Set 0 §0.31–§0.32
and v0.3 §9–§10 forbid reading older data as current or rewriting it
silently.

## Decisions

1. **Schema 2 names the accounts.** `accounting` gains
   `customerCreditsAccount`, `customerDepositsAccount` and `badDebtAccount`.
   `Organization.schema` is `{ OldestReadable = 1; Current = 2 }`. A new
   organization's manifest names the default chart's 2100, 2200 and 6500.
   `Organization.receivableAccounts` resolves all six codes against the
   ledger; there is no default-chart fallback anywhere.
2. **A schema 1 manifest is read only to be migrated.**
   `Organization.decodeStored` returns `NeedsMigration(1, body)`;
   `Organization.decode` refuses it (`INCOMPATIBLE_SCHEMA`).
3. **The migration is a pure, named function:** `Migrations.organizationV2`
   fills the three fields from the books' own chart, 2100 and 2200 as
   liabilities and 6500 as an expense. If any is missing or of another type
   it refuses (`MIGRATION_UNSAFE`), names what is wrong, and invents nothing.
   It refuses a body that already names any of them.
4. **Local books are migrated as they are read, once.** `LocalSnapshot.decode`
   migrates a schema 1 manifest and reports `Migrated`. The application saves
   the books at once and says the books were updated. The next load finds
   schema 2.
5. **Books on a store stay read-only until an administrator migrates them.**
   `Compatibility.storedAccess` makes a folder whose manifest is schema 1
   read-only, whatever its Arca manifest declares, so every command refuses
   it (`Incompatible`). `Migrations.migrate` needs `ManageSettings` and books
   that pass every integrity check. It writes one commit, conditioned on the
   revisions read: the manifest at schema 2, and the folder's Arca manifest
   declaring `summa.organization: 2`. `Verification.checkMigration` confirms
   the books are unchanged. Running it again does nothing.
6. **In place, not through Arca's migration workflow.** Arca's
   `Migration.run` copies a namespace to another location. This migration
   rewrites one mutable record and moves no financial record, so it is an
   ordinary conditioned commit. Moving data between locations remains WI-0038.

## Consequences

- The application manifest records `summa.organization` at schema 2 for new
  deployments.
- An organization whose chart lacks the three accounts must add them, or
  name others, before its books can be written again.
- When WI-0037 connects the application to GitHub, start-up offers the
  migration to an administrator instead of opening the books for writing.
