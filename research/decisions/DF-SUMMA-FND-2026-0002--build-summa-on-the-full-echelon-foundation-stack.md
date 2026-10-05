---
id: DF-SUMMA-FND-2026-0002
title: Summa is built the same way as the other Echelon applications, on Limen, Forma, Aegis and Folio
status: accepted
version: 1.0.0
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes:
  - DF-SUMMA-FND-2026-0001
superseded_by: []
related_documents:
  - .echelon/foundations.json
  - aegis-boundaries.json
  - limen.config.json
  - package.json
  - Directory.Packages.props
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - docs/web-interface.md
  - docs/project-administration-hub.md
  - .github/workflows/build.yml
  - .github/workflows/echelon-foundations.yml
  - research/decisions/DF-SUMMA-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md
tags: [governance, foundations, aegis, forma, folio, limen, architecture]
---

# DF-SUMMA-FND-2026-0002: Summa is built the same way as the other Echelon applications

- **Date:** 2026-10-05
- **Status:** accepted
- **Decision type:** architecture and applicability (supersedes an applicability declaration)
- **Work items:** `WI-0004` (Aegis), `WI-0005` (Forma), `WI-0006` (Folio)
- **Authority:** repository owner instruction, 2026-10-05: "Apply to summa, we want all
  apps built the same way."

## Context

[`DF-SUMMA-FND-2026-0001`](DF-SUMMA-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md)
declared Aegis, Forma and Folio `required: false` until Summa had a product
foundation of its own. It listed, as its first alternative, installing Forma
into the copied `web/` and `web-hub/` pages, and said that if the owner
preferred that, the record should be superseded. The owner has now chosen the
alternative, for all three capabilities and for the reason that every Echelon
application is to be built the same way. Under the repository's authority order
(`AGENTS.md`: explicit user instruction first) that instruction also settles
the QDI-071 concern the earlier record relied on: the pages are rebuilt on the
shared foundation rather than kept as a scaffold.

## What "built the same way" means: the reference pattern

The pattern was taken from the Echelon applications that are fully on the
foundation stack, as of their `origin/main` on 2026-10-05, and from the Praxis
foundations verifier (`src/Praxis.Cli/Foundations.fs`, unchanged between the
pinned `a95dbf238e561eaac4b38ca7011efc1a496cf1c6` and current `main`).

| Evidence | What it shows |
|---|---|
| `vigila` `.echelon/foundations.json` | The only reference application declaring foundations: Aegis 1.0.0, Forma, Folio, Limen 0.7.0, Ordo and Praxis all `required: true`. (Forma Studio, Tekmerion, Visual Engineering and Folio carry no foundations declaration.) |
| `vigila` `src/`, `web/`, `limen.config.json` | Four tiers: F# semantic/transition/application projects (the Limen *engine*), a C# `Microsoft.NET.Sdk.WebAssembly` shim with one `[JSExport]` per entry point, and a `web/` kernel that starts Limen's `BrowserKernel` over a WASM transport. Engine and kernel paths are named in `limen.config.json`. |
| `vigila` `Directory.Packages.props`, `Dispatch.fs` | `EchelonFoundry.Aegis.Core` 1.0.0 centrally pinned (with FSharp.Core 10.1.400); Aegis configured once and validated (`Bootstrap.validate`); every kernel message dispatched inside `Aegis.capture` with one classifier; faults presented through `Presentation.present` into Forma's `ef-fault-inline`; expected refusals stay typed; `aegis-boundaries.json` declares each boundary. |
| `vigila` `web/index.html`, `web/styles.css`, `web/main.js` | Forma consumed from the installed `@echelon-foundry/design-system` (`@import ".../dist/all.css"`), markup composed from Forma patterns inside inert `<ef-*>` wrappers (`ef-alert`, `ef-fault-inline`, `ef-work-queue`, `ef-status-lozenge` with `data-state`); Folio's `print.css` imported for print media and `register.js` imported by the kernel; an `<ef-print-document>` projecting the same engine state. Bindings are Limen `data-*` attributes; state cues are `data-*` attributes plus CSS. |
| `forma-studio` `src/engine`, `src/kernel`, `src/wasm` | The same layout: F# engine published to .NET WebAssembly, Limen 0.7.0 kernel page with `data-event`/`data-text`/`data-bind-*` bindings, Forma tokens/foundations/components CSS, no `data-bind-style`. |
| `tekmerion` `src/Tekmerion.Cli` | Aegis 1.0.0 referenced and configured at an F# host's operational boundaries; Forma consumed from the installed package. |
| Praxis `Foundations.fs` | What the gate checks per capability: Aegis = a `PackageReference` to `EchelonFoundry.Aegis.Core` in a .NET project, source using `open Aegis`/`Aegis.capture`/`Bootstrap.validate`, and the boundary manifest; Forma = `@echelon-foundry/design-system` in `package.json`, pinned (version, `/v<version>/` or commit) and referenced from source; Folio = `@echelon-foundry/print-components`, pinned and referenced (`print.css`, `register`, `<ef-print-*>`). |
| `echelon-registry` `channels/echelon-current/linux-x64.json` | The versions the current channel selects: Aegis 1.0.0 (NuGet), Forma 0.3.0 (`kemiller2002/forma` release `v0.3.0`), Folio 0.3.0 (`kemiller2002/folio` release `v0.3.0`, commit `330fc480b8f5a977801bc82bdf65ee736e526681`). |

So "the same way" is: every foundation `required: true`; an F# engine compiled
to .NET WebAssembly behind Limen's browser kernel; Aegis at the engine's
operational boundary; Forma for all interactive presentation, consumed from the
pinned package; Folio for the printable projection, consumed from the pinned
package; pinned releases from the `echelon-current` channel.

## Decision

1. **All foundations required.** `.echelon/foundations.json` sets Aegis 1.0.0,
   Forma 0.3.0 and Folio 0.3.0 to `required: true` (Limen 0.7.0, Ordo and Praxis
   unchanged). Folio's earlier `sourceCommit` pin is dropped because the
   dependency is now the immutable `v0.3.0` release artifact.
2. **Same layout as the reference applications.**
   - `src/Summa.Web.Engine` (F#, pure): both pages' state, transitions, request
     descriptions and view projections, carrying over every decision of the
     TypeScript engines it replaces (`web/engine/backlog.ts`,
     `web-hub/engine/hub.ts`).
   - `src/Summa.Web.Application` (F#): Limen protocol codec, handshake (Core
     plus the `limen.files` and `limen.transfer` packs for uploads), response
     decoding, and the Aegis boundary.
   - `src/Summa.Wasm` (C#, WebAssembly SDK): a two-method `[JSExport]` shim.
   - `web-kernel/limen-wasm.js`: the shared Limen `BrowserKernel` start-up;
     `web/` and `web-hub/` hold markup and page CSS only.
   - `limen.config.json` names the two F# projects as engine and the shim and
     the three browser directories as kernel. `verify --strict` passes.
3. **Aegis** (`EchelonFoundry.Aegis.Core` 1.0.0, central package pin) is
   configured once and validated, and every kernel message runs inside
   `Aegis.capture` with one classifier: `SUMMA.BOUNDARY.MESSAGE_INVALID`,
   `RESPONSE_INVALID`, `CAPABILITY_UNAVAILABLE`, `UNEXPECTED`. Server refusals,
   unanswered requests and unknown outcomes stay typed replies shown as
   ordinary errors. Faults reach the page only as safe presentation, through
   Forma's fault component. `aegis-boundaries.json` declares the boundaries,
   including the unguarded Node servers.
4. **Forma** (`@echelon-foundry/design-system` from the `v0.3.0` release
   tarball, locked with its integrity hash) is imported by both pages from the
   installed package (`dist/all.css`). Markup uses Forma patterns (field, alert,
   fault-inline, data-grid, status-lozenge, file-upload, dialog, empty-state,
   record-header) inside inert `<ef-*>` wrappers. State cues are `data-*`
   attributes set by the engine and matched by CSS; there are no inline styles
   and no `data-bind-style`, which Limen 0.7.0 refuses.
5. **Folio** (`@echelon-foundry/print-components` from the `v0.3.0` release
   tarball) supplies `print.css` (print media) and `register.js` (imported by
   the kernel). Each page carries an `<ef-print-document>` with an
   `<ef-print-table>` projecting the same rows as the screen; nothing is
   recomputed for print.
6. **Verification** is part of the build (`.github/workflows/build.yml`): .NET
   tests (engine decisions carried over from the TypeScript tests, page state
   machines, the Limen protocol and Aegis boundary with a collector sink,
   foundation conformance, and an HTML/engine binding agreement), Limen
   `verify --strict`, a WebAssembly publish, and a Playwright suite driving both
   pages against the real Node servers.

## Behaviour changes, deliberately

The pages keep their features and their server contract. Where the move to
Limen's model changed something visible, it is listed here:

- Dialogs open through the native `command`/`commandfor` invoker and are no
  longer awaited by script; the hub's unregister confirmation is a native
  dialog instead of `window.confirm`.
- Only the newest list request may replace the rows, so a slow reply to an
  earlier filter can no longer overwrite a later one.
- Tags render as one comma-separated cell (Limen view items are flat).
- The hub keeps the chosen repository after creating an item, and drops a
  repository filter whose repository was unregistered.
- An empty queue shows Forma's empty state; the open row is marked
  (`data-selected`, `aria-pressed`).
- An error row in the hub spans the five work columns (it spanned four of five).

## Consequences

- `foundations verify` checks Aegis, Forma and Folio for real; a regression in
  any of them fails the gate and the conformance tests.
- Building Summa now needs the .NET 10 SDK and Node 24 (Folio declares
  `>=24 <25`), as the other applications do.
- The Node work-protocol servers (`tools/ros_server.mjs`,
  `tools/ros_hub_server.mjs`) remain the Node-era tooling QDI-071 and QDI-005
  describe; this decision changes what they serve (`tools/web_static.mjs`), not
  what they are. Replacing them is still the precondition QDI-079 sets for the
  ledger.

## Alternatives considered

- **Keep the TypeScript engines and add Forma and Folio only.** Rejected: Aegis
  is a .NET package, so an application with no .NET tier cannot consume it, and
  the reference applications' engines are F# behind a WebAssembly shim. A
  separate .NET project existing only to reference Aegis would not be "the same
  way" and would fail the verifier's intent ("merely adding a package does not
  satisfy the contract").
- **Pin Forma 0.2.0 and Folio commit `273b18f` as Vigila does.** Rejected: the
  instruction was to use what the registry's `echelon-current` channel selects,
  which is Forma 0.3.0 and Folio 0.3.0.

## Revisit trigger

A new `echelon-current` channel selection for Aegis, Forma, Folio or Limen; a
Praxis foundations-verifier change; or the replacement of the Node-era servers.
