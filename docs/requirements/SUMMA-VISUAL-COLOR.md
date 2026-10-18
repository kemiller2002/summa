---
id: SUMMA-VIS
title: Summa semantic color and visual-intent requirements
status: accepted
version: 1.0.0
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
      operations: [created, modified]
      at: 2026-10-17T16:41:00.000Z
      last: 2026-10-18T01:09:38.095Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Ingest Summa issue #110 as a draft canonical requirement without claiming product palette approval"
---

# SUMMA-VIS — semantic color and visual intent

Status: **Accepted requirement**. Source: `INPUT-950614e34eca68b4`, preserving
GitHub issue [#110](https://github.com/kemiller2002/summa/issues/110).
Automated contrast and accessibility checks remain evidence rather than a
substitute for product review.

Work item: `GH-110`.

## Product approval

The repository owner reviewed the proposed palette and accounting hierarchy
and authorized merge on 2026-10-17 (America/Indiana/Indianapolis). The public
approval record is attached to
[PR #111](https://github.com/kemiller2002/summa/pull/111#issuecomment-6244066478),
separately from the engineering and accessibility evidence.

## Problem and boundary

`app/summa-app.css` was authored against Forma 0.6.1 with five unsupported
names: `--ef-color-action-primary`, `--ef-color-text-link`,
`--ef-color-focus`, `--ef-color-text-danger`, and
`--ef-color-text-muted`. The first four were reported by issue #110; the fifth
was found when the published Forma validator was applied. Literal fallbacks
therefore determined their rendering. Summa must consume released Forma
semantic roles without forking Forma CSS, while retaining Summa's ownership
of accounting meaning and task hierarchy.

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

## Proposed product visual intent

This is the implementation baseline submitted for product review, not an
approval record. Summa inherits Forma's light and dark palettes through
semantic roles; it owns the accounting meaning and non-color cues below.

| Product purpose | Forma role or pairing | Required non-color signal |
|---|---|---|
| Page text and financial values | `text-primary` on `surface-primary` | Labels, tabular numerals, weight, and document structure |
| Supporting labels and notes | `text-secondary` on `surface-primary` | Position and explicit label relationship; never status by dimness |
| Brand mark and primary actions | `text-inverse` on `surface-inverse` | Visible action label and native button semantics |
| Links and selected navigation | `accent-primary` on `surface-primary` | Underline or link context; `aria-current` plus weight/underline for selection |
| Keyboard focus | `focus-gap` outline with `focus-ring` halo | Two-part outline geometry on `:focus-visible` |
| Successful payment or completion | `status-success` on `surface-primary` | Explicit success text plus border or status component |
| Failed save, invalid state, or overdue consequence | `status-danger` on `surface-primary` | Explicit error/overdue text plus fault or border treatment |
| Blocked work or warning | `status-warning` on `surface-primary` | Explicit blocker/warning text plus border treatment |
| Unknown, pending, or review state | `status-info` on `surface-primary` | Explicit pending/review wording; never promoted to success or failure |
| Tables and document boundaries | `border-subtle` with primary surfaces and text | Headers, captions, alignment, and responsive `data-label` values |
| Charts | No Summa chart palette is approved or currently used | Future charts require direct labels or patterns and contextual review |
| Print | Folio's print contract; semantic text remains authoritative | Status and amounts remain explicit in document text without screen color |

Every pairing follows the active Forma theme. Summa supplies no raw palette
values and does not freeze a light-theme value as a fallback. Color is
supplemental: accounting state remains visible in text, structure, native or
ARIA state, and borders or underlines after hue is removed.

## Dependencies and implementation state

- Forma issues [#159](https://github.com/kemiller2002/forma/issues/159) and
  [#160](https://github.com/kemiller2002/forma/issues/160) are implemented and
  released in Forma 0.9.0.
- Ordo issue [#92](https://github.com/kemiller2002/ordo/issues/92) governs the
  future decision-record path.
- Visual Engineering issue
  [#49](https://github.com/kemiller2002/visual-engineering/issues/49) governs
  product-level acceptance.
- `echelon-current` 1.24.0 selects Forma 0.9.0, Ordo 1.8.2, and Praxis
  3.12.0. Summa applied that exact release set through Conditor 0.10.6; the
  governed pins were not edited by hand.

Implementation, automated evidence, and the independent palette and hierarchy
review are complete. Future chart work still requires its own product review
because no chart palette was in scope for this approval.
