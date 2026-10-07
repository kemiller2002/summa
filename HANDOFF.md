# Summa handoff

## Objective

Bootstrap Summa as a greenfield Repository Operating System pilot.

## Current state

- ROS 2.0.1-main.78.1 greenfield profile installed on 2026-09-14.
- Project charter is a draft.
- No first vertical slice, evidence record, hypothesis, or experiment has been
  accepted.
- The operating system is under evaluation.

## Financial core (2026-10-07)

- Requirement coverage is tracked in
  [`docs/requirements/implementation-gap-analysis.md`](docs/requirements/implementation-gap-analysis.md)
  (`SUMv-NNN` section ids and `INV-<FAMILY>` rows; baseline and current
  columns; `tests/Summa.Tests/GapAnalysisTests.fs` holds the counts).
  19 tested, 5 partial, 199 missing of 223 (baseline 0 / 1 / 222).
- `src/Summa.Ledger` is the pure financial domain: `Money`, `Ledger`
  (WI-0012), `Invoicing` (WI-0013), `Payments` and `Reports` (WI-0014). The
  v0.1 First Vertical Slice runs end to end as a test
  (`PaymentsReportsTests`): issue, pay, Paid, trial balance balances, P&L
  and balance sheet reflect it.
- Not built: GitHub storage (set 0), the invoice document on Folio (v0.1 §7,
  INV-DOC), the finance UI on Limen/Forma (v0.4), v0.2 edge cases beyond
  partial/multiple payments, v0.3 operational safety, the Chrona time
  integration (v0.1 §17, INV-CHR).

## Next action (financial core)

Persist `Summa.Ledger` state in GitHub with deterministic paths and
optimistic concurrency (set 0), then the printable invoice on Folio and a
Limen/Forma invoice-and-payment workflow over the existing domain.

## Validation

Run:

```bash
./ros registry check
./ros validate
```

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
