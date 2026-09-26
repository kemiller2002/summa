---
id: DF-SUMMA-PROV-2026-0001
title: Adopt Praxis provenance for billing records and separate the hub's identity from the requester's
status: draft
version: 1.0.0
created: 2026-09-26
updated: 2026-09-26
owners:
  - summa
review_cycle: on-resolution
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/SUMMA-PROVENANCE.md
  - input-documents/summa-requirement-set-0.txt
  - input-documents/summa-invoice-generation-requirements.txt
  - lib/billing-record-provenance.mjs
  - lib/hub-identity.mjs
  - tools/summa_hub.mjs
  - tools/summa_hub_server.mjs
tags: [provenance, identity, billing, hub, decision]
provenance:
  contributions:
    EXE-20260926T081758520Z-5b5cb32d:
      operations: [created]
      at: 2026-09-26T08:25:53.411Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decision adopting Praxis provenance for billing and hub identity"
    EXE-20260926T085503319Z-1bc9aa20:
      operations: [modified]
      at: 2026-09-26T09:00:48.676Z
      last: 2026-09-26T09:01:13.484Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Contract revision 1.1: create semantics, full identity-environment scrub, library-only writes"
---

# DF-SUMMA-PROV-2026-0001 — Praxis provenance for billing records; hub identity separate from the requester

- **Date:** 2026-09-26
- **Status:** draft (implemented on branch `claude/echelon-provenance-upgrade-51kzr2`; awaiting owner acceptance)
- **Decision type:** adoption of an upstream contract; additive implementation
- **Requirements:** INV-PROV-001 .. INV-PROV-010 (`docs/requirements/SUMMA-PROVENANCE.md`)
- **Upstream:** Praxis `DF-ROS-2026-A036`, `DF-ROS-2026-A037`, `RQ-ROS-2026-A001..A019` at commit `c2657efb4d54f11d0fd0617cc1bcd5b8418601d5` (contract revision 1.1)

## Context

Requirement Set 0 §0.23 requires Summa to distinguish human, agent, and
service principals, to record actor type/ID, agent identity, execution ID,
source system, and correlation ID, and never to record agent actions as
though a human performed them. `INV-ARCH-005` makes Praxis the engineering
execution mechanism. Praxis now defines the Echelon-wide actor and the
portable `praxis.provenance/1` interchange block.

Two gaps existed:

1. No billing-side record shape said how provenance of billable work (for
   example a Chrona time entry produced by an agent execution) survives into
   Summa.
2. The project-administration hub runs other repositories' `./ros`. It
   forwarded only a bare `--actor` string taken from the HTTP body or CLI,
   and its child processes inherited the hub's own environment, so any
   `ROS_ACTOR*`/`ROS_TELEMETRY_*`/`ROS_EXECUTION_ID` of the process running the
   hub (for example an agent session) could be recorded in the spoke as the
   requester. The hub's own identity was never recorded.

## Decision

1. **Adopt, do not redefine.** Summa records Praxis actors and embeds
   `praxis.provenance/1` blocks. The Praxis reference library, schemas, and
   fixtures are vendored unchanged (`vendor/praxis-provenance/`, hashes in
   `SOURCE.json`); there is no runtime dependency on Praxis.
2. **Billing records are created, not relayed.** Each gets its own block;
   each source block is stored verbatim and never appended to. **Billing records** (`summa.billing-record/1`,
   `schemas/billing-record-provenance.schema.json`) carry each source's
   performer, execution, and provenance verbatim, plus their own block
   (creator keyed by execution; `derivedFrom` to Chrona entries and Praxis work
   items). When no requester is declared, Summa itself is the creator
   (automation `echelon/summa`, key `EXT-summa.<operationId>`).
3. **Provenance never gates accounting.** Absent/unknown provenance is valid
   and billable; nothing is decided by actor.
4. **Hub identity.** The hub resolves the requester only from explicit
   declarations and passes it to spokes through `ROS_ACTOR_KIND`, `ROS_ACTOR`,
   `ROS_TELEMETRY_PROVIDER/MODEL/RUNTIME`, and `ROS_EXECUTION_ID` after removing
   every inherited identity variable in the Praxis identity-environment list
   (reference `identityEnvironment`; contract revision 1.1), plus the legacy
   `--actor` for older spokes. An undeclared requester is set explicitly to
   `unknown`. It
   records its own actor (automation `echelon/summa-hub`, or a human operator
   declared with `--hub-actor-json`) separately in `.ros/hub/dispatches.jsonl`.
5. **Where the hub change lives.** `tools/ros_hub_cli.mjs`,
   `tools/ros_hub_server.mjs`, and `ros-hub` are ROS tool-owned artifacts;
   `./ros doctor` reports edits to them as errors ("move the change into a
   user-owned file; tool-owned artifacts are replaced on upgrade"). The change
   is therefore made in user-owned files: the pure `lib/hub-identity.mjs`, the
   `summa-hub` CLI (`tools/summa_hub.mjs`; `create` is identity-aware, every
   other command is delegated to `ros-hub`), and `tools/summa_hub_server.mjs`
   (handles `POST /api/repos/:id/work`, delegates every other route to the
   tool-owned server).

## Consequences

- Agent execution -> work item -> time entry -> billing record is traceable
  without joining on free text, and grouping keeps every source.
- Summa keeps billing when Praxis, Chrona provenance, or both are absent.
- `ros-hub` and `tools/ros_hub_server.mjs` still behave as before (bare
  `--actor`, inherited environment) until the upstream ROS hub adopts this
  rule. Operators should use `summa-hub` and `tools/summa_hub_server.mjs`.
- `.ros/hub/dispatches.jsonl` grows by one line per hub-created work item.
- Identity remains self-reported provenance, not authentication: the hub
  server still has no authentication and binds to localhost.

## Rejected alternatives

- **Edit the tool-owned hub files in place.** Fails `./ros verify`; lost on
  the next ROS upgrade.
- **Forward the hub's environment unchanged.** That is the defect being fixed.
- **Record the hub as the requester when none is declared.** That would
  misattribute the request; an undeclared requester is `unknown`.
- **Require provenance for billing.** Contradicts INV-PROV-003 and would make
  accounting depend on Praxis.

## Migration path

No historical data is rewritten. Existing work items, events, and billing
data without provenance remain valid and read as unattributed/unknown. The
`agent:chatgpt` actor recorded by earlier runs of `readiness-work.yml` stays
as recorded; new runs declare `automation`.

## Follow-ups

- Upstream: propose the same requester/hub separation for the ROS hub
  scaffold (`tools/ros_hub_cli.mjs`, `tools/ros_hub_server.mjs`), then retire
  the Summa-owned wrappers.
- Upstream: the web hub UI (`web-hub/app.ts`) could send `actorJson`/`execution`.
- When the Echelon execution envelope v2 is published in echelon-registry,
  accept it on the billing.record intake (map v1 envelopes with
  `actorFromEnvelopeV1`/`keyFromEnvelopeV1`).
