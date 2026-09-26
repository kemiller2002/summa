# Summa Agent-Origin Provenance Requirements

Status: **Required**
Application: **Summa**
Work item: `WI-0004`
Date: 2026-09-26
Refines (without editing the source inputs in `input-documents/`):
Requirement Set 0 §0.23 (human, agent, and service identity) and §0.25
(cross-application references); invoice generation requirements
`INV-AUD-002`, `INV-AUD-004`, `INV-REV-007`, `INV-AGENT-005`..`INV-AGENT-008`,
`INV-CHR-001`..`INV-CHR-004`, `INV-CHR-010`, and `INV-SOURCE-008`.

## 1. Purpose and scope

Where billable work derives from agent activity, Summa must keep enough
provenance to trace an invoice line back to the agent execution that produced
the work, without making accounting depend on the system that recorded that
execution.

Identity and provenance shapes are owned by Praxis and are referenced, not
restated:

- actor: `RQ-ROS-2026-A001`; execution identity: `RQ-ROS-2026-A002`;
- provenance interchange record `praxis.provenance-record`: `RQ-ROS-2026-A013`;
- execution propagation (`EXE-<system>.<run>`): `RQ-ROS-2026-A014`;
- no silent stripping: `RQ-ROS-2026-A015`.

Chrona's origin fields are defined by Chrona `CHR-PROV-001`..`CHR-PROV-010`
(`chrona.time-observation-origin` v1 and its `originLineage`).

The IDs below use the prefix `INV-PROV-` because `INV-CHR-005` and later are
already assigned in the invoice generation requirements. MUST, MUST NOT,
SHOULD, and MAY are normative.

## 2. Requirements

### INV-PROV-001 Praxis-compatible actor (refines §0.23, INV-AUD-002, INV-AGENT-006)

Where §0.23 and INV-AUD-002 require "Actor", "Actor type", and "Agent
identity", Summa MUST be able to hold a Praxis-compatible actor
(`kind`: `agent` | `human` | `automation` | `unknown` | `x-...`, `id`, and for
non-humans `provider`/`model`/`runtime`, literal `unknown` when not known),
preserving fields it does not model. Summa's principal types map to it without
loss: Human → `human`; Agent → `agent`; Service → `automation` (or an `x-`
extension when Summa needs a finer category). An agent action MUST NOT be
recorded as a human action, and an unknown actor MUST NOT be recorded as a
human.

### INV-PROV-002 Execution ID is an opaque reference (refines §0.23, INV-REV-007, INV-AGENT-006)

An Execution ID is a namespaced, opaque reference: a Praxis
`EXE-<timestamp>-<random>`, a foreign `EXE-<system>.<run>`, or `CTB-...` for a
non-agent contribution outside any run. Summa MUST store it exactly as
supplied, MUST NOT parse meaning out of it beyond its syntax, and MUST NOT
dereference it to authorize, approve, or price anything. Summa MUST NOT mint a
Praxis-shaped execution ID; a Summa run that needs a key uses
`EXE-summa.<run>`.

### INV-PROV-003 Chrona billing snapshots keep origin (refines INV-CHR-001..004, INV-SOURCE-008)

When Summa imports a Chrona billing snapshot, each source entry MUST keep
Chrona's origin exactly as Chrona published it: every `originLineage` entry
(origin actor, origin execution, and any Praxis provenance record, verbatim).
When Chrona supplied no origin, Summa MUST record the origin as explicitly
`unknown`. Summa MUST NOT infer an origin from the approver, the Chrona user,
Git authorship, or the importing service, and MUST NOT upgrade `unknown` later
without a new, versioned source snapshot from Chrona.

### INV-PROV-004 Grouped invoice lines keep each source's origin (refines INV-CHR-010, INV-AUD-004)

An invoice line grouped from several source entries MUST retain each source's
origin, tied to that source, as lineage. Grouping MUST NOT merge authorship
into one actor, drop an origin, duplicate one, or invent one. The invoice line
itself has no "author" derived from its sources.

### INV-PROV-005 Issuance and accounting never require Praxis (refines §0.47, INV-AGENT-008)

Proposal, review, approval, issuance, posting, rendering, delivery, payment,
correction, and audit MUST NOT require Praxis (or Chrona) to be reachable in
order to read, validate, or keep origin provenance. Summa validates the origin
shape locally and carries it verbatim. A malformed origin blocks the import of
that source entry as an explicit, reviewable finding; it is never silently
dropped. A Praxis provenance record in an unsupported major version is carried
verbatim and not interpreted.

### INV-PROV-006 Identity is provenance, not authority (refines INV-AGENT-007, INV-AGENT-008, INV-REV-007)

Origin provenance is self-reported. It MUST NOT be treated as authentication,
authorization, approval, evidence of work performed, or grounds to price or
issue. INV-REV-007 approval provenance records the approver separately from
any source origin; an agent origin never satisfies a human-approval
requirement.

### INV-PROV-007 Audit carries actor, execution, and source origin (refines INV-AUD-002, INV-AGENT-005)

Material audit events MUST record the acting actor (INV-PROV-001) and its
Execution ID (INV-PROV-002) when relevant, and, for events on sources or lines
with origin lineage, reference that lineage verbatim. When a human changes an
agent-generated proposal (INV-AGENT-005), both contributions remain recorded:
the human change is appended; the agent's contribution is not overwritten.

### INV-PROV-008 No secrets

Actor, execution, origin, and audit fields MUST NOT contain credentials. A
source origin containing a credential-shaped value is malformed
(INV-PROV-005).

## 3. Contract

The receiver-owned contract for Chrona billing-source origins is
`summa.billing-source-origin` v1:
`contracts/billing-source-origin.v1.schema.json`. Any minor of major 1 is
accepted and unknown fields are preserved. It deliberately does not reference
Chrona's or Praxis's schema files; it restates only the fields Summa stores.

## 4. Traceability

| Requirement | Implementation (contract tier) | Verification |
|---|---|---|
| INV-PROV-001, 002, 003, 008 | `contracts/billing-source-origin.v1.schema.json`, `contracts/billing-source-origin.mjs` (`sourceOriginProblems`) | `contracts/test/billing-source-origin.test.mjs` (valid/invalid fixtures) |
| INV-PROV-004 | `groupingProblems` | `contracts/fixtures/billing-source-origin/v1/grouping/cases.json` |
| INV-PROV-005 | local codec `contracts/praxis-provenance/praxis-provenance-record.mjs`; no Praxis dependency | `contracts/test/praxis-provenance-conformance.test.mjs` (vendored fixtures, SHA-256 pinned) |
| INV-PROV-006, 007 | Requirement only; review, issuance, and audit are not yet implemented | Pending with the invoice slice |

The JavaScript in `contracts/` is a contract-verification harness, not product
code. The F# implementation (Requirement Set 0 §0.1.3) MUST pass the same
fixtures. Run `npm test`.
