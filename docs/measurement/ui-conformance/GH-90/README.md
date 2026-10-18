---
id: EV-SUMMA-UI-2026-0001
title: Summa universal UI conformance evidence
status: accepted
version: 1.0.0
created: 2026-10-18
updated: 2026-10-18
owners:
  - summa
related_documents:
  - docs/requirements/SUMMA-UI-CONFORMANCE.md
  - docs/measurement/ui-conformance/GH-90/evidence.json
  - tests/browser/ui-conformance.spec.js
tags: [evidence, ui-conformance, playwright, accessibility, responsive, print]
provenance:
  contributions:
    EXE-20261018T071903931Z-5035a47a:
      operations: [created]
      at: 2026-10-18T07:34:02.869Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record revision-pinned UI conformance test, environment, limitation, and print evidence for Summa issue #90"
derived_from: [SUMMA-UI]
---

# Summa universal UI conformance evidence

Work item: `GH-90`. Inspected implementation revision:
`20edb0a789d99d64656b03e24c57718ff089ee92`.

The machine-readable assessment is [`evidence.json`](./evidence.json). It uses
version 1.0.0 of the Universal UI Layout and Interaction evidence contract and
records five maintained product or print surfaces, their declared alignment
groups, twelve critical actions, viewport coverage, exceptions, and bounded
unknown scope. There are no maintained live-demo surfaces and no exceptions.

## Verification

| Gate | Result |
|---|---|
| `npm run test:ui-conformance` | 5 passed, 0 failed |
| Forma color validation and regression tests | 6 source files valid; 6 passed, 0 failed |
| Limen strict verification | 18 engine and 7 kernel source files passed |
| .NET suites | 667 passed, 0 failed, 0 skipped |
| Full Playwright suite | 68 passed, 0 failed |

The Playwright conformance gate recursively inventories source actions,
requires a real link, native form/dialog command, or Limen event binding, and
then separately proves observable outcomes for critical backlog and hub
actions. It measures declared peer edges and control heights after fonts and
two animation frames settle; exercises populated tables and dialogs at 320,
390, 768, and 1280 CSS pixels; checks a 1280-pixel/200%-zoom-equivalent layout;
and stresses long right-to-left labels. Existing end-to-end tests provide the
failure/recovery, complete accounting workflow, keyboard, print-media, stored
PDF, and multi-page generated-PDF evidence.

## Environment and limitations

- Playwright/Chromium contract: Playwright 1.63.0, pinned by `package-lock.json`.
- Local validation: macOS arm64, .NET SDK 10.0.100, Node 25.6.0, npm 11.8.0.
- Folio declares Node `>=24 <25`; the local executor therefore emitted its
  existing engine-range warning. Repository CI uses Node 24 and is the
  authoritative supported-engine result.
- The restricted executor sandbox stalled twice while NuGet restore attempted
  network access. Those processes were interrupted; the same focused gate and
  the complete suite passed when run with permitted network access.
- Firefox, Safari, real assistive-technology combinations, and physical
  printers remain outside the current CI matrix. Chromium print pagination is
  not represented as pixel-identical portable output.
- Token and cost telemetry are recorded by Praxis only where the runtime makes
  them available; no values are estimated.

No Summa-specific production layout or action defect reproduced at the pinned
Forma 0.9.0 and Folio 0.4.0 baselines. The production markup and CSS were not
changed to manufacture evidence.
