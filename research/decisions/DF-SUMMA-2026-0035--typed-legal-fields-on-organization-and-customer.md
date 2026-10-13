---
id: DF-SUMMA-2026-0035
title: Typed legal fields on the organization and the customer, snapshotted at issue
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
  - research/decisions/DF-SUMMA-2026-0029--invoice-data-line-identity-details-references-rounding.md
  - research/decisions/DF-SUMMA-2026-0030--invoice-exports-and-the-large-invoice-scenario.md
tags: [international, legal-fields, records]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T14:04:30.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record legal field decisions (WI-0029 slice 14)"
---

# DF-SUMMA-2026-0035 — Typed legal fields, snapshotted at issue

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

INV-INTL-001: *"Invoice templates and organization/customer configuration
should allow jurisdiction-specific fields without requiring arbitrary schema
hacks."* Its examples are a tax registration number, a VAT ID, a business
registration number, a customer tax ID and statutory invoice wording.

Summa had only the issuer's single tax ID.

## Decisions

1. **Typed, not free-form.**
   - A `LegalField` has a kind (one of the five examples), a jurisdiction
     (an ISO 3166 country or subdivision code, such as `GB` or `US-NY`)
     and a value.
   - New kinds are new cases, not ad-hoc keys.
   - The organization may record every kind except a customer tax ID.
   - A customer may record every kind except statutory wording.
   - One field per kind and jurisdiction; values are not blank.
2. **Recorded as given; nothing inferred.** The value is shown exactly as
   recorded. Showing a field claims no compliance (INV-INTL-003), and the
   Settings page says so.
3. **Where they live.**
   - The organization's fields are their own record,
     `summa.legal-fields/organization`, not a key in the organization
     manifest. Adding a key there would need a manifest schema migration
     for every organization.
   - The record is written once the organization records a field. It is
     kept, even when it lists none, because financial records are never
     deleted (SUM3-034). `Books.LegalFields` is therefore an option.
   - A customer's fields are on the customer record.
   - Both are written under `legalFields`, a marked schema 2 key, only
     when there are some.
4. **Snapshotted at issue.**
   - The organization's fields are copied onto the issuer snapshot, and the
     customer's onto the customer snapshot, when the invoice is issued.
   - Later changes never reach an issued invoice (INV-DATA-004/005).
   - The template (`summa.invoice` 1.0.0) and the in-app document show
     them only when present, so earlier invoices render and hash as before.
     The template shows the party's fields with the party, and statutory
     wording in its own section.
   - The document JSON gains an optional `legalFields` array.
5. **Commands.** Adding and removing are commands with capabilities:
   ManageSettings for the organization, ManageBilling for a customer. A
   field is removed by its kind and jurisdiction, so a replay on books that
   changed meanwhile removes the same field.

## Consequences

- **INV-INTL-001 is partial.** The requirement names invoice templates as
  one of the places that must allow jurisdiction-specific fields.
  - A print template or a recurring template cannot yet carry its own
    fields, for example statutory wording that differs per template.
  - Only the organization's and the customer's fields apply, and the
    template renders them.
  - Per-template fields remain open under WI-0029.
