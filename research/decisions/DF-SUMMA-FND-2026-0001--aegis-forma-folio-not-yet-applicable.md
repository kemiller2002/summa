---
id: DF-SUMMA-FND-2026-0001
title: Aegis, Forma and Folio are declared not yet applicable until Summa's product foundation exists
status: superseded
version: 1.0.1
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes: []
superseded_by:
  - DF-SUMMA-FND-2026-0002
related_documents:
  - .echelon/foundations.json
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - limen.config.json
  - docs/web-interface.md
  - docs/project-administration-hub.md
  - .github/workflows/echelon-foundations.yml
tags: [governance, foundations, aegis, forma, folio, applicability]
---

# DF-SUMMA-FND-2026-0001 — Aegis, Forma and Folio are not yet applicable

- **Date:** 2026-10-05
- **Status:** superseded by [`DF-SUMMA-FND-2026-0002`](DF-SUMMA-FND-2026-0002--build-summa-on-the-full-echelon-foundation-stack.md) (2026-10-05). The owner chose the alternative below, for all three capabilities: "we want all apps built the same way." Kept for history; it no longer governs.
- **Decision type:** applicability declaration (temporary, with restoration triggers)
- **Work item:** `FOUNDATIONS-APPLICABILITY`

## Context

`.echelon/foundations.json` declared Aegis, Forma and Folio `required: true`.
The Echelon foundations gate (Praxis `foundations verify`, pinned at
`a95dbf238e561eaac4b38ca7011efc1a496cf1c6`) therefore failed on `main` and on
every branch with:

```
[FAIL] aegis    installed=False pinned=False used=False evidence=False expected=1.0.0
[FAIL] forma    installed=False pinned=False used=False evidence=False expected=0.2.0
[FAIL] folio    installed=False pinned=False used=False evidence=False expected=0.3.0
ECHELON-FND-AEGIS-001 aegis is required but is not installed/declared.
ECHELON-FND-FORMA-001 forma is required but is not installed/declared.
ECHELON-FND-FOLIO-001 folio is required but is not installed/declared.
```

What the verifier counts (Praxis `src/Praxis.Cli/Foundations.fs` at that
commit):

| Capability | Installed | Used | Evidence |
|---|---|---|---|
| Aegis | a `PackageReference` to `EchelonFoundry.Aegis.Core` in a `.fsproj`/`.csproj`/`.props`/`.targets` | source contains `open Aegis`, `Aegis.capture`/`guard`, `Bootstrap.validate` or `Sinks.Collector` | the boundary manifest (`aegis-boundaries.json`) exists |
| Forma | `@echelon-foundry/design-system` in `package.json` | source references the package, `design-system/all.css` or an `<ef-*>` control | same as used |
| Folio | `@echelon-foundry/print-components` in `package.json` | source references the package, `<ef-print-*>`, `print.css` or `register` | same as used |

## Evidence: Summa has no code that these capabilities govern

The portfolio has already recorded what this repository actually is
(`echelon-organization-administration`,
`application-governance/ENGINEERING-QUALITY-DECISION-INVENTORY.md`):

- **QDI-079** (and its 2026-10-05 amendment): Summa's actual role is a
  project-administration hub. Ledger, invoicing, receivables and payments
  exist only as requirement documents (`input-documents/summa-*.txt`). Summa is
  the *planned* owner of that ledger, on condition that it first replaces its
  copied Node-era tooling with a real foundation. "Nothing is implemented, so
  Summa does not provide these capabilities yet."
- **QDI-071**: `web/app.ts` and `web-hub/app.ts` are the old ROS
  project-administration UI copied from the starter profile. They are
  "upgrade/migration artifacts under QDI-035, not Summa domain architecture";
  the instruction is to remove or replace them through the supported migration
  path and "do not refactor this old scaffold into a better Summa UI".

The repository confirms it:

- **Aegis.** There is no `.fsproj`, `.csproj` or `.fs` file. The only server
  code is the copied Node ROS tooling in `tools/*.mjs`. Aegis is a .NET package
  (`EchelonFoundry.Aegis.Core`); `ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §3.1 scopes it to "every .NET/F# application or host tier that owns an
  operational boundary". No such tier exists, and none of the listed Summa
  boundaries (repository/storage, payment/accounting integrations,
  import/export, document rendering) is implemented.
- **Forma.** The only browser pages are `web/` and `web-hub/`, the copied ROS
  backlog and hub UIs ("it adds no new capability over the CLI",
  `docs/web-interface.md`). They are not a Summa product surface, and
  restyling them onto Forma is exactly the refactoring QDI-071 rules out. The
  surface Forma governs is whatever replaces them.
- **Folio.** Folio's Summa scope (§5) is invoices, statements, receipts, aging
  reports, account summaries and other financial document outputs. None
  exists; the ledger that would produce them is not built (QDI-079).

Installing the packages now would mean an unused dependency and an empty
project, or a restyle of the scaffold QDI-071 says not to grow. The verifier's
`used` check exists to reject the first ("merely adding a package does not
satisfy the contract", Praxis `docs/application-foundations.md`), and the
repository's rules forbid fabricated evidence.

## Policy basis

- **Repository requirement.** `docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §1: the requirements are inherited "when the capability is applicable … An
  implementation MAY mark a capability not applicable only when it is
  genuinely outside that feature's boundary. The reason MUST be explicit and
  reviewable." This record is that reason.
- **Verifier contract.** Praxis `docs/application-foundations.md`:
  applications own applicability; a capability declared `required: false` is
  reported as `N/A`, not PASS. The foundations schema
  (`echelon-foundations-v1.schema.json`) has no reason field
  (`additionalProperties: false`), so the reason lives here.
- **Portfolio inventory.** `QUALITY-REMEDIATION-INVENTORY.md` SUM-F4
  (remediation "same as CHR-F2": "Mark capabilities not-applicable with
  reasons until a slice needs them, or install them") and XC-16 ("foundations
  must be justified by artifacts present").
- **QDI-035** asks that Summa "install/declare the required shared foundations"
  and end with "all declared foundation checks green". This decision makes the
  declaration truthful and the gate green now; the installation itself belongs
  to the real foundation QDI-035/QDI-071 call for, and the triggers below bind
  it there.

## Decision

In `.echelon/foundations.json`, set `aegis`, `forma` and `folio` to
`required: false`. Keep their `version`, `sourceCommit` and `boundaryManifest`
values unchanged so the baseline to restore is not lost.

This does **not** relax any requirement in
`ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`. Those apply in full to the first
code that owns the boundary in question.

## Restoration triggers (each flips the capability back to `required: true` in the same change)

1. **Aegis:** the first .NET/F# project or host is added (for example the
   foundation that replaces the Node-era tooling, or the ledger). That change
   must reference `EchelonFoundry.Aegis.Core` 1.0.0 (or the then-current
   baseline), use it at the boundary, and add `aegis-boundaries.json`.
2. **Forma:** the first interactive browser surface that is not the copied
   QDI-071 scaffold is added, including any replacement of `web/` or
   `web-hub/`. It must consume the pinned `@echelon-foundry/design-system`.
3. **Folio:** the first financial or other document output (invoice,
   statement, receipt, aging report, account summary or printable export) is
   added. It must consume the pinned `@echelon-foundry/print-components`.

Each trigger is recorded as a backlog obligation: WI-0004 (Aegis), WI-0005
(Forma) and WI-0006 (Folio). Reviewers of a
change that crosses a trigger should reject it if `foundations.json` still
says `required: false` for that capability.

## Alternatives considered

- **Install Forma into the copied `web/` and `web-hub/` pages now.** Limen
  was applied to these pages on `chore/limen-0.7.0` at the owner's choice
  (LIMEN-0-7-0-APPLY). Forma differs: Limen's change separated state from
  effects without changing what the pages are, whereas moving them onto Forma
  is a UI rework of the scaffold, which QDI-071 explicitly rules out. If the
  owner prefers otherwise, this record should be superseded and Forma
  installed there with real `<ef-*>`/`all.css` usage.
- **Leave `required: true`.** Keeps a permanently red gate that no change can
  turn green without fabricating usage (XC-16: "gates ignored").

## Consequences

- `foundations verify` reports Aegis, Forma and Folio as `N/A`; the gate
  fails again only if Limen, Ordo or Praxis drift.
- The gate cannot itself detect that a trigger was crossed. That gap is
  portfolio-wide (XC-16) and is mitigated here by the backlog obligations and
  the review rule above.

## Revisit trigger

Any restoration trigger above; a Praxis foundations schema change that adds an
explicit deferred/not-applicable state with reason (XC-16); a QDI-079 review
trigger (charter update, re-profiling, Node-era tooling replaced, ledger work
started); or the owner superseding the Forma alternative above.
