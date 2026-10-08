# Summa backlog plan

Captured 2026-10-08 (WI-0016) from the missing and partial rows of [`implementation-gap-analysis.md`](implementation-gap-analysis.md). Build order, per the user (DF-SUMMA-2026-0001): Chrona first, **then Summa**, then the rest. WI-0017, the Chrona-to-Summa contracts package, is pulled forward because Chrona's integration item consumes it; Summa owns the contract because it accepts the data. Storage and sign-in slices depend on Arca (kemiller2002/arca) and Fides (kemiller2002/fides). `GapAnalysisTests` fails if any row that is not yet `tested` is not named by an open work item.

| Order | Work item | Slice | Depends on |
|---:|---|---|---|
| 1 | WI-0017 | Summa 01: publish the Chrona-to-Summa contract as a small versioned contracts package (pure F# types and JSON codec), released through Summa's release workflow and registered in echelon-registry | - |
| 2 | WI-0018 | Summa 02: configurable data location, Summa-owned namespace, application and organization manifests (SUM-DATALOC-001, SUM0-005..011, SUM0-033, SUM0-043) | arca slice 2 (data location, ARCA-LOC) and arca slice 3 (record format, ARCA-REC) |
| 3 | WI-0019 | Summa 03: GitHub sign-in through Fides, token handling, principals and capability authorization (SUM0-003, SUM0-004, SUM0-022, SUM0-023, SUM2-029, SUM3-012..015) | fides slice 7 (WASM client and Arca token provider, FID-CLI) and arca slice 5 (token-provider port, ARCA-AUTH) |
| 4 | WI-0020 | Summa 04: authoritative financial storage on Arca - one record per file, command-to-commit, optimistic concurrency, no silent rebase (SUM0-002, SUM0-012..021, SUM0-027, SUM0-028, SUM0-044..046, SUM3-001, SUM3-003, SUM3-005, SUM0-034, SUM1-019 remainder) | WI-0018, WI-0019, arca slice 6 (GitHub adapter, ARCA-API/OUT) and arca slice 8 (integrity, ARCA-INT) |
| 5 | WI-0021 | Summa 05: integrity and tamper detection, schema versioning and migration safety, integrity before availability (SUM0-029..032, SUM0-047, SUM3-004, SUM3-009, SUM3-010, SUM3-019) | WI-0020 |
| 6 | WI-0022 | Summa 06: rebuildable runtime index, reconciliation jobs, failure recovery, backup and restore drills (SUM0-014, SUM0-015, SUM3-002, SUM3-006..008, SUM3-020, SUM3-044) | WI-0020 and arca slice 10 (derived indexes and migration, ARCA-MIG) |
| 7 | WI-0023 | Summa 07: environment isolation, staging, environment visibility and scoped configuration (SUM0-038..042, SUM3-036..041) | WI-0018 |
| 8 | WI-0024 | Summa 08: v0.2 receivables edge cases - multi-invoice payments, overpayments, deposits, corrections, voids, credit memos, refunds, bounced payments, write-offs, discounts, terms and late status (SUM2-003..013, SUM2-018, SUM2-019, SUM2-025, SUM2-027) | WI-0020 |
| 9 | WI-0025 | Summa 09: billing models - hourly from Chrona, fixed-price, milestone and reimbursable expenses (SUM2-014..017, INV-RATE, INV-TERM, INV-CHR) | WI-0017 and WI-0024 |
| 10 | WI-0026 | Summa 10: opening balances, CPA adjustments, year-end closing, cash versus accrual reporting (SUM2-021..024) | WI-0020 |
| 11 | WI-0027 | Summa 11: invoice generation - document model, numbering, draft/issue/revise/correct/adjust/deliver lifecycle, rendered with Folio (SUM1-007, SUM0-037, SUM3-030, INV-DOC, INV-NUM, INV-ISS, INV-STATE, INV-DRAFT, INV-REV, INV-COR, INV-ADJ, INV-DEL, INV-PAYINST) | WI-0024 and WI-0025 |
| 12 | WI-0028 | Summa 12: audit provenance, audit immutability and agent execution safety (SUM1-018, SUM2-028, SUM2-030, SUM3-021, INV-AUD, INV-AGENT, SUM4-042..045, SUM0-048, SUM0-049) | WI-0019 and WI-0020 |
| 13 | WI-0029 | Summa 13: operational safety - API contracts, observability, structured logging, correlation ids, sensitive data and secrets, import/export safety, deterministic calculations, time zones, outages and idempotent integrations, retention (SUM3-011, SUM3-016..018, SUM3-022..029, SUM3-031..035, INV-OPS, INV-FAIL, INV-SEC, INV-PERF, INV-EXP, INV-FX, INV-INTL, INV-ACC, INV-REC, INV-SET, INV-DATA, INV-CON, INV-ARCH) | WI-0020 |
| 14 | WI-0030 | Summa 14: v0.4 product UI on Limen/Forma - dashboard, invoices, receivables, payments, follow-up, ledger, reports, period close, CPA workspace, work queue (SUM4-001..041, INV-UI, INV-A11Y) | WI-0027 and WI-0028 |
| 15 | WI-0031 | Summa 15: data export and escape hatch, v0.2 scenario tests, property, invariant and failure-injection tests (SUM1-020, SUM2-032, SUM3-042, SUM3-043, SUM0-001, SUM0-050) | WI-0022, WI-0027 and WI-0029 |

The backlog itself lives in `.ros/work/queue.json` and is managed only through the Praxis CLI. This table is a readable snapshot from when the slices were captured.

## Housekeeping (2026-10-08)

- Draft PRs #6 and #7 (billing provenance) are folded into
  [`SUMMA-PROVENANCE.md`](SUMMA-PROVENANCE.md) (INV-PROV-001..008,
  DF-SUMMA-2026-0002); WI-0017, WI-0019, WI-0025, WI-0027 and WI-0028 name the
  INV-PROV requirements they carry. PR #9 (CI debounce) was closed: it edited a
  workflow that no longer exists.
- GH-4 (implementation baseline, issue #4) is complete: the context files now
  describe the baseline that WI-0007 and WI-0012..WI-0014 built.
- WI-0001..WI-0003 still read `ready` in the raw `queue.json` `status` field
  although their live records are complete (`queue.md` shows complete). They
  were completed before Praxis started marking the backlog row complete, and
  the CLI has no transition that corrects a completed row (abandoning would
  misstate them), so they are left as they are.
