---
id: DF-SUMMA-2026-0036
title: A versioned contract for external invoice commands
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
  - research/decisions/DF-SUMMA-2026-0001--summa-owns-the-chrona-contract-and-stores-through-arca.md
  - research/decisions/DF-SUMMA-2026-0023--issue-requires-the-reviewed-version.md
tags: [contracts, commands, versioning]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T14:15:26.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the invoice command contract (WI-0029 slice 15)"
---

# DF-SUMMA-2026-0036 — A versioned contract for external invoice commands

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-CONTRACT-002: *"External invoice commands shall have explicit versioned
request/response contracts."* Summa's commands were F# messages inside the
application. Only the Chrona billing seam had a wire contract.

## Decisions

1. **One contract, `summa.invoice-commands` 1.0.**
   - A request is `{requestId, command}`, in an envelope naming the
     contract, the kind and the version.
   - The commands are domain operations (INV-CONTRACT-003): propose an
     invoice from time, submit a draft version for review, issue a
     reviewed draft version, and void an invoice.
   - A response returns the `requestId` with exactly one outcome:
     - accepted, with what was produced;
     - refused, with coded reasons;
     - a conflict, naming the draft's current version (INV-CON-001).
2. **The same versioning rules as the Chrona contract.**
   - Canonical JSON, built on `Summa.Contracts.Json`.
   - Strict parsing, and every problem reported with its JSON path.
   - A newer minor of major 1 is read, ignoring the members it adds; any
     other major is refused. A new major is a new contract version beside
     this one.
3. **Who acts is not in the message.** The host authenticates the caller
   and passes the `Context`, so a request cannot claim another identity.
4. **Kept inside Summa, not in the published contracts package.**
   - `EchelonFoundry.Summa.Contracts` is released whenever it changes on
     main, and it is the Chrona billing contract.
   - This contract lives in `Summa.Ledger.InvoiceCommands`, reusing the
     package's JSON and decoding helpers. Its golden vectors are under
     `tests/Summa.Tests/vectors/invoice-commands`.
   - Publishing it as a package is a later decision, for when an external
     caller needs one.
5. **Idempotency.** `requestId` is the caller's key, returned in the
   response. Repeating an issue request returns the same invoice and
   changes nothing, because issuing is idempotent per draft (INV-CON-007).

## Consequences

- There is no network endpoint yet. A host hands decoded requests to
  `InvoiceCommands.handle`.
- Business events (INV-CONTRACT-004) remain open.
