# Summa current state

Updated 2026-10-08 (GH-4: replace the bootstrap-era context with the
implementation baseline that now exists).

## Repository status

- Praxis 3.7.2, Ordo and Visual Engineering through Conditor
  (`conditor.json`, echelon-current channel); Limen/Forma/Folio and Aegis are
  required foundations (`.echelon/foundations.json`).
- Implementation baseline (issue #4) is in place:
  - Limen converged (LIMEN-0-7-0, WI-0007);
  - the F# tiers `Summa.Web.Engine` (pure decisions), `Summa.Web.Application`
    (Limen protocol, Aegis boundary), `Summa.Wasm` (browser host) and the
    `web/` pages on Forma;
  - the pure financial domain `Summa.Ledger` (WI-0012..WI-0014).
- The first customer/invoice/payment vertical slice (v0.1 "First Vertical
  Slice") runs end to end as a test (`PaymentsReportsTests`): create a
  customer, issue an invoice that posts AR/revenue, record a payment that posts
  cash/AR, the invoice reads Paid, the trial balance balances and the P&L and
  balance sheet reflect it.

## Observed facts

- Requirement coverage is tracked row by row in
  `docs/requirements/implementation-gap-analysis.md`; its counts are enforced
  by `tests/Summa.Tests/GapAnalysisTests.fs`.
- Every requirement row that is not yet `tested` is named by an open work item
  (WI-0017..WI-0031), ordered in `docs/requirements/backlog-plan.md`.
- Financial state is in memory only; nothing is stored in GitHub yet.

## Active work

Follow `docs/requirements/backlog-plan.md`: the Chrona-to-Summa contracts
package (WI-0017), then data location, sign-in and authorization, storage on
Arca, and the invoicing, receivables and ledger features.

## Largest decision-relevant unknown

How the deployment (Fides endpoint, GitHub App, data repository) will be
configured; nothing is deployed yet, so storage and sign-in are proven against
in-memory providers and the Arca/Fides conformance suites until it is.

## Baseline

`main` at `0e825a6` (2026-10-07): 0 tested, 1 partial, 222 missing of 223
requirement rows. See the gap analysis for the current column.
