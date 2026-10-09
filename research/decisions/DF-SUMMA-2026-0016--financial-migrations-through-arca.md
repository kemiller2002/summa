---
id: DF-SUMMA-2026-0016
title: Financial migrations and relocations through Arca's migration workflow, checked before they write
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0012--organization-manifest-schema-2-and-its-migration.md
  - research/decisions/DF-SUMMA-2026-0015--room-for-tax-without-calculating-it.md
tags: [storage, migration, arca, backup, reconciliation]
provenance:
  contributions:
    EXE-20261009T012503535Z-72200b70:
      operations: [created]
      at: 2026-10-09T01:31:20.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Run financial migrations end to end (WI-0038)"
---

# DF-SUMMA-2026-0016 — Financial migrations and relocations through Arca's migration workflow

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0038

## Context

WI-0021 built the migration safety checks: identity or lineage, the trial
balance unless a correction is declared, and invariants after. WI-0022 built
sealed backups and verified restores. Arca 0.2.1's `Migration` copies a
namespace to another location (validate, copy, verify, activate) and
retires the source only on request. The organization manifest's small
in-place migration (DF-SUMMA-2026-0012) needs none of this. A change to
financial records, or a move to another repository, does.

## Decisions

1. **A migration is a definition** (`Summa.Operations.FinancialMigration`):
   - an id;
   - a one-line purpose;
   - a stated rollback;
   - the schema versions it writes;
   - a per-record transform;
   - its declared intent (lineage, whether balances change).
   `relocation` is the definition that transforms nothing.
2. **Checked before anything is written** (`check`, pure). The run stops
   at the first of these that fails:
   - The rollback must be stated (SUM3-039).
   - The books read must pass every check.
   - The books the transform produces are computed from the stored records
     and must load cleanly and pass `Verification.checkMigration` against
     the books read.
3. **Then, in order:**
   1. a sealed backup (`Backup.take`);
   2. reconciliation of the books;
   3. Arca's `Migration.run` (resumable, recorded in the target manifest);
   4. the copy is read back, and it must be exactly the checked books,
      record for record, and is reconciled again.
4. **The source is untouched until it is explicitly retired**
   (`retire`), and Arca retires it only while the copy still holds what it
   implies. Rolling back before that is to keep using the source; after
   it, the backup restores it. "Restore the database" is never the only
   way back.
5. **Operations, not the browser.** The runner lives in
   `Summa.Operations`, beside backup and recovery, for an operator to run
   with both providers.

## Consequences

- `FinancialMigrationTests` cover:
  - a relocation with its backup, the copy equal to the source, the source
    unchanged;
  - no rollback stated, nothing written;
  - an undeclared balance change refused before writing;
  - a schema migration writing customer schema 2 to the copy;
  - retirement making the source refused.
- Summa has no financial schema change that needs this today. Tax
  (DF-SUMMA-2026-0015) is written at schema 2 only where it is recorded. The
  runner is ready for the first one, and for moving an organization to its
  own repository.
