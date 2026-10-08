---
id: DF-SUMMA-2026-0002
title: Fold the draft billing-provenance PRs into Summa-owned F#-neutral requirements and close them
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
  - docs/requirements/SUMMA-PROVENANCE.md
  - docs/requirements/backlog-plan.md
tags: [provenance, chrona, housekeeping]
provenance:
  contributions:
    EXE-20261008T145558830Z-61dab5d8:
      operations: [created]
      at: 2026-10-08T14:56:44.807Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide how the draft provenance PRs are folded and closed"
---

# DF-SUMMA-2026-0002 — Fold the draft provenance PRs into requirements

- **Date:** 2026-10-08
- **Status:** accepted

## Context

Draft PRs #6 and #7 (2026-09-26) both proposed billing provenance
requirements numbered `INV-PROV-001..`, with different meanings for the same
numbers, and both implemented them in JavaScript (`lib/*.mjs`,
`contracts/*.mjs`) with vendored Praxis fixtures. Since then Summa's domain
is F# (`Summa.Ledger`), the project-administration hub became Praxis
tool-owned, and the remaining requirements were split into ordered work
items (WI-0017..WI-0031). Neither PR can merge as it stands.

## Decision

1. Keep the requirement substance, not the code: one numbering,
   `docs/requirements/SUMMA-PROVENANCE.md` INV-PROV-001..008, based on #7
   (the receiver-side set) with #6's overlapping billing rules merged in.
2. Each requirement names the work item that implements it: WI-0017 (origin
   travels in the contract), WI-0025 (import keeps origin), WI-0027
   (grouping), WI-0028 (audit actor and execution), WI-0019 (identity is not
   authority). Those items' descriptions reference INV-PROV.
3. #6's hub dispatch identity rules are not carried forward: the hub is
   Praxis's, so they belong upstream.
4. Close #6 and #7 with a pointer to this decision.

## Rationale

The requirements refine corpus rows (§0.23, §0.25, INV-AUD, INV-AGENT,
INV-CHR, INV-SOURCE) that the backlog already plans; implementing them in F#
inside those items avoids a second, JavaScript implementation that would
have to be kept in agreement. Reversible: the closed branches remain.
