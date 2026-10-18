---
id: EV-SUMMA-VIS-2026-001
title: Summa semantic color engineering review
status: observed
version: 1.0.0
owners:
  - summa
created: 2026-10-17
updated: 2026-10-17
related_documents:
  - docs/requirements/SUMMA-VISUAL-COLOR.md
  - app/summa-app.css
tags: [evidence, forma, color, accessibility, visual-engineering]
provenance:
  contributions:
    EXE-20261017T163958200Z-b0a63140:
      operations: [created]
      at: 2026-10-18T00:59:55.736Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Capture commit-specific semantic color, responsive, forced-color, grayscale, and CVD engineering evidence without claiming product approval"
---

# Summa semantic color engineering review

Work item: `GH-110`. Product implementation inspected at
`debd3dfd0af633d8ca10d04e9204d9ec579e225e`; the following checkpoint commit
changed only Praxis state. The screenshots contain deterministic local-demo
data, not customer data.

## Automated evidence

- Forma 0.9.0's published validator accepted all six first-party CSS files.
- Six negative contract tests refused all five former unsupported names and a
  dark-theme value frozen as a light-theme fallback.
- Computed-style Playwright checks passed in explicit light and dark themes.
  Measured contrast ratios were: inverse pair 13.68/15.26, link 5.04/4.85,
  danger 5.79/8.07, success 5.60/8.78, warning 5.45/8.88, and focus ring
  15.26/15.26 (light/dark respectively).
- Selected navigation retained `aria-current`, weight, and underline; links
  retained underlines; status notices retained explicit text and a border.
- Keyboard focus used the two-part `focus-gap` outline plus `focus-ring` halo.
- Chromium reported zero horizontal overflow at 1280, 640, and 375 CSS
  pixels. The 640 CSS-pixel capture is the reflow equivalent of a 1280-pixel
  viewport at 200% zoom.
- Forced-colors focus remained programmatically focused with a non-`none`
  outline. Grayscale, protan, deutan, and tritan screens retained the text,
  structure, underline, weight, and border cues required to identify the
  captured state without hue.

The full gate passed 667 F# tests, 6 color-contract tests, and 63 Chromium
tests. Visual Engineering context 1.0.1 at source commit
`b648cdef291d39c443ccb10215529ad69afe8b32` verified before review.

## Captures

| Condition | File | SHA-256 |
|---|---|---|
| Light desktop | [light-desktop.png](light-desktop.png) | `aa0c8a87fe6a001112d1c4563682c695e570682654b99b520075ab819358a125` |
| Dark desktop with keyboard focus | [dark-desktop-focus.png](dark-desktop-focus.png) | `a6f615e18f01d3d1c31d65854fb6923f0bc2613fb5cc6c1dca43c9b8399e14a7` |
| Light mobile, 375 CSS pixels | [light-mobile-375.png](light-mobile-375.png) | `37510e6a135430371633f46c96d702e04118ab4fe53d1d2993672061f815b158` |
| Dark 200% reflow proxy | [dark-zoom-200-proxy.png](dark-zoom-200-proxy.png) | `dc5f183c4c88d14f6460b0169a1e638f55e73039055b94aaee9975e661bb1828` |
| Forced colors with keyboard focus | [forced-colors.png](forced-colors.png) | `1ace8d097a44803d5d79f499312e43230a1dbf9d1acdb10d998d7486e8d7784e` |
| Grayscale screen | [grayscale-screen.png](grayscale-screen.png) | `fb36176d197e3c6d5f1bfcad08eb9400cb6480badc51543b2745277f8b3a68b9` |
| Protan screen | [protan-screen.png](protan-screen.png) | `133637b1114417bc1b2a0f63ca695b12b7da444ef32b1b373b894fbd982b1d21` |
| Deutan screen | [deutan-screen.png](deutan-screen.png) | `245bbaa108ea471219d470145ad4c8a67b92bf09d513ddba42504a122910e1b7` |
| Tritan screen | [tritan-screen.png](tritan-screen.png) | `89338ec21840e26cfd47b5ae75d085615ae795a893418fe39f62c090b52b7378` |

## Boundary and incomplete evidence

This is an engineering screen, not product approval or affected-user
validation. It establishes contract conformance, computed pairings, reflow,
focus visibility, and semantic-channel survival for the captured dashboard.
The product reviewer must still approve the palette and accounting hierarchy
across the broader workflows. No chart palette was reviewed because Summa has
no charts. Motion was not applicable to this color-only change.
