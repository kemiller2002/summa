# Echelon Shared Application Foundation Requirements

Status: **Required**
Application: **Summa**
Scope: **All current and future product requirements in this repository**
Date: 2026-09-23

## 1. Normative scope

These requirements are cross-cutting. Every existing and future requirement in
this repository inherits them when the capability is applicable. A feature
requirement does not need to repeat them.

Aegis, Forma, and Folio are shared Echelon application infrastructure. Their
existing capabilities MUST be consumed rather than independently recreated in
Summa. A missing shared capability MUST be recorded as a gap in the owning
shared repository instead of being silently forked locally.

An implementation MAY mark a capability not applicable only when it is genuinely
outside that feature's boundary. The reason MUST be explicit and reviewable.
Silence is not an exception.

## 2. Required dependency baselines

Application dependencies MUST be reproducible and pinned. Floating versions and
tracking a moving repository branch are not valid application baselines.

- **Aegis:** `EchelonFoundry.Aegis.Core` **1.0.0** is the current .NET
  application baseline. Integration-specific Aegis packages MUST use a
  compatible pinned version when the matching integration exists.
- **Forma:** `@echelon-foundry/design-system` **0.2.0** is the current
  application baseline. Until npm is the selected canonical source, consume the
  immutable v0.2.0 release artifact rather than copying CSS or tracking `main`.
- **Folio:** `@echelon-foundry/print-components` **0.3.0** is the current
  source baseline. Until a canonical v0.3.0 package/release artifact exists,
  pin the immutable Folio commit `2b101b6d840a670abb959148fff8e1477c059eda` rather than tracking
  `main`. Once published, pin the exact canonical package version.

A dependency upgrade is an explicit application change and MUST include
verification evidence.

## 3. Aegis

**Applicability for Summa: required at repository/storage, payment/accounting integrations, import/export, browser/WASM, document rendering, and other external boundaries.**

Aegis is the required mechanism for **unexpected operational failure at
architectural boundaries**.

### Requirements

1. Every .NET/F# application or host tier that owns an operational boundary
   MUST reference and use `EchelonFoundry.Aegis.Core`.
2. Aegis MUST be configured once at application composition/startup with stable
   application identity, application version when available, explicit sinks,
   redaction rules, and persistence posture. Bootstrap configuration MUST be
   validated before it is trusted.
3. Network, repository/GitHub, storage, filesystem, parsing/deserialization of
   externally sourced data, browser/WASM interop, renderer/export, database,
   process, and other external operational boundaries MUST route unexpected
   failures through the standard Aegis capture/guard surface as appropriate.
4. New generic `try/with`, catch-all exception swallowing, or equivalent
   boundary handling MUST NOT replace Aegis at a declared operational boundary.
5. Expected domain outcomes MUST remain typed application/Ordo outcomes.
   Validation refusal, illegal transition, authorization refusal, ambiguity,
   conflict, insufficient evidence, and other modeled outcomes MUST NOT be
   converted into Aegis faults.
6. Programming defects MUST retain Aegis fail-loud behavior and MUST NOT be
   disguised as ordinary recoverable operational failures.
7. Cancellation MUST follow Aegis cancellation semantics and MUST NOT
   automatically be recorded as a fault.
8. Every captured fault MUST have a stable machine-readable code and explicit
   category, severity, availability impact, recovery posture, and safe
   user-facing message.
9. Integration-specific failure types SHOULD use one central Aegis translation
   mapping rather than ad hoc classification at every call site.
10. Secrets, credentials, tokens, sensitive user/customer data, and unsafe raw
    exception detail MUST be privacy-classified/redacted before reaching Aegis
    context, sinks, exports, or user presentation.
11. Repositories with multiple operational boundaries MUST maintain a
    machine-readable declaration of boundary name, kind, owner, expected
    fault-code family, and guarded status.
12. Fault tests MUST use deterministic replaceable sinks such as the Aegis
    collector. Tests MUST prove both that operational failures are captured and
    that expected domain refusals are not misclassified as Aegis faults.

## 4. Forma

**Applicability for Summa: required for every interactive browser application surface.**

Forma is the required Echelon presentation system for interactive UI.

### Requirements

1. Interactive browser UI MUST consume the pinned Forma dependency.
2. Application code MUST search and use existing Forma patterns, components,
   tokens, and accessibility contracts before creating application-local
   equivalents.
3. Forma CSS, component markup, or patterns MUST NOT be copied or forked into
   the application merely for convenience, branding, or small spacing changes.
4. Consumer markup SHOULD use Forma's documented inert `<ef-*>` authoring
   wrappers around the canonical native HTML pattern.
5. Forma wrappers MUST NOT be registered with `customElements.define()`.
   Native HTML remains the semantic and accessibility authority.
6. Forma owns presentation, tokens, responsive recomposition, visual state cues,
   and shared accessibility presentation. Limen/application code owns runtime
   behavior beyond native HTML. Summa/Ordo state owns legal transitions,
   capabilities, obligations, permissions, validation, scoring/interpretation,
   and domain invariants.
7. Business meaning MUST NOT be inferred from color, position, animation, or
   component-local visual state.
8. Every applicable screen MUST preserve Forma's mobile contract, including the
   320 CSS px baseline, keyboard/non-pointer operation, visible focus,
   reduced-motion behavior, forced-colors behavior where applicable, and
   non-color state cues.
9. A new reusable UI need that Forma does not provide MUST be recorded against
   Forma and implemented there when it is cross-application. The app MAY use a
   bounded temporary composition, but MUST NOT silently grow a competing design
   system.
10. Aegis fault presentation intended for an interactive user MUST flow through
    safe presentation intent into the applicable Forma fault/error components
    rather than exposing raw exceptions or maintaining a parallel error UI
    system.
11. Forma upgrades MUST run the application's build, browser, mobile,
    accessibility, and affected-pattern verification.

## 5. Folio

**Applicability for Summa: required for invoices, statements, receipts, aging reports, account summaries, printable exports, and other financial document outputs.**

Folio is the required Echelon document-intent and print component system for
printable, PDF, paginated, print-preview, and document-style output.

### Requirements

1. Applicable document surfaces MUST consume the pinned Folio dependency,
   including its registration module and `print.css`.
2. Before implementing print layout locally, the application MUST use an
   existing Folio primitive when one expresses the required document intent.
3. The application MUST NOT create competing local implementations of
   Folio-provided document, header, footer, page-number, title/back-page,
   artwork/layer, column, sidebar, break/keep, metric, finding, callout, figure,
   table, code, TOC, note, or equivalent shipped primitives.
4. Semantic HTML and logical reading order MUST come first. Meaningful content
   MUST remain understandable if custom elements have not upgraded.
5. Summa owns document data, privacy, authorization, labels, interpretation,
   and domain meaning. Folio owns reusable print/layout intent. The selected
   renderer owns physical pagination and fragmentation.
6. Renderer capability MUST be explicit. Portable browser behavior MUST NOT be
   represented as pixel-identical output, and Chromium-specific page-margin or
   page-counter behavior MUST NOT be described as portable.
7. Official deterministic PDF output MUST use a controlled renderer contract
   and preserve renderer/version metadata sufficient to reproduce the output.
8. Limen/Ordo MAY own meaningful preview/configuration state, but the
   application MUST NOT implement a JavaScript/WASM DOM-measure-and-repage
   pagination engine.
9. Interactive controls surrounding a document use Forma. Printable document
   composition uses Folio. Neither substitutes for the other.
10. Substantial printable outputs MUST have structural print tests and
    renderer-backed regression evidence proportional to their risk, including
    long/fragmented content where relevant.
11. Printable output MUST be tested to ensure sensitive application/Aegis data
    is not leaked.
12. A reusable print need missing from Folio MUST be recorded against Folio and
    implemented there when cross-application, rather than silently growing a
    parallel print library.

## 6. Cross-capability composition

The ownership model is:

- **Summa/Ordo domain state:** authoritative meaning, legal states,
  transitions, capabilities, obligations, permissions, validation, and modeled
  outcomes.
- **Aegis:** unexpected operational failures at external/architectural
  boundaries.
- **Limen/application boundary:** browser interaction and effects.
- **Forma:** interactive application presentation.
- **Folio:** printable/document presentation and reusable layout intent.

Interactive operational failure flow:

`external boundary -> Aegis fault -> safe presentation intent -> application/Limen state -> Forma UI`

Printable artifact flow:

`application-owned semantic data -> semantic HTML -> Folio primitives -> declared renderer capability -> paper/PDF`

Screen and print views MUST project the same authoritative application/domain
state. Print code MUST NOT independently recompute legality, status,
obligations, scoring, or other domain meaning.

## 7. Verification and Definition of Done

A requirement using an applicable shared capability is not complete until
evidence proves:

- the dependency is present, pinned, and reproducibly restorable;
- the shared capability is actually used, not merely listed;
- no competing local implementation bypasses an existing shared primitive
  without an approved decision;
- Aegis boundary classification, redaction, recovery, and domain/fault
  separation are tested where relevant;
- Forma browser, mobile, keyboard, reduced-motion, and accessibility behavior
  are tested where relevant;
- Folio print/PDF output and renderer capability assumptions are tested where
  relevant;
- any exception is recorded in an architecture/decision record with rationale
  and follow-up if the shared capability has a gap.

Absence, incompatibility, or bypass of a required applicable shared dependency
MUST block completion of the affected work.

This document is normative for the repository and MUST be considered during
requirement refinement, implementation planning, review, and acceptance.
