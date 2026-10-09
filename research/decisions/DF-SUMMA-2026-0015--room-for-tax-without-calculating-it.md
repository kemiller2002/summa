---
id: DF-SUMMA-2026-0015
title: Room for tax without calculating it, written at schema 2 only where tax is recorded
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0012--organization-manifest-schema-2-and-its-migration.md
  - docs/requirements/implementation-gap-analysis.md
tags: [invoicing, tax, schema, storage]
provenance:
  contributions:
    EXE-20261009T005401069Z-ca84f2de:
      operations: [created]
      at: 2026-10-09T01:14:23.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Give the model room for tax without calculating it (WI-0040, INV-ADJ-005)"
---

# DF-SUMMA-2026-0015 — Room for tax without calculating it

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0040

## Context

INV-ADJ-005 defers sales, VAT and GST calculation, but asks the model to
leave room for all of the following, without inferring tax obligations from
incomplete data:
- taxable and non-taxable lines;
- tax category or code;
- customer exemption;
- jurisdiction;
- rate source;
- tax amount;
- inclusive or exclusive pricing;
- evidence.

WI-0027 already had a typed tax adjustment, a code and a liability account.
Issued invoices are immutable records, so changing what an invoice record
holds is a schema question.

## Decisions

1. **What the person said, in three types** (`Summa.Ledger.Invoicing`):
   - `LineTax`: `NotAssessed`, `Taxable category` or `NonTaxable reason`.
   - `CustomerTax`: `TaxNotAssessed`, `SubjectToTax jurisdiction` or
     `TaxExempt(evidence, jurisdiction)`.
   - `TaxCharge` in `AdjustmentKind.Tax`: code, liability account,
     jurisdiction, rate source, rate (in hundredths of a basis point, so
     8.875% is exact), evidence, and `TaxExclusive` or `TaxInclusive`.
   "Not assessed" is the default and means exactly that: nothing was said.
2. **Summa adds no tax and infers none.** Taxable lines without a tax issue
   as entered. Readiness refuses only what contradicts itself:
   - a tax with no line marked taxable;
   - a tax on a customer recorded as exempt (the evidence is quoted);
   - more tax inside the prices than the taxable lines charge.
3. **Inclusive tax:**
   - The total is unchanged.
   - The posting debits the first revenue account and credits the tax's
     liability with the tax amount, so revenue is net of the tax and the
     entry balances.
   - The document labels the tax "(included in the prices above)", so no
     reader adds it again.
4. **Schema 2 only where tax is recorded.**
   - `summa.customer`, `summa.draft`, `summa.invoice` and
     `summa.proposal` read schema 1 and 2.
   - A record is written at schema 2 only when it records tax: a `tax` or
     `taxDetails` member (`schemaVersionOf`). A record without tax is
     written exactly as before, at schema 1.
   - Existing books therefore need no migration, an issued schema 1 invoice
     is never rewritten, and an older Summa still reads everything except a
     record that holds tax, which it refuses (fails safe) instead of
     misreading.

## Consequences

- `TaxTests` cover:
  - nothing inferred;
  - a tax with its details, which needs a taxable line;
  - the exempt customer;
  - inclusive posting and its document label.
- `FinancialRecordsTests` cover:
  - books without tax stay at schema 1;
  - tax recorded at schema 2 and read back exactly.
- The application's screens do not yet edit tax. Recording it there is
  part of the product UI (WI-0044); the domain and storage take it now.
