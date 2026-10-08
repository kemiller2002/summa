---
id: SUM-PROV
title: Summa billing provenance requirements (INV-PROV)
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
related_documents:
  - research/decisions/DF-SUMMA-2026-0002--fold-draft-provenance-prs-into-summa-requirements.md
  - docs/requirements/backlog-plan.md
tags: [requirements, provenance, chrona, audit]
provenance:
  contributions:
    EXE-20261008T145558830Z-61dab5d8:
      operations: [created]
      at: 2026-10-08T14:57:26.519Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Fold draft PRs #6 and #7 into Summa-owned INV-PROV requirements"
derived_from: [DF-SUMMA-2026-0002]
---

# Summa billing provenance requirements (INV-PROV)

Status: **Required** (adopted by DF-SUMMA-2026-0002)
Work item: WI-0033 (folded from draft PRs #6 and #7, which are closed)
Date: 2026-10-08

These requirements refine, without editing the corpus in `input-documents/`,
Requirement Set 0 §0.23 (human, agent and service identity) and §0.25
(cross-application references), and the invoice-generation requirements
INV-AUD-002, INV-AUD-004, INV-REV-007, INV-AGENT-005..008, INV-CHR-002,
INV-CHR-010 and INV-SOURCE-008. Identity and provenance shapes are owned by
Praxis (`docs/agent-provenance.md`, RQ-ROS-2026-A001, A002, A013..A019) and
are referenced, not restated. MUST, MUST NOT, SHOULD and MAY are normative.

They are not corpus rows, so they are not rows of the gap analysis; the rows
they refine are. Each one is carried by the work item named in its heading.

## INV-PROV-001 Praxis-compatible actor (WI-0028)

Where §0.23 and INV-AUD-002 require an actor, actor type and agent identity,
Summa holds a Praxis-compatible actor: `kind` (`agent`, `human`,
`automation`, `unknown` or an `x-` extension), `id`, and for non-humans
`provider`, `model` and `runtime` (literal `unknown` when not known),
preserving fields it does not model. Human maps to `human`, Agent to `agent`,
Service to `automation`. An agent action MUST NOT be recorded as a human
action; an unknown actor MUST NOT be recorded as a human.

## INV-PROV-002 Execution ID is an opaque reference (WI-0028)

An execution id (`EXE-...`, a foreign `EXE-<system>.<run>`, or `CTB-...`) is
stored exactly as supplied. Summa MUST NOT parse meaning from it beyond its
syntax and MUST NOT dereference it to authorize, approve or price anything.
Summa MUST NOT mint a Praxis-shaped execution id; a Summa run that needs one
uses `EXE-summa.<run>`.

## INV-PROV-003 Chrona billing sources keep their origin (WI-0017, WI-0025)

Each billable source Chrona publishes keeps the origin Chrona published with
it (who performed the work, how it was entered, and the observation it came
from), carried verbatim. When Chrona supplies no origin, the origin is
explicitly `unknown`. Summa MUST NOT infer an origin from the approver, the
Chrona user, Git authorship or the importing service, and MUST NOT upgrade
`unknown` later without a new, versioned publication from Chrona.

## INV-PROV-004 Grouped invoice lines keep each source's origin (WI-0025, WI-0027)

An invoice line grouped from several sources retains each source's origin,
tied to that source, as lineage. Grouping MUST NOT merge authorship into one
actor, drop, duplicate or invent an origin. The line has no author derived
from its sources.

## INV-PROV-005 Issuance and accounting never require Praxis or Chrona (WI-0025)

Proposal, review, approval, issuance, posting, rendering, delivery, payment,
correction and audit MUST NOT require Praxis or Chrona to be reachable to
read, validate or keep origin provenance. A malformed origin blocks the
import of that one source as an explicit, reviewable finding; it is never
silently dropped. A provenance record in an unsupported major version is
carried verbatim and not interpreted.

## INV-PROV-006 Identity is provenance, not authority (WI-0019, WI-0028)

Origin provenance is self-reported. It MUST NOT be treated as
authentication, authorization, approval, evidence of work performed, or
grounds to price or issue. Approval provenance (INV-REV-007) records the
approver separately from any source origin; an agent origin never satisfies a
human-approval requirement. Capability authorization (§0.22) stays separate.

## INV-PROV-007 Audit carries actor, execution and source origin (WI-0028)

Material audit events record the acting actor (INV-PROV-001) and, when
relevant, its execution id (INV-PROV-002), and reference the lineage of the
sources or lines they touch. When a human changes an agent-generated proposal
(INV-AGENT-005) both contributions remain: the human change is appended and
the agent's contribution is not overwritten.

## INV-PROV-008 No secrets (WI-0017, WI-0028)

Actor, execution, origin and audit fields MUST NOT contain credentials. A
source origin holding a credential-shaped value is malformed (INV-PROV-005).

## Not carried forward

Draft PR #6 also specified identity handling for the project-administration
hub's dispatches to spoke repositories (its INV-PROV-005..007, 009, 011 and
012). The hub is now Praxis tool-owned (`praxis hub`), so that behaviour
belongs to Praxis, not Summa. Its rule that repository automation records
itself as `automation`, never as an agent, is Praxis RQ-ROS-2026-A003 and
already applies.
