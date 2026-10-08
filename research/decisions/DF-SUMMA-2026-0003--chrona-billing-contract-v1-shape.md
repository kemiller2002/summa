---
id: DF-SUMMA-2026-0003
title: Shape of the Chrona-to-Summa billing contract, version 1
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0001--summa-owns-the-chrona-contract-and-stores-through-arca.md
  - docs/contracts/chrona-billing.md
  - docs/requirements/SUMMA-PROVENANCE.md
tags: [contracts, chrona, versioning, release]
provenance:
  contributions:
    EXE-20261008T150340202Z-1bdf96a1:
      operations: [created]
      at: 2026-10-08T15:13:24.222Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the shape of the Chrona-to-Summa billing contract v1"
---

# DF-SUMMA-2026-0003 — Chrona-to-Summa billing contract, version 1

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0017

DF-SUMMA-2026-0001 settled who owns the contract (Summa) and how it is
distributed (a versioned contracts package, registered in echelon-registry,
pinned by Chrona through `conditor.json`). The requirements do not settle its
shape. These choices fit what Chrona already records (`Chrona.Domain.Review`,
`Chrona.Domain.Billing`, `Chrona.Domain.Activity`), so Chrona's WI-0037 maps
existing values rather than inventing new ones.

## Decisions

1. **Messages.**
   - Chrona to Summa:
     - `billable-time-published` (one activity at one revision: its
       classification, exact and billable minutes with the billing policy id
       and version, billing references, approval, origin and lineage);
     - `publication-withdrawn` (voided, replaced by split/merge, or no
       longer billable).
   - Summa to Chrona:
     - `invoiced` (field for field Chrona's `InvoiceReport`, plus the
       organization id for routing);
     - `adjustment-required`.
   - Identifiers and minutes only; money stays in Summa (Chrona 8, 17).
2. **Corrections are explicit.**
   - A corrected activity is republished at its new revision with a new
     publication id and `supersedes` naming the earlier one.
   - Time that is no longer billable is withdrawn.
   - Summa never infers a change from a missing message (Chrona 17,
     INV-CHR-008, INV-CHR-012).
3. **Idempotency.**
   - The publication id is the idempotency key.
   - `Codec.digest`, the SHA-256 of the canonical bytes, tells a retry from a
     conflicting reuse.
4. **Canonical JSON.**
   - Members are sorted, there is no whitespace, escaping is minimal, and
     numbers are whole numbers only (RFC 8785 for these value types).
   - Optional fields are always present, as `null` when absent.
   - Instants carry their offset with seven fractional digits, so they
     round-trip exactly.
5. **Versioning.**
   - The wire contract is `summa.chrona-billing` `MAJOR.MINOR`, separate from
     the package version.
   - A reader accepts its own major. It ignores members of a newer minor and
     refuses unknown members at its own minor or older, so typos fail loudly.
   - A new major is a new namespace (`V2`), so two majors can be read side by
     side (Chrona 18).
6. **Origin and secrets.**
   - Origin is `unknown` unless Chrona states it, and it is carried verbatim
     (INV-PROV-003).
   - Credential-shaped text is refused in every field (INV-PROV-008).
7. **Package and release.**
   - `EchelonFoundry.Summa.Contracts` depends only on FSharp.Core.
   - It ships its golden vectors as embedded resources, so Chrona tests
     against the same bytes.
   - It is released as Echelon system `summa-contracts` (`nuget-library`) by
     `.github/workflows/contracts-release.yml`, tag `contracts-v<version>`,
     with Sigstore attestations. It is not on nuget.org; Conditor installs it
     into `vendor/nuget`.
   - The tag prefix keeps contract releases apart from any future Summa
     application release.

## Consequences

- Chrona's WI-0037 maps:
  - `Review.PublicationRecord` plus the activity to `BillableTime`;
  - `Ledger`'s AdjustmentRequired transitions to republication or
    withdrawal;
  - `Invoiced` to `recordInvoiced`.
- A contract change that alters canonical bytes fails the pinned-digest
  test and needs a deliberate version decision.
