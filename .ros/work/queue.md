# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ECHELON-UPGRADE-2026-09-21 | Modernize Summa Echelon engineering capabilities | complete | tooling, ordo, limen | high |
| FOUNDATIONS-APPLICABILITY | FOUNDATIONS-APPLICABILITY | complete |  |  |
| GH-4 | Prepare Summa implementation baseline | complete | readiness,bootstrap | high |
| LIMEN-0-7-0 | LIMEN-0-7-0 | complete |  |  |
| LIMEN-0-7-0-APPLY | LIMEN-0-7-0-APPLY | complete |  |  |
| LIMEN-0-7-0-FND | LIMEN-0-7-0-FND | complete |  |  |
| LIMEN-0-7-0-VERIFIER | LIMEN-0-7-0-VERIFIER | complete |  |  |
| ROS-INSTALL-2-0-1-main-78-1 | ROS-INSTALL-2-0-1-main-78-1 | complete |  |  |
| SUMMA-SDE-UPGRADE-2026-09-21 | Upgrade legacy unmanifested SDE installation to 1.3.0 | complete | tooling, sde | high |
| WI-0001 | Vendor SDE methodology, Visual Engineering context, and WASM kernel dependency | complete | setup | medium |
| WI-0002 | Attribute pre-ROS setup artifacts (kernel dep, VE context, SDE) | complete | setup | medium |
| WI-0003 | Record and resolve CI deviation: ./ros F# launcher unusable, CI runs Node ros_cli.mjs | complete | governance, deviation, ci, ros | high |
| WI-0004 | Restore Aegis to required: true in .echelon/foundations.json, with EchelonFoundry.Aegis.Core, real boundary usage and aegis-boundaries.json, in the change that adds the first .NET/F# host or project (DF-SUMMA-FND-2026-0001) | complete | foundations | medium |
| WI-0005 | Restore Forma to required: true in .echelon/foundations.json, with the pinned @echelon-foundry/design-system, in the change that adds the first browser surface that is not the copied QDI-071 scaffold, including any replacement of web/ or web-hub/ (DF-SUMMA-FND-2026-0001) | complete | foundations | medium |
| WI-0006 | Restore Folio to required: true in .echelon/foundations.json, with the pinned @echelon-foundry/print-components, in the change that adds the first invoice, statement, receipt, report or other document output (DF-SUMMA-FND-2026-0001) | complete | foundations | medium |
| WI-0007 | Upgrade to Forma 0.4.1 and Limen 0.7.1, with a submit regression test | complete | foundations | high |
| WI-0008 | Move Summa to Praxis 3.7.1 (ROS -> Praxis rename) and Ordo 1.4.0 | complete | praxis, ordo, toolchain | medium |
| WI-0009 | Move summa to Ordo 1.4.1 | complete | ordo, toolchain | medium |
| WI-0010 | Move summa to Praxis 3.7.2, Ordo 1.4.2, Visual Engineering 1.0.1 and adopt Conditor | complete |  | medium |
| WI-0011 | Requirement gap analysis: compare every Summa requirement section and INV requirement family against code and tests | complete | requirements,gap-analysis | high |
| WI-0012 | Summa.Ledger: fixed-decimal money, chart of accounts, balanced journal entries, posting, reversal and accounting periods (v0.1 sections 1-2) | complete | ledger,accounting | high |
| WI-0013 | Invoices and receivables: customers, numbering, issue with AR/revenue posting, idempotency, obligations, payment terms (v0.1 sections 3-6, 9-10, 19) | complete | invoicing,receivables | high |
| WI-0014 | Payments, allocation, AR aging and core financial reports: trial balance, general ledger, income statement, balance sheet (v0.1 sections 11-15) | complete | payments,reports | high |
| WI-0015 | Move summa to Ordo 1.5.0 via echelon-current 1.2.0 (conditor upgrade --current) | complete |  | medium |
| WI-0016 | Capture the remaining Summa requirements as dependency-ordered backlog slices; add SUM-DATALOC-001 and record the 2026-10-08 decisions | complete | planning, requirements | high |
| WI-0017 | Summa 01: publish the Chrona-to-Summa contract as a small versioned contracts package (pure F# types and JSON codec), released through Summa's release workflow and registered in echelon-registry | complete | summa, order:01, contracts, chrona, package | high |
| WI-0018 | Summa 02: configurable data location, Summa-owned namespace, application and organization manifests (SUM-DATALOC-001, SUM0-005..011, SUM0-033, SUM0-043) | complete | summa, order:02, data-location, depends:arca | high |
| WI-0019 | Summa 03: GitHub sign-in through Fides, token handling, principals and capability authorization (SUM0-003, SUM0-004, SUM0-022, SUM0-023, SUM2-029, SUM3-012..015) | complete | summa, order:03, auth, depends:fides, depends:arca | high |
| WI-0020 | Summa 04: authoritative financial storage on Arca - one record per file, command-to-commit, optimistic concurrency, no silent rebase (SUM0-002, SUM0-012..021, SUM0-027, SUM0-028, SUM0-044..046, SUM3-001, SUM3-003, SUM3-005, SUM0-034, SUM1-019 remainder) | complete | summa, order:04, storage, depends:arca | high |
| WI-0021 | Summa 05: integrity and tamper detection, schema versioning and migration safety, integrity before availability (SUM0-029..032, SUM0-047, SUM3-004, SUM3-009, SUM3-010, SUM3-019) | complete | summa, order:05, integrity, versioning | high |
| WI-0022 | Summa 06: rebuildable runtime index, reconciliation jobs, failure recovery, backup and restore drills (SUM0-014, SUM0-015, SUM3-002, SUM3-006..008, SUM3-020, SUM3-044) | complete | summa, order:06, derived-state, recovery | high |
| WI-0023 | Summa 07: environment isolation, staging, environment visibility and scoped configuration (SUM0-038..042, SUM3-036..041) | complete | summa, order:07, environments | medium |
| WI-0024 | Summa 08: v0.2 receivables edge cases - multi-invoice payments, overpayments, deposits, corrections, voids, credit memos, refunds, bounced payments, write-offs, discounts, terms and late status (SUM2-003..013, SUM2-018, SUM2-019, SUM2-025, SUM2-027) | complete | summa, order:08, receivables, corrections | medium |
| WI-0025 | Summa 09: billing models - hourly from Chrona, fixed-price, milestone and reimbursable expenses (SUM2-014..017, INV-RATE, INV-TERM, INV-CHR) | complete | summa, order:09, billing-models, chrona | medium |
| WI-0026 | Summa 10: opening balances, CPA adjustments, year-end closing, cash versus accrual reporting (SUM2-021..024) | captured | summa, order:10, periods, accounting | medium |
| WI-0027 | Summa 11: invoice generation - document model, numbering, draft/issue/revise/correct/adjust/deliver lifecycle, rendered with Folio (SUM1-007, SUM0-037, SUM3-030, INV-DOC, INV-NUM, INV-ISS, INV-STATE, INV-DRAFT, INV-REV, INV-COR, INV-ADJ, INV-DEL, INV-PAYINST) | ready | summa, order:11, invoices, folio | medium |
| WI-0028 | Summa 12: audit provenance, audit immutability and agent execution safety (SUM1-018, SUM2-028, SUM2-030, SUM3-021, INV-AUD, INV-AGENT, SUM4-042..045, SUM0-048, SUM0-049) | captured | summa, order:12, audit, agents | medium |
| WI-0029 | Summa 13: operational safety - API contracts, observability, structured logging, correlation ids, sensitive data and secrets, import/export safety, deterministic calculations, time zones, outages and idempotent integrations, retention (SUM3-011, SUM3-016..018, SUM3-022..029, SUM3-031..035, INV-OPS, INV-FAIL, INV-SEC, INV-PERF, INV-EXP, INV-FX, INV-INTL, INV-ACC, INV-REC, INV-SET, INV-DATA, INV-CON, INV-ARCH) | captured | summa, order:13, operations | medium |
| WI-0030 | Summa 14: v0.4 product UI on Limen/Forma - dashboard, invoices, receivables, payments, follow-up, ledger, reports, period close, CPA workspace, work queue (SUM4-001..041, INV-UI, INV-A11Y) | captured | summa, order:14, ui, limen, forma | medium |
| WI-0031 | Summa 15: data export and escape hatch, v0.2 scenario tests, property, invariant and failure-injection tests (SUM1-020, SUM2-032, SUM3-042, SUM3-043, SUM0-001, SUM0-050) | captured | summa, order:15, quality | medium |
| WI-0032 | Stop the Chromium install from hanging the browser-suite CI (unbounded apt-get update in playwright install --with-deps) | complete | ci, playwright, reliability | high |
| WI-0033 | Fold draft PRs #6 and #7 (billing provenance, INV-PROV) into the backlog as Summa-owned requirements and close them | complete | housekeeping, provenance | high |
| WI-0034 | Move summa to echelon-current 1.7.0 with Conditor 0.6.0 and install Arca 0.2.0 and Fides 0.2.0 through Conditor | complete | toolchain, conditor, arca, fides | high |
| WI-0035 | Summa 03b: sign in with GitHub through Fides 0.2.0 in the application - Identity port over Limen requests, session actor github:<id>, token provider to Arca, never a token in state (SUM0-003, SUM0-004, SUM3-012) | captured | summa, order:03, auth, depends:fides | high |
| WI-0036 | Real end-to-end sign-in and storage check against the user's Fides deployment and a Summa data repository | captured | summa, e2e, blocked-on:deployment | medium |
| WI-0037 | Summa 04b: the application's store on GitHub - Arca's GitHub provider with the Fides token provider, start-up from the manifests, the offline queue on localStorage and conflict resolution in the interface (SUM0-002, SUM0-007, SUM0-018, SUM0-019) | captured | summa, order:04, storage, depends:arca, depends:fides | high |
| WI-0038 | Summa 06b: financial schema migrations - run a record-schema change as an explicit, versioned migration through Arca, checked by Verification.checkMigration before it is activated (SUM0-032, SUM3-010) | captured | summa, order:06, migration | medium |
| WI-0039 | Summa 14b: GitHub Pages deployment of the accounting app on summa.echelonfoundry.com (after WI-0030) | captured | deployment, pages | medium |
