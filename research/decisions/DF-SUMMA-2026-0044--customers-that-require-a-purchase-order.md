---
id: DF-SUMMA-2026-0044
title: A customer may require a purchase order; an invoice for them is not issued without one
status: accepted
version: 1.1.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0031--invoice-search-timeline-drill-down-and-attention.md
tags: [invoicing, customers, operations]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T19:24:53.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the required purchase order (WI-0029 slice 24, INV-OPS-006)"
---

# DF-SUMMA-2026-0044 — Customers that require a purchase order

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-OPS-006 lists "Missing PO" among the obligations the work queue shows.
DF-SUMMA-2026-0031 left it open because Summa had no way to say that a
customer requires a purchase order.

Many accounts payable departments reject an invoice without their purchase
order number. An invoice issued without one is not paid until it is
corrected by a credit memo and reissued, and issued invoices are
immutable.

## Decisions

1. **The requirement belongs to the customer.** `Customer.RequiresPurchaseOrder`
   is stored on `summa.customer` as `requiresPurchaseOrder: true`.
   - It is written only when true, so every other customer keeps its bytes.
   - The key is one of the schema-2 marks, so a customer that requires a
     purchase order is written at schema 2. An absent key reads as false.
2. **An invoice for such a customer is not issued without one.** Issuing a
   draft for a customer that requires a purchase order, when the draft has
   none, is refused with `PurchaseOrderRequired`. The readiness code is
   `missing-po`: "Customer … requires a purchase order on every invoice,
   and this draft has none."
   - The rule is in the domain's issue checks. The editor, agents, the
     external command contract (an open-set refusal code; noted in the 1.2
     document) and the recurring run's later issue all meet it.
   - A draft can still be saved and proposed without a purchase order. Only
     issuing waits.
3. **The work queue shows each draft that lacks one.** It appears as
   "Missing PO", with the customer's name and the draft's address. Issued
   invoices never appear, since they cannot lack one.
4. **People set it where they look after customers.**
   - The add-customer form has "Their accounts payable needs a purchase
     order on every invoice".
   - The customer's page has "Invoices for this customer need a purchase
     order", which saves the change at once.
   - The draft editor says "Required: …" under the purchase order field
     when the chosen customer requires one.
   - Changing it on the customer's page is a change to the books. It needs
     `ManageBilling`, and on GitHub-backed books it is committed like any
     other change (`bookCommand`). #98 shipped without this; the fix added
     `BookCommandTests`, which fails for any message that is neither a
     book command nor listed as changing no books.
5. **Checkboxes carry their checked state.** Both checkboxes send whether
   they are checked, never a toggle, as the tax checkboxes do. Limen's event
   value for a checkbox is its `value` attribute, so the wire reads
   `checked` for the events it lists.

## Consequences

- INV-OPS-006 is closed for "Missing PO". "Payment promise missed" remains
  until Summa records payment promises (SUM4-016).
- The tests are:
  - `InvoicingTests`: refused without a PO, issued with one, and a customer
    that does not require one;
  - `FinancialRecordsTests`: the round trip, bytes and schema;
  - `AttentionTests` "INV-OPS-006": the customer page, the add form, the
    editor note, the readiness blocker and the work queue;
  - `editor.spec.js`: the same in a browser, through the wire.
