---
id: DF-SUMMA-2026-0023
title: Issuing requires the reviewed version; a reissue with other input is a conflict
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
tags: [concurrency, idempotency, invoicing]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T09:14:13.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record concurrency decisions (WI-0029 slice 4)"
---

# DF-SUMMA-2026-0023 — Issue requires the reviewed version

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-CON-001 says important invoice operations require an expected
aggregate version. INV-CON-002 says issuing a stale version fails safely.
INV-CON-008 says a reused idempotency identity with materially different
input is a conflict.

Before this decision:
- `IssueRequest.ExpectedVersion` was optional, and None issued whatever
  version was current.
- The application issued with None. On GitHub, where a command is decided
  again on the books as they stand, it would issue a draft someone else had
  changed since this page reviewed it.
- An issue repeated for an already-issued draft returned the invoice
  whatever the request said.

## Decisions

1. **`ExpectedVersion` is required** (`int`, not `int option`).
   `Organization.issueRequest` takes it. Issuing at any version other
   than the draft's is refused as `stale-version` (`StaleDraft`), before
   the "not reviewed" check, so the person is told what happened.
2. **The application commits what the person saw.** `Accounting.pinned`
   rewrites `DraftIssued` to `DraftIssuedAt version`, using the draft
   version on screen, before the command is committed. The replay on fresh
   books checks that version. The kernel cannot carry the version: event
   keys come only from list rows.
3. **`Billing.accept` binds the draft it makes.** It saves the draft and
   issues the version it just saved. The proposal's own state and
   changed-source checks still guard it.
4. **A reissue is checked** (`Invoicing.reissued`).
   - The same date, draft version and number (when one was chosen) return
     the invoice already issued.
   - Anything else is `IssueConflict`, and nothing changes.
   - The ids a retry generates again (invoice, entry, obligation) are not
     input.

## Consequences

- INV-CON is tested.
- INV-CONTRACT-001 and 003 are evidenced. 002 (versioned external command
  contracts) remains.
- Callers that relied on `ExpectedVersion = None` must pass the version
  they read.
