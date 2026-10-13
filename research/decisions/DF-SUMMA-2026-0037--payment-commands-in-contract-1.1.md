---
id: DF-SUMMA-2026-0037
title: Payment commands join the invoice command contract as version 1.1
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
  - research/decisions/DF-SUMMA-2026-0036--versioned-external-invoice-command-contract.md
  - docs/contracts/invoice-commands-1.1.md
tags: [contracts, payments, versioning]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T14:26:35.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the payment commands of contract 1.1 (WI-0029 slice 16)"
---

# DF-SUMMA-2026-0037 — Payment commands in contract 1.1

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM3-011 (v0.3 §11) asks:

- that all external operations have explicit request and response
  contracts (IssueInvoiceCommand, RecordPaymentCommand,
  AllocatePaymentCommand) rather than loose JSON;
- that contracts are versioned;
- that breaking changes never silently affect agents or integrations.

Contract 1.0 (DF-SUMMA-2026-0036) covered issuing, not payments.

## Decisions

1. **A minor version, 1.1, adds `record-payment` and `allocate-payment`.**
   - The commands are additive, and nothing in 1.0 changes meaning.
   - Money is `{currency, minor}`: an ISO 4217 code and whole minor units,
     never a decimal.
   - The allocation's posting accounts and entry id come from the host,
     not the message.
2. **Each message is written at the oldest version that defines it.**
   - Every 1.0 message keeps the version `1.0` and its exact bytes; the
     golden vectors are unchanged. A 1.0 reader keeps reading what it
     always read.
   - Responses are still written at 1.0.
3. **Nothing new reaches an older reader silently.**
   - An unknown command kind is refused, so a 1.0 reader never acts on a
     1.1 command.
   - A message claiming 1.0 but carrying a 1.1 command is refused at
     `$.message.command.kind`.
   - Another major is refused.
4. **Each version has its own document.** `invoice-commands-1.0.md` stays
   as written. `invoice-commands-1.1.md` is complete in itself and lists
   what changed.

## Consequences

- **SUM3-011 is partial.** Agent requests in words (\`AgentRequests\`,
  INV-AGENT-001) read one documented phrasing, but they have no versioned
  request and response envelope. A change to that phrasing would not be
  versioned for the agents that use it. This remains open under WI-0029.

