---
id: INV-PROV
title: Summa Agent Provenance, Billing Traceability, and Hub Identity Requirements
status: required
version: 1.1.0
owners:
  - summa
created: 2026-09-26
updated: 2026-09-26
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - input-documents/summa-requirement-set-0.txt
  - input-documents/summa-invoice-generation-requirements.txt
  - research/decisions/DF-SUMMA-PROV-2026-0001--praxis-provenance-for-billing-and-hub-identity.md
  - schemas/billing-record-provenance.schema.json
  - lib/billing-record-provenance.mjs
  - lib/hub-identity.mjs
  - tools/summa_hub.mjs
  - vendor/praxis-provenance/SOURCE.json
tags: [provenance, identity, billing, hub, echelon]
provenance:
  contributions:
    EXE-20260926T081758520Z-5b5cb32d:
      operations: [created]
      at: 2026-09-26T08:25:52.918Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Summa provenance, billing traceability, and hub identity requirements"
    EXE-20260926T085503319Z-1bc9aa20:
      operations: [modified]
      at: 2026-09-26T09:00:48.112Z
      last: 2026-09-26T09:01:12.876Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Contract revision 1.1: create semantics, full identity-environment scrub, library-only writes"
---

# Summa Agent Provenance, Billing Traceability, and Hub Identity Requirements

Status: **Required**. These requirements refine Requirement Set 0 §0.23
(Human, Agent, and Service Identity: "Agent actions must never be recorded as
though a human performed them"), and the invoice requirements `INV-ARCH-005`
(Praxis governs engineering execution), `INV-AGENT-005`/`INV-AGENT-006`
(human modifications retained; agent identity and execution ID),
`INV-AUD-002`/`INV-AUD-004` (audit fields; provenance survives grouping), and
`INV-CHR-010` (underlying Chrona entry traceability). The decision is
[`DF-SUMMA-PROV-2026-0001`](../../research/decisions/DF-SUMMA-PROV-2026-0001--praxis-provenance-for-billing-and-hub-identity.md).

## Authority

Praxis is authoritative for identity and provenance. Summa adopts, and does
not restate or redefine, the Praxis contract at commit
`c2657efb4d54f11d0fd0617cc1bcd5b8418601d5` of `kemiller2002/praxis` (contract revision 1.1):
`docs/agent-provenance.md`, `DF-ROS-2026-A036`, `DF-ROS-2026-A037`, and
`RQ-ROS-2026-A001` through `RQ-ROS-2026-A019`. Where this document and the
Praxis contract appear to differ, the Praxis contract wins.

## Requirements

### INV-PROV-001 Praxis identity, not a second identity model

Summa MUST record actors as Praxis actors (`RQ-ROS-2026-A001`). Its §0.23
principal types map as Human -> `human`, Agent -> `agent`, Service ->
`automation`. Actor type, actor ID, agent identity (provider/model/runtime,
literal `"unknown"` when not known), execution ID, source system, and
correlation ID (§0.23, `INV-AUD-002`) are carried by the actor, the
contribution key, and the surrounding record; Summa adds no parallel schema.

### INV-PROV-002 Billable work keeps the provenance of its source

A billing record derived from agent activity MUST preserve provenance
sufficient to trace the source (`RQ-ROS-2026-A008`, `RQ-ROS-2026-A009`):

- each source (for example a Chrona time entry, `chrona:entry/<id>`) keeps its
  performer actor and execution (`EXE-...`/`EXT-...`) and its own
  `praxis.provenance/1` block, carried verbatim;
- `billing.record` is a create (Praxis `docs/echelon-provenance-architecture.md`,
  "Creating a record versus relaying one"): the billing record gets its own
  `provenance` block recording who created it, keyed by execution, with
  `derivedFrom` = each supported source block's `derivedFrom` plus the Chrona
  entries and Praxis work items it was derived from. Source blocks are stored
  verbatim in `sources[]` and are never appended to. Lineage is not
  authorship: the agent that performed the work is not recorded as the
  billing record's author;
- grouping several sources into one record or line never removes a source or
  its provenance (`INV-AUD-004`, `INV-CHR-010`); two executions of the same
  agent remain distinct.

### INV-PROV-003 Accounting does not depend on Praxis

Summa MUST keep working without Praxis (`RQ-ROS-2026-A018`). A billing record
with absent, empty, or unknown provenance MUST remain valid and billable, and
reads as provenance `unknown`; nothing is inferred or backfilled. Billing,
issuance, or payment MUST NOT be blocked on Praxis availability, and MUST NOT
be granted, denied, or weighted by actor (`RQ-ROS-2026-A019`). Capability
authorization (§0.22) stays separate.

### INV-PROV-004 Agent actions are never recorded as human

The actor that created or changed a billing record MUST come from an explicit
declaration and be recorded verbatim. An agent creator is recorded as an
agent, keyed by its execution, or by `EXT-op.<operationId>` when the
execution is unknown (never invented). A human approval or correction is a
separate contribution (`approved`/`modified`) that never replaces the agent's
(`RQ-ROS-2026-A004`, `INV-AGENT-005`); re-attributing an execution to another
actor is refused.

### INV-PROV-005 The hub records its own actor separately

The project-administration hub MUST record its own actor, automation
`echelon/summa-hub`, or the human operating it when explicitly declared,
separately from the requester for every spoke operation it performs, in its
own dispatch record (`.ros/hub/dispatches.jsonl`, `summa.hub-dispatch/1`).
The dispatch's `praxis.provenance/1` block credits the requester with the
request (`created`) and the hub with carrying it (`transformed`, key
`EXT-summa.<operationId>`). The hub is never an agent.

### INV-PROV-006 The hub passes the requester's identity explicitly

The requester MUST be taken only from an explicit declaration
(`RQ-ROS-2026-A016`): a structured actor and execution, the legacy `--actor`
string, or, for a command-line invocation only, the invoking process's
`ROS_ACTOR_KIND`/`ROS_ACTOR`/`ROS_TELEMETRY_*`/`ROS_EXECUTION_ID`. The HTTP
server's own environment is never the requester's, and an environment
`ROS_EXECUTION_ID` is ignored unless the process also declares an identity.
Before launching a spoke the hub MUST remove every identity variable in the
Praxis `identity-environment.json` list (`IDENTITY_ENVIRONMENT_VARIABLES`:
`ROS_*`, all `ROS_TELEMETRY_*` including session/run/conversation/version
keys, `CLAUDE_CODE_SESSION_ID`, `CODEX_SESSION_ID`, `CODEX_THREAD_ID`,
`GEMINI_SESSION_ID`, `COPILOT_SESSION_ID`, `GITHUB_ACTIONS`, `GITHUB_RUN_ID`,
`OLLAMA_HOST`) and then set only the requester's declared values, so the hub's
identity, session, runtime, or CI run is never forwarded as if it were the
requester's. An undeclared requester is set explicitly to
`ROS_ACTOR_KIND=unknown` and `ROS_ACTOR=unknown`.

### INV-PROV-007 Older spokes keep working

The hub MUST keep passing the legacy `--actor` argument (the caller's string
verbatim, else the requester's id when known) so that spokes on older ROS
versions, which ignore the environment variables, behave as before.

### INV-PROV-008 Receiving provenance

At Summa's boundary a `praxis.provenance/1` block is classified per
`RQ-ROS-2026-A015`: `supported` blocks are preserved; `unsupported` majors are
carried verbatim and never appended to; `malformed` blocks, and
credential-like values anywhere in the record (`RQ-ROS-2026-A017`), reject
the record with a structured error.

### INV-PROV-011 Contract revision 1.1 writes and validation

Every write to a provenance block MUST use only the reference library's
result; it refuses credentials, contributions dated before the creation, a
late `created`, re-attribution, and an unknown actor extending a known
entry, and the refusal is returned to the caller with nothing stored.
Summa's own validation follows revision 1.1: calendar-valid timestamps
ordered at millisecond precision; JSON `null` is never absence; keys derived
from operation ids use the reference's injective escaping.

### INV-PROV-009 Conformance

The Praxis reference library, schemas, and conformance fixtures are vendored
unchanged with SHA-256 recorded; tests verify the hashes, every conformance
case, and the Echelon end-to-end chain (`RQ-ROS-2026-A018`).

### INV-PROV-010 Automation is not an agent

Repository automation (GitHub Actions) MUST NOT declare itself as an agent;
it is recorded as `automation`. Historical records that declared otherwise are
left unchanged (`RQ-ROS-2026-A003`, `RQ-ROS-2026-A007`).

## Traceability

| Requirement | Implementation | Tests |
|---|---|---|
| INV-PROV-001 | `schemas/billing-record-provenance.schema.json` (refs vendored Praxis actor/interchange schemas); `lib/billing-record-provenance.mjs` | `tests/vendored-praxis-provenance.test.mjs` (schema references); `tests/billing-record-provenance.test.mjs` |
| INV-PROV-002 | `sourceFromChronaEntry`, `deriveBillingRecord`, `originatingExecutions` | billing record derived from an agent time entry keeps the originating execution; grouping several executions keeps each one distinct; round trip; source blocks are kept verbatim and never appended to |
| INV-PROV-003 | `receiveBillingRecord` (`billable` independent of provenance) | missing Praxis provenance: still valid and billable; billing validity never depends on who the actor is |
| INV-PROV-004 | `deriveBillingRecord`, `appendBillingContribution` | agent actions are never recorded as human …; agent creator without a known execution …; human correction … |
| INV-PROV-005 | `lib/hub-identity.mjs` (`resolveHubActor`, `dispatchRecord`); `tools/summa_hub.mjs`; `tools/summa_hub_server.mjs` | `tests/hub-identity.test.mjs`: dispatch record keeps the hub actor and the requester apart; create (HTTP path); summa hub server |
| INV-PROV-006 | `resolveRequester`, `spokeEnvironment` (reference `identityEnvironment`) | HTTP request with no declaration is unknown …; requester's identity is passed … replacing the hub's; create with nothing declared (regression: no hub provider/runtime/session); scrub list is the reference identity-environment list; environment execution id without identity is not inherited |
| INV-PROV-007 | `legacyActorArguments` | legacy --actor string is forwarded verbatim …; summa-hub create (CLI path) |
| INV-PROV-008 | `receiveBillingRecord` | malformed provenance is rejected …; unsupported provenance major is carried verbatim … |
| INV-PROV-009 | `vendor/praxis-provenance/` + `SOURCE.json` | `tests/vendored-praxis-provenance.test.mjs` |
| INV-PROV-010 | `.github/workflows/readiness-work.yml` | workflow review (declares `ROS_ACTOR_KIND=automation`, no agent actor) |
| INV-PROV-011 | `appendBillingContribution`, `deriveBillingRecord`, `dispatchRecord` (reference `appendContribution`), null checks, key escaping | regression: Bearer reason / back-dated / late created / unknown extension / creator credential refused; contract 1.1: null, calendar, escaping; 56 vendored cases |

Run: `npm test`.

## Scope note: tool-owned hub files

`tools/ros_hub_cli.mjs`, `tools/ros_hub_server.mjs`, and `ros-hub` are ROS
tool-owned artifacts (`.echelon/ros.json`); editing them fails `./ros verify`
and an upgrade would replace them. INV-PROV-005..007 are therefore met by the
Summa-owned `summa-hub` CLI and `tools/summa_hub_server.mjs`, which delegate
every other operation to the tool-owned hub. `ros-hub` and
`tools/ros_hub_server.mjs` keep their previous behaviour until the upstream
ROS hub adopts the same rule (DF-SUMMA-PROV-2026-0001, follow-up).
