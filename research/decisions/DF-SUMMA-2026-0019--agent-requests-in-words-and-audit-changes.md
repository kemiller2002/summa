---
id: DF-SUMMA-2026-0019
title: Agent requests in words become proposals, and audit events say what a change did
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
  - research/decisions/DF-SUMMA-2026-0017--audit-provenance-and-agent-safety.md
tags: [agents, audit, provenance]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-09T03:31:04.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the agent request interface and audit change detail (WI-0029)"
---

# DF-SUMMA-2026-0019 — Agent requests in words, and what a change did

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-AGENT-001 asks for an authorized agent interface. Through it, a request
in words, such as "Invoice Acme for all approved architecture work from
September 1 through September 15 at the contracted rate and include approved
reimbursable expenses", becomes a proposal. INV-AUD-002 asks that material
audit events carry the entity's version, the outcome, and a changed-field
summary. DF-SUMMA-2026-0017 recorded who acted and from where, but not what
the change did.

## Decisions

1. **Summa reads one documented phrasing, and asks about everything else.**
   `AgentRequests.interpret` reads the following phrasing, in which only
   the customer is required:

   > Invoice \<customer\> for [all] [approved] [\<kind of\>] work [from \<date\> through \<date\>] [at the contracted rate] [and include approved reimbursable expenses]

   Summa does not interpret free language itself. The agent, often a
   language model, puts the person's words into this phrasing. Summa then
   settles every fact from the books or asks a question. A question
   arises when:
   - no customer has the name, or several do;
   - a date cannot be read;
   - the period ends before it starts;
   - no work matches the request;
   - there is nothing to bill;
   - the request names a rate. Agents propose at the contracted rate only
     (INV-AGENT-007), and a person may change a rate on the proposal.

   A date without a year takes the current year. Work matches when its
   project, activity type or description mentions the kind named. Time is
   grouped by project.
2. **The interface is a command that needs `ProposeInvoice` only.**
   `Commands.agentRequest` runs through the stored roster like any
   command:
   - an agent the roster does not know is refused;
   - the result is a proposal reserving its sources, never an issued
     invoice;
   - the request is kept as the agent's reason on its contribution;
   - the proposal id is the idempotency key, so the same request again
     changes nothing.
3. **`AuditChange` on the audit event.** It holds three things:
   - the entity's version after the change;
   - the outcome;
   - the fields changed, with their values before and after when they are
     short, and by name otherwise.

   The outcome is `applied`, because audit events record only what was
   committed. A refused command leaves the books unchanged and is
   reported to its sender.

   It is recorded on:
   - drafts created, changed, submitted, returned and issued (with the
     version issued);
   - customer and engagement changes;
   - proposal rate overrides.

   An event without it is a change applied to an entity that keeps no
   version. It is stored at schema 2 and shown in the invoice history.

## Consequences

- INV-AGENT and INV-AUD are tested.
- The rest of WI-0029, operational safety, stays open on it.
