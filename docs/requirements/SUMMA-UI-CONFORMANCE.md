---
id: SUMMA-UI
title: Summa universal UI layout and action conformance
status: accepted
version: 1.0.0
created: 2026-10-18
updated: 2026-10-18
owners:
  - summa
related_documents:
  - app/index.html
  - app/summa-app.css
  - web/index.html
  - web-hub/index.html
  - web-kernel/page.css
  - tests/browser/ui-conformance.spec.js
tags: [requirements, forma, layout, interaction, accessibility, visual-engineering]
provenance:
  contributions:
    EXE-20261018T071903931Z-5035a47a:
      operations: [created]
      at: 2026-10-18T07:26:13.009Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Adopt the pinned universal UI layout and interaction contract for Summa issue #90"
---

# SUMMA-UI — universal UI layout and action conformance

Status: **Accepted requirement**. Source: Summa GitHub issue
[#90](https://github.com/kemiller2002/summa/issues/90), under the organization
rollout in Visual Engineering issue
[#47](https://github.com/kemiller2002/visual-engineering/issues/47).

Work item: `GH-90`.

## Versioned contract

Summa adopts version **1.0.0** of the
[Universal UI Layout and Interaction Standard](https://github.com/kemiller2002/visual-engineering/blob/8da8fbcdfa4344cc2ec248102e8a2741f9e95c08/framework/standards/UI-LAYOUT-INTERACTION.md),
pinned to the merge revision of Visual Engineering PR #46. The standard is
referenced, not copied. Forma remains the owner of shared components, tokens,
and reusable interaction presentation; Summa owns only its product
compositions and accounting meaning.

## Maintained surface classification

| Surface | Source | Classification | Boundary |
|---|---|---|---|
| Accounting application | `app/index.html` | product app UI | Local-first accounting routes, forms, tables, dialogs, and workflows |
| Work backlog | `web/index.html` | product app UI | One repository's Praxis work queue |
| Project administration hub | `web-hub/index.html` | product app UI | Registered repositories and their aggregated Praxis work |
| Invoice and credit documents | projections in `app/index.html` | print UI | Folio-backed print and PDF output |
| Backlog and hub reports | projections in `web/index.html` and `web-hub/index.html` | print UI | Folio-backed queue reports |

There are no maintained marketing, catalog, or live-demo surfaces. Therefore
the live-demo action rule is not applicable; product actions remain subject to
the stronger product-workflow rules below.

## Requirements

- **SUMMA-UI-001 — Meaningful alignment.** Forms, filters, action groups, and
  record layouts declare meaningful peer groups. Peer edges and equivalent
  control geometry remain within one CSS pixel after fonts and layout settle.
- **SUMMA-UI-002 — Responsive composition.** Maintained screen surfaces must
  avoid page-level horizontal overflow, clipping, and inaccessible actions at
  320, 390, 768, and 1280 CSS pixels. A 640 CSS-pixel viewport represents a
  1280-pixel viewport at 200% zoom. Long labels and right-to-left direction
  must reflow without hiding content or actions.
- **SUMMA-UI-003 — Truthful actions.** Every source action has an actual link,
  native form/dialog command, or Limen engine event. Critical actions must
  prove a distinct observable outcome; activation alone is not evidence.
  Disabled controls remain disabled only when the application state says the
  operation is unavailable.
- **SUMMA-UI-004 — Complete interaction paths.** Critical product workflows
  prove initial state, invocation, outcome, and a reset or recovery strategy.
  Expected refusals remain ordinary recoverable errors; operational faults
  remain Aegis faults. Keyboard activation uses the same path as pointer
  activation.
- **SUMMA-UI-005 — Semantic operation.** Controls keep native roles,
  programmatic names, labels, focus behavior, and machine-operable locators.
  Presentation and responsive changes must not replace semantic HTML.
- **SUMMA-UI-006 — Print fidelity.** Folio-backed documents must prove print
  visibility, screen-control suppression, table repetition and fragmentation,
  page geometry, overflow behavior, and actual multi-page Chromium PDF output
  where pagination is material.
- **SUMMA-UI-007 — Durable evidence.** A machine-readable evidence record must
  identify the standard version, repository revision, surface status,
  alignment groups, critical actions, viewport widths, exceptions, and any
  unknown scope. Playwright traces remain available on CI failure.

## Declared alignment groups

The conformance suite measures these peer relationships using semantic roles
and accessible names, not coordinates or brittle CSS selectors:

- backlog capture: `Tags` and `Priority`, top edge and control height;
- backlog filters: `Tag` and `Status`, top edge;
- hub registration: `Repository path` and `Display name`, top edge and control
  height;
- accounting customer identity: `Name` and `Billing name`, top edge and
  control height.

At phone widths these peers intentionally recompose into a vertical flow and
are checked for overflow rather than forced horizontal alignment.

## Verification boundary

`npm run test:ui-conformance` is the focused local gate. The same specification
is discovered by the full Playwright suite in CI. Existing browser tests remain
the outcome evidence for the complete backlog lifecycle, hub register/create/
unregister flows, accounting issue/payment/export paths, expected refusal and
recovery, Aegis failure separation, keyboard operation, and Folio print/PDF
behavior.

The adoption assessment found no reproducible Summa-specific layout or action
defect in the maintained surfaces at the pinned Forma 0.9.0 and Folio 0.4.0
baselines. Production CSS and markup therefore remain unchanged; the change is
the versioned requirement, deterministic enforcement, and durable evidence.
