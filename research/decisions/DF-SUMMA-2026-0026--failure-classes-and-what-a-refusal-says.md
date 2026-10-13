---
id: DF-SUMMA-2026-0026
title: Every command failure has a class and says what is unchanged and whether to try again
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
tags: [operations, failures, errors]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T11:05:14.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record failure classification (WI-0029 slice 6, INV-FAIL)"
---

# DF-SUMMA-2026-0026 — Failure classes and what a refusal says

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-FAIL-002 asks that errors say what failed, why, what remains unchanged,
whether a retry is safe, and what to do next. INV-FAIL-004 asks that
failures be told apart as:
- retryable;
- permanent or validation;
- authorization;
- concurrency conflict;
- external dependency unavailable;
- integrity violation.

Domain refusals already came as blockers with an explanation and a
resolution. Command failures said only why.

## Decisions

1. **`Commands.classify`** maps every `CommandFailure`, including every
   Arca `StorageFailure`, to a `FailureReport`: its class, what is
   unchanged, whether trying again is safe, and what to do next. It is
   total and pure.
2. **Only an unknown outcome is unsafe to retry.** The change may have been
   saved, so it is reconciled before anything is sent again (Arca's offline
   sync does this). Every other failure leaves the books as they were.
3. **`Commands.explain`** turns the report into sentences. The
   application appends them to the reason (`Store.describeFailure`). The
   reasons no longer carry their own advice, so nothing is said twice.
4. **A provider failure marked transient is retryable**, and one not
   marked transient is "dependency unavailable". A rate limit is
   retryable.

## Consequences

- INV-FAIL is tested.
- Telemetry categories (DF-SUMMA-2026-0020) are unchanged. The finer class
  is available to any later event schema without changing the domain.
