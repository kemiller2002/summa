# Summa measurement baseline: where the evidence lives

Work item: WI-0049. This is an **index only**. It points at the evidence and
copies none of it. Hashes are commits in `kemiller2002/summa`. Dates are
commit dates (UTC). Status as of 2026-10-13, `main` at the commit that adds
this file.

## Evidence sources

| Source | Where | Range | Notes |
|---|---|---|---|
| Work items | `.ros/work/queue.json` (derived view `queue.md`) | WI-0001 … current | Lifecycle: capture → ready → start → checkpoint → complete. History is in git. |
| Work events | `.ros/events/events.jsonl` | from `4e444af` (2026-09-14) | Append-only. One line per transition and checkpoint. |
| Execution telemetry | `.ros/telemetry/executions/EXE-*.json` | first record added in `54567ac` (2026-09-14) | One file per execution, with its steps, measurements and availability. Token and cost metrics are recorded as `supported-unavailable` from 2026-10-13 (WI-0029, WI-0047, WI-0048, WI-0049). They are not observed. |
| Execution envelopes | `.ros/executions/EXE-*/` | where Praxis created them | Envelope and events for executions started with `work start`. |
| Metric definitions | `telemetry/metrics.json` | from `4e444af` | Normalized metric ids (`tokens.*`, `cost.*`, …). |
| Decision records | `research/decisions/DF-SUMMA-*.md`; index `registries/decisions.json` (`praxis registry build`) | DF-SUMMA-ROSCLI-2026-0001 (`02289dd`) … DF-SUMMA-2026-0025 | CI checks the registry is current. |
| Requirement coverage | `docs/requirements/implementation-gap-analysis.md` | baseline `0e825a6` as the document names it: 0 tested, 1 partial, 234 missing of 235 | The "Current" column is updated by each change. The summary row is at the end. |
| Pull requests | GitHub `kemiller2002/summa` pulls | #1 (2026-09-14) … current | PR bodies from 2026-10-13 (#64 onward) carry a Measurement section. |
| CI history | GitHub Actions runs | first run 2026-09-14 (`54567ac`, "ROS validation"); 1208 runs as of 2026-10-13T10:56Z | Workflows are in `.github/workflows/`: build, praxis-validation, echelon-foundations, limen-verify, pages, contracts-release, readiness-work. |
| Release selection | `conditor.json`, `.conditor/lock.json`, `.conditor/authority/resolved-release-set.json` | from `f6cdac5` (2026-10-06) | The component versions Conditor selected, per commit. |

## Capability adoption

| Capability | Version | Commit | Date | Work item / PR |
|---|---|---|---|---|
| ROS (Praxis's predecessor), project-administration profile | 2.0.1-main.78.1 | `4e444af` | 2026-09-14 | PR #1 |
| WASM kernel (`@echelon-foundry/typescript-wasm-kernel`) | — | `66ee26f` | 2026-09-14 | PR #1 |
| Echelon application foundations enforced in CI | — | `eaf74ad` | 2026-09-26 | direct commit (no PR) |
| Limen | 0.7.0 | `6a2270b` | 2026-10-05 | PR #12 |
| Aegis, Forma, Folio (full foundation stack) | — | `337c4f1` | 2026-10-05 | WI-0004..0006, PR #13 |
| Forma 0.4.1, Limen 0.7.1 | — | `bed543d` | 2026-10-06 | WI-0007, PR #14 |
| Praxis, Ordo | Praxis 3.7.1, Ordo 1.4.0 | `3f3b59c` | 2026-10-06 | WI-0008, PR #15 |
| Ordo | 1.4.1 | `493596e` | 2026-10-06 | WI-0009, PR #16 |
| Conditor adopted (`conditor adopt` against the echelon-current registry at its revision f0e45db, as the commit message records; not a Summa commit); Praxis 3.7.2, Ordo 1.4.2 | — | `f6cdac5` | 2026-10-06 | WI-0010, PR #17 |
| Ordo via echelon-current 1.2.0 | Ordo 1.5.0 | `58430a7` | 2026-10-08 | WI-0015, PR #23 |
| Arca, Fides through Conditor (echelon-current 1.7.0) | Arca 0.2.0, Fides 0.2.0 | `6ac3ed1` | 2026-10-08 | WI-0034, PR #27 |
| Limen F# routing package (limen-fsharp); Arca | Limen 0.9.0; Arca 0.2.1 | `dea81cb` | 2026-10-08 | WI-0042, PR #48 |
| Praxis, Arca through Conditor 0.8.2 (echelon-current 1.17.0) | Praxis 3.11.0, Arca 0.4.0 | `7df0c80` | 2026-10-09 | WI-0045, PR #61 |
| Arca (echelon-current 1.18.0) | 0.4.1 | `f763bf6` | 2026-10-09 | WI-0046, PR #62 |
| Arca (echelon-current 1.19.0); pinned reads adopted | 0.5.0 | `5cef90f` | 2026-10-13 | WI-0050, PR #77 |
| Forma and Folio declared through Conditor 0.9.0 (echelon-current 1.19.0) | Forma 0.5.0, Folio 0.3.0 | `15289cf` | 2026-10-13 | WI-0052, PR #86 |
| Forma and Folio through Conditor 0.10.0 (echelon-current 1.20.0) | Forma 0.6.1, Folio 0.4.0 | `8b3c524` | 2026-10-13 | WI-0052, PR #87 |
| Ordo through Conditor 0.10.1 (echelon-current 1.21.0) | Ordo 1.6.0 | `32fe1ad` | 2026-10-13 | WI-0052, PR #92 |
| Ordo through Conditor 0.10.1 (echelon-current 1.22.0) | Ordo 1.7.0 | `54d185e` | 2026-10-13 | WI-0052, PR #93 |
| Praxis, Ordo, Forma through Conditor 0.10.6 (echelon-current 1.24.0) | Praxis 3.12.0, Ordo 1.8.2, Forma 0.9.0 | — | 2026-10-17 | GH-110 / this PR |
| Visual Engineering | 1.0.1 (in the Conditor set since `f6cdac5`); context 0.1.0 synced in `7718b18` | — | — | — |

## Not adopted in this repository (as of 2026-10-13)

- **Ordo construction** (ECIR, typed plans, `praxis construct`): Ordo is
  installed, but no work item was built through construction.
- **Dokimos**: no checks configured. The name appears only in Praxis
  schemas and the Conditor release set.
- **Checklist enforcement** and **OS containment**: not used by any work
  item.

## Missing or partial

- **Strict merge gate.** "Merge only when every push and pull_request check
  on the exact head SHA completed with success, merging with `sha=`" is a
  program rule.
  - The script that enforces it lives in the executor's session scratchpad,
    not in this repository.
  - Merge commits do not record the `sha=` precondition.
  - So when it was adopted cannot be read from the repository. Evidence is
    in session transcripts and, from #64 onward, in PR bodies.
- **Token and cost usage.** No provider usage is available to the executors
  that built Summa. Executions before 2026-10-13 do not record availability
  at all. From 2026-10-13 they record `supported-unavailable`. Nothing was
  estimated.
- **Execution parentage.** Before 2026-10-13, executions do not record the
  coordinating session as a parent. Successor executors continued their
  predecessor's execution id when Praxis treated the session as the same
  (WI-0029: `EXE-20261009T031528777Z-d8d5aa1a`).
- **Repository history before `6290d36`** was missing from shallow clones.
  A full clone has it from `86c0455` (2026-09-14).
- **CI run logs** expire under GitHub's retention. Only run metadata
  (names, events, conclusions, times) is durable.
