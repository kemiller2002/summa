---
id: DF-SUMMA-2026-0024
title: Books founded on a store start with their chart
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
  - research/decisions/DF-SUMMA-2026-0012--organization-manifest-schema-2-and-its-migration.md
  - research/decisions/DF-SUMMA-2026-0014--books-on-github-through-arca.md
tags: [github, accounts, founding, defects]
provenance:
  contributions:
    EXE-20261013T092158480Z-a1b3e67c:
      operations: [created]
      at: 2026-10-13T09:22:10.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Revisit founding on GitHub after the missing-accounts defect (WI-0048)"
---

# DF-SUMMA-2026-0024 — Books founded on a store start with their chart

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0048
- **Revisits:** DF-SUMMA-2026-0012 §1 and DF-SUMMA-2026-0014 §"setting up" (WI-0037, PRs #50 and #53)

## Context

WI-0037 retired the default-chart shortcut (DF-SUMMA-2026-0012):
- an organization's chart on a store is whatever the organization made it;
- `Organization.receivableAccounts` resolves the manifest's codes against
  the ledger, with no default-chart fallback anywhere.

DF-SUMMA-2026-0014 then founded books on GitHub with:
- the organization manifest, which names 1000, 1100, 2100, 2200, 4000 and
  6500;
- the founder as administrator;
- no ledger accounts.

The application has no way to add accounts. So on GitHub-backed books, no
draft could be submitted ("4000 is not an active revenue account") and no
invoice issued. This is an escaped defect from WI-0037 (PR #53, merge
55f8c3e, commit c1ff25d). It was found on 2026-10-13 while writing a
two-page end-to-end test for WI-0029.

The two remedies considered:
1. found the books with the manifest's accounts;
2. provide a path to add accounts.

## Decision

**Books on a store are founded with their starting chart, as the
organization's own records.**

1. `Organization.startingChart` is the one starting chart (twelve accounts)
   for every deployment. Local books use it as before
   (`LocalSnapshot.defaultChart` is the same list).
2. `Workspace.startingRecords` adds each account as the founder, with an
   audit event, and turns them into `summa.account` records.
3. `Governance.foundWith` writes those records in the same founding commit
   as the manifest and the administrator. Founding stays one commit, so it
   is all or nothing.
4. Nothing is inferred when books are read. `receivableAccounts` still
   resolves the manifest's codes against stored accounts only. An
   organization may later rename, deactivate or add accounts through
   records, as the chart is its own.

This keeps WI-0037's intent: the chart on a store is the organization's
own data, never a default assumed at read time. The defect was in founding,
which left the manifest naming accounts that did not exist. Remedy 2 alone
would have left every new organization unable to invoice until someone
built a chart by hand, and it would add a UI that v0.4 does not ask for. It
stays open as a later improvement (an account editor) and is not needed to
fix the defect.

## Consequences

- A founded organization can be invoiced at once. This is tested end to end
  through the page against a GitHub fake (`StoreTests`) and in a real
  browser (`github-books.spec.js`).
- No organization exists on GitHub yet: deployment (WI-0036) is blocked. So
  no books founded without a chart need repair. An organization founded
  before this decision would still have to add its accounts through
  records.
