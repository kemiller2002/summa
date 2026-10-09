---
id: DF-SUMMA-2026-0020
title: Command telemetry, self-describing exports, projection checks and business dates
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
  - research/decisions/DF-SUMMA-2026-0019--agent-requests-in-words-and-audit-changes.md
tags: [operations, telemetry, exports, reports, time]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-09T04:46:53.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record operational safety decisions (WI-0029 slice 2)"
---

# DF-SUMMA-2026-0020 — Command telemetry, exports, projection checks and business dates

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0029

## Context

v0.3 asks for the following (§16–18 and §26–29):
- operational telemetry that tells financial problems from technical ones;
- structured logs without sensitive data;
- correlation ids that run through a workflow;
- exports that say what they hold;
- reports that agree with each other;
- deterministic results;
- business dates that do not shift through UTC conversion.

## Decisions

1. **One telemetry event per command run** (`Telemetry.observe` around
   `Commands.execute`). The event holds:
   - the correlation id, command, capability, actor, actor kind, execution
     id and source system;
   - the changed record paths;
   - the outcome and how many times the command was decided;
   - whether the command was a duplicate.

   Each event has a category:
   - `Financial`: a refused transition, untrustworthy books, or a change
     that cannot be stored;
   - `Access`: not authorized, or needs a person;
   - `Technical`: storage, an unsettled conflict, an unknown outcome, or
     incompatible data.

   `Telemetry.toJson` is the structured log line. It holds identifiers and
   outcomes only, never names, addresses, amounts or free text. The sink is
   the host's to choose.
2. **The correlation id is the actor's.** `contextFor` puts it in every
   audit event a command writes, and Arca puts it in the commit metadata.
3. **Exports describe themselves.** Every CPA CSV starts with `# name:
   value` lines: the export, `summa.export/1`, when it was generated
   (UTC), the accounting period, the data version and the filters. The
   data version is the SHA-256 of the books' authoritative records, by
   path and canonical content. The lines are comments to a CPA's tools,
   and a reproducible fingerprint of the dataset.
4. **The receivable projection is checked against the books**
   (`Reconciliation.agreesWithIndex`), both in total and invoice by
   invoice. The balance sheet's receivables are the control account, which
   `Reconciliation.run` checks against the open invoices.
5. **Today is a business date.** The application's "today" is the
   organization's date in its IANA time zone (`Organization.businessDate`).
   An unknown zone falls back to UTC, never to a guess. Instants stay UTC;
   accounting dates stay `DateOnly`.

## Consequences

- SUM3-017, 018, 026, 027, 028 and 029 are tested.
- SUM3-016 still needs events for integration and migration failures.
