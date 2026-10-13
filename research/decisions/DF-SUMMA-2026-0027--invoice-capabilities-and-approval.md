---
id: DF-SUMMA-2026-0027
title: The invoice capabilities INV-SEC-001 names, and approval as a gate
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
  - research/decisions/DF-SUMMA-2026-0022--delivery-authority-outcome-events-and-attention.md
tags: [security, access]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T11:12:57.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record how INV-SEC-001's capabilities map (WI-0029 slice 7)"
---

# DF-SUMMA-2026-0027 — Invoice capabilities and approval

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-SEC-001 asks Summa to distinguish capabilities "such as" fourteen
named ones. Summa's capability set (`Summa.Access`) already governs
every invoice operation. Four of the names have no capability of their
own: EditDraftInvoice, ApproveInvoice, ResendInvoice and
ViewSensitivePaymentInstructions.

## Decisions

1. **No new capabilities for these four.**
   - Editing a draft is drafting (`CreateDraftInvoice`). A draft has no
     financial effect until it is issued, and issuing is its own
     capability.
   - Approving is not a capability. It is the approval gate: a different
     person, holding the gated capability, approves what someone else
     prepared (`Access.canApprove`, `defaultApprovalGates`). Issuing,
     voiding, applying credit, refunding, writing off and overriding a rate
     are gated.
   - A resend is a delivery attempt (INV-DEL-006), governed by
     `DeliverInvoice` (DF-SUMMA-2026-0022).
   - There are no sensitive payment instructions to protect. A payment
     profile or any record holding a full account or card number is
     refused (DF-SUMMA-2026-0021), so instructions hold references only.
2. **Operations with different financial effect never share a
   capability.** `AccessTests` pins the mapping, and asserts that the ten
   governing capabilities are distinct.

## Consequences

- INV-SEC is tested.
- Adding new capabilities later would need a roster migration, because
  stored rosters name their capabilities explicitly. None is needed now.
