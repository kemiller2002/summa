---
id: SUMMA-VIS
title: Summa semantic color and visual-intent requirements
status: draft
version: 0.1.0
created: 2026-10-17
updated: 2026-10-17
owners:
  - summa
related_documents:
  - app/summa-app.css
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - research/decisions/DF-SUMMA-FND-2026-0002--build-summa-on-the-full-echelon-foundation-stack.md
tags: [requirements, forma, color, accessibility, visual-engineering]
derived_from: [INPUT-950614e34eca68b4]
provenance:
  contributions:
    EXE-20261017T163958200Z-b0a63140:
      operations: [created]
      at: 2026-10-17T16:41:00.000Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Ingest Summa issue #110 as a draft canonical requirement without claiming product palette approval"
---

# SUMMA-VIS — semantic color and visual intent

Status: **Draft requirement**. Source: `INPUT-950614e34eca68b4`, preserving
GitHub issue [#110](https://github.com/kemiller2002/summa/issues/110).
Ingestion records the obligation; it does not approve Summa's palette or
substitute automated contrast checks for product review.

Work item: `GH-110`.

## Problem and boundary

`app/summa-app.css` was authored against Forma 0.6.1 with four unsupported
names: `--ef-color-action-primary`, `--ef-color-text-link`,
`--ef-color-focus`, and `--ef-color-text-danger`. Literal fallbacks therefore
determined their rendering. Summa must consume released Forma semantic roles
without forking Forma CSS, while retaining Summa's ownership of accounting
meaning and task hierarchy.

## Requirements

- **SUMMA-VIS-001.1 — Supported roles only.** Remove every unresolved or
  rebranded Forma color reference and arbitrary color fallback. Use a released
  semantic role or request a deliberate public role through Forma; never
  invent an alias.
- **SUMMA-VIS-001.2 — Product visual intent.** Record Summa's explicit palette,
  light/dark policies, and task hierarchy for financial totals, overdue or
  blocked work, draft versus posted or issued state, review, actionable versus
  informational status, navigation, focus, charts, and tables. Approval of
  this intent remains a product-review decision and belongs in the governed
  Ordo path once available.
- **SUMMA-VIS-001.3 — Accounting-critical states.** Check color and non-color
  signals for unpaid or overdue work, blocked issues, successful payment,
  failed save, unknown or pending state, selected navigation, validation,
  focus, and print-versus-screen output.
- **SUMMA-VIS-001.4 — Rendered evidence.** Capture rendered and computed-color
  evidence on representative screens and states in light and dark themes,
  mobile and desktop layouts, 200% zoom, keyboard focus, forced colors, and
  grayscale plus color-vision-deficiency screening. Contrast success is not
  design approval.
- **SUMMA-VIS-001.5 — Regression and shared validation.** Add failing
  regression tests for every original unsupported token and intentionally
  wrong-theme pairing. Integrate the published Forma/Dokimos validator and
  Praxis receipt gate when available; retain independent local checks until
  every shared gate is available.
- **SUMMA-VIS-001.6 — Ownership.** Do not copy or fork Forma CSS and do not
  alter accounting state or semantics to compensate for presentation defects.

## Acceptance criteria

- No unsupported `--ef-*` references or unapproved literal colors remain in
  first-party application CSS.
- Source-to-computed review demonstrates coherent navigation, primary actions,
  links, financial statuses, and focus in light and dark themes.
- Automated checks and visual-review evidence identify the inspected commits
  and explicitly name incomplete states.
- A product reviewer signs off on hierarchy and palette separately from
  accessibility and automated-test success.

## Dependencies and implementation state

- Forma issues [#159](https://github.com/kemiller2002/forma/issues/159) and
  [#160](https://github.com/kemiller2002/forma/issues/160) are implemented and
  released in Forma 0.9.0.
- Ordo issue [#92](https://github.com/kemiller2002/ordo/issues/92) governs the
  future decision-record path.
- Visual Engineering issue
  [#49](https://github.com/kemiller2002/visual-engineering/issues/49) governs
  product-level acceptance.
- The `echelon-current` registry must select Forma 0.9.0 before Summa may apply
  the dependency through Conditor. Direct edits to governed pins are not an
  acceptable workaround.

Implementation and automated evidence may complete before human palette and
hierarchy approval. This requirement remains draft until that independent
review is recorded.
