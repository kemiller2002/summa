---
id: DF-SUMMA-2026-0029
title: Line identity, line details, typed client references and a recorded rounding policy
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
  - research/decisions/DF-SUMMA-2026-0028--entry-references-settlement-and-explanations.md
tags: [invoicing, data, storage]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T11:54:02.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record invoice data decisions (WI-0029 slice 9, INV-DATA)"
---

# DF-SUMMA-2026-0029 — Invoice data

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-DATA-001..014 describe the invoice aggregate. Most of it existed. Four
parts did not:
- stable line identifiers (007);
- the optional line fields (008);
- a versioned rounding policy kept with the invoice (011);
- typed client references beyond the purchase order (014).

Issued invoices' HTML and JSON are re-rendered and hash-checked, so an
existing invoice must render byte for byte as before.

## Decisions

1. **Line identity is derived:** `lineId invoice index = "{invoiceId}/L{n}"`.
   An issued invoice and the order of its lines never change, so the id is
   stable without being stored, and no record or artifact changes. Draft
   lines are editable and referenced by nothing, so they need no id. Credit
   memos keep their positions, which map to these ids.
2. **`LineDetails`** (detailed description, unit, service period,
   category, customer code) is optional on a line.
   - It is stored as `lineDetails` and rendered under the description,
     both only when given. Lines without it render and store exactly as
     before.
3. **Typed client references:** `InvoiceDetails.References` holds
   contract, engagement, vendor, cost center and matter numbers. They
   appear as labelled rows on the document. They are stored as
   `clientReferences`, only when given. The purchase order and the free
   "your reference" stay as they were.
4. **The rounding policy is recorded.**
   - `roundingPolicy = "summa.rounding/1"`: integer minor units; quantity
     times rate, percentage discounts and allocations rounded half away
     from zero, line by line; totals as sums of rounded lines.
   - Every issued invoice records it (`rounding`), so new invoices are
     written at schema 2. Writing a new key at schema 1 would be refused by
     an older reader, which is why the version moves.
   - An invoice written before the field existed reads as
     `summa.rounding/1`. That is true: the rounding code is unchanged.
5. **Distinct keys.** The new keys (`lineDetails`, `clientReferences`,
   `rounding`) are distinct from existing ones, so no other record changes
   schema.

## Consequences

- INV-DATA is tested: a test per requirement in `InvoiceDataTests`, plus
  a storage round trip.
- Not yet: inputs for line details and typed references in the draft
  editor. Proposals and agents can set them.
