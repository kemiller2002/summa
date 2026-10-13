---
id: DF-SUMMA-2026-0038
title: The remaining domain operations join the contract as version 1.2; the close rule moves into the domain
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
  - research/decisions/DF-SUMMA-2026-0037--payment-commands-in-contract-1.1.md
  - docs/contracts/invoice-commands-1.2.md
tags: [contracts, integration-boundaries, periods]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T14:39:36.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the 1.2 domain operations (WI-0029 slice 17)"
---

# DF-SUMMA-2026-0038 — Domain operations in contract 1.2

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM2-031 (v0.2 §31) asks Summa to expose domain operations rather than
generic CRUD. It names seven: IssueInvoice, RecordPayment, AllocatePayment,
VoidInvoice, ApplyCreditMemo, ReversePayment and CloseAccountingPeriod.

Contract 1.1 had four of them. The rule that stops a month from closing
lived in the web engine, where an external caller would not meet it.

## Decisions

1. **1.2 adds `apply-credit-memo`, `reverse-payment` and `close-period`.**
   - Each runs the existing domain command: `Credits.apply`,
     `Credits.reversePayment` and `Ledger.closePeriod`.
   - The accounts and entry ids come from the host.
   - Messages are written at the oldest version that defines them, so 1.0
     and 1.1 messages are unchanged.
2. **The close rule is the domain's.**
   - `Periods.closeBlockers currency books period` decides whether a month
     may close: the trial balance balances at month end, and no payment
     received in the month is unapplied.
   - The page and the contract both use it. The page's `periodBlockers`
     now calls it, unchanged in behaviour.
   - A locked month is refused, and an already closed one is accepted
     unchanged.
3. **No generic record operations.** The contract has no PUT, PATCH or
   DELETE of records; an unknown command kind is refused. The store already
   refuses deleting or rewriting posted records (SUM0-027).

## Consequences

- Agents and integrations can act only through legal business transitions.
- Further operations, such as write-offs or deposits, are added as further
  minor versions when a caller needs them.
