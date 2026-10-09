---
id: DF-SUMMA-2026-0017
title: Audit provenance, appended contributions, and agents that prepare but do not commit
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
  - docs/requirements/SUMMA-PROVENANCE.md
  - research/decisions/DF-SUMMA-2026-0015--room-for-tax-without-calculating-it.md
tags: [audit, provenance, agents, access]
provenance:
  contributions:
    EXE-20261009T014433559Z-48075d15:
      operations: [created]
      at: 2026-10-09T02:24:56.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record audit provenance and agent execution safety (WI-0028)"
---

# DF-SUMMA-2026-0017 — Audit provenance and agent safety

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0028

## Context

The ledger's audit records kept who, what, when, source and correlation.
`Summa.Access.Actor` knew the actor's kind, agent identity and execution
id, but the audit trail did not carry them, so an agent's action read like
anyone's. Proposals did not keep who changed them. INV-PROV-001, 002 and
007, INV-AUD-002 and INV-AGENT-004 to 008 ask for all of this.

## Decisions

1. **`Provenance` travels with the ledger `Context` into every audit
   record.** It holds:
   - the Praxis actor kind (`human`, `agent`, `automation`, `unknown`,
     `x-` extensions);
   - the agent's provider, model and runtime;
   - the execution id, stored as supplied and never parsed;
   - the source system and id;
   - the reason.
   `Commands.contextFor` derives it from the command's actor. Services,
   integrations and scheduled processes are `automation`, never a person.
   The application's own changes are a person's.
2. **Stored at schema 2 only when present** (as for tax,
   DF-SUMMA-2026-0015): an audit event or proposal without provenance keeps
   its schema 1 bytes.
3. **Contributions are appended, never written over** (INV-AGENT-005,
   INV-PROV-007). A proposal keeps every contribution, oldest first: the
   agent's proposal, a person's rate change, readiness. Issuing it records
   the person; the trail keeps all three.
4. **A proposal explains itself from its records** (`Billing.explain`):
   - the sources selected, and those left out with why;
   - the grouping;
   - where each rate came from, and any change with its reason;
   - the terms and their source;
   - what is still assumed.
   The same explanation serves an agent's proposal and a person's.
5. **An agent reads only what its task needs** (`Billing.agentContext`):
   one customer's records, nothing of the others.
6. **Agents prepare; people commit** (INV-AGENT-007, SUM4-045). The default
   approval gates now also hold voiding, applying credit, reversing a
   payment, and changing billing terms or rates, alongside issue, refund,
   write-off, period close and manual adjustment.
7. **Audit is append-only at the storage boundary.** A command that
   rewrites or drops an audit event is refused, because audit records are
   immutable.

## Consequences

- `ProvenanceTests`, `CommandTests` and `AccountingTests` cover the
  above. The invoice history shows an agent's action with its identity and
  run.
- Still to do:
  - inspecting and approving an agent's proposal, with its explanation, in
    the application (SUM4-042, SUM4-043; WI-0044);
  - a natural-language request interface (INV-AGENT-001) and the entity
    version, outcome and changed-field summary on audit events
    (INV-AUD-002) (WI-0029).
