# Summa handoff

## Objective

Build Summa, the invoicing, receivables and ledger application, on F# and the
Echelon foundation stack, under Praxis.

## Current state

- The implementation baseline (issue #4, GH-4) is in place: see
  [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md) and
  [`context/ARCHITECTURE.md`](context/ARCHITECTURE.md).
- The remaining requirements are ordered work items WI-0017..WI-0031
  ([`docs/requirements/backlog-plan.md`](docs/requirements/backlog-plan.md)).

## Financial core (2026-10-07)

- Requirement coverage is tracked in
  [`docs/requirements/implementation-gap-analysis.md`](docs/requirements/implementation-gap-analysis.md)
  (`SUMv-NNN` section ids and `INV-<FAMILY>` rows; baseline and current
  columns; `tests/Summa.Tests/GapAnalysisTests.fs` holds the counts).
  46 tested, 17 partial, 160 missing of 223 (baseline 0 / 1 / 222).
- `src/Summa.Ledger` is the pure financial domain: `Money`, `Ledger`
  (WI-0012), `Invoicing` (WI-0013), `Payments` and `Reports` (WI-0014). The
  v0.1 First Vertical Slice runs end to end as a test
  (`PaymentsReportsTests`): issue, pay, Paid, trial balance balances, P&L
  and balance sheet reflect it.
- Not built: GitHub storage (set 0), the invoice document on Folio (v0.1 §7,
  INV-DOC), the finance UI on Limen/Forma (v0.4), v0.2 edge cases beyond
  partial/multiple payments, v0.3 operational safety, the Chrona time
  integration (v0.1 §17, INV-CHR).

## Next action

Follow the backlog plan: WI-0017 (Chrona-to-Summa contracts package), then
storage on Arca, sign-in through Fides, authorization, and the invoicing,
receivables and ledger features.

## Validation

Run:

```bash
./praxis registry check
./praxis validate
dotnet test Summa.sln
```

## Unresolved questions

1. The deployment (Fides endpoint, GitHub App, Summa data repository) does not
   exist yet; storage and sign-in are proven against in-memory providers until
   it does.
