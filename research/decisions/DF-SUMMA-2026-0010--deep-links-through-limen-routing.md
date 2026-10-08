---
id: DF-SUMMA-2026-0010
title: Deep links through Limen.Routing
status: accepted
version: 1.2.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/SUMMA-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
  - research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md
  - .echelon/routes.json
tags: [web, limen, routing, deep-links]
provenance:
  contributions:
    EXE-20261008T194235483Z-c793abf6:
      operations: [created, modified]
      at: 2026-10-08T19:49:22.488Z
      last: 2026-10-08T20:28:13.593Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide how Summa meets the deep-linking requirement before Limen 0.9.0 ships"
    EXE-20261008T214640906Z-b2269183:
      operations: [modified]
      at: 2026-10-08T21:46:41.662Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the switch from the vendored router to the Limen 0.9.0 package (WI-0042)"
---

# DF-SUMMA-2026-0010 — Deep links through Limen.Routing

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0041 (screens in WI-0030)

## Context

The user made deep linking a portfolio requirement (SUM-LINK-001..012): all
navigable state goes in the URL, so a copied link opens the same view. Limen
0.9.0 will ship a shared router for this (`Limen.Routing`, LCP-088..112,
DF-LIMEN-2026-0006). Its F# library is written and under review in
kemiller2002/limen#101 but not yet released. Summa must match its API now, so
moving to the package later is mechanical.

## Decisions

1. **Vendor the library unchanged rather than re-implement it.** *Amended
   in 1.2.0:* Limen 0.9.0 shipped, and Summa now references the
   `EchelonFoundry.Limen.Routing` 0.9.0 package. Conditor installs it from the
   release assets of `limen-fsharp` 0.9.0, proven against the Registry digest
   (echelon-current 1.11.0). The vendored copy and `UPSTREAM.json` are gone
   (WI-0042). The route tests passed unchanged against the package, and the
   inventory is byte-identical. Its history:
   - `vendor/Limen.Routing/Routing.fs` is the upstream file at limen#101's
     head (`e935da7`), byte for byte. `UPSTREAM.json` pins its git blob and
     SHA-256, and a test fails if the file changes.
   - The upstream conformance runner proves the file against 104 URL-state
     vectors and 9,000 generated cases; a second implementation in Summa would
     have to be proven again and would drift.
   - Its project sets only what the upstream file was written against:
     nullable checking and XML documentation are off for it alone.
   - **Switching** when 0.9.0 ships: delete `vendor/Limen.Routing`, reference
     the package from `Summa.Web.Engine`. `namespace Limen.Routing` and every
     function Summa calls are the package's own.
   - If #101 changes before release, the switch is where Summa takes the
     change, with the route tests as the check.
2. **Hash routes.** GitHub Pages serves one static page, so the place lives in
   the fragment (`#/invoices/INV-0001?tab=history`). Links are relative
   (`#/…`), there is no `<base href>`, and a shared link keeps the page's own
   path, so it works at any base path.
3. **The route model is Summa's `Routes.Place` union.** Each case maps to a
   declared route and back through `RouteCodec`. List filters, sort, search,
   tabs, report dates and basis are typed parameters with declared defaults.
   Free text appears only as a search (`q`) or an opaque record id.
4. **Opaque ids only.** Records are named by their own ids (`INV-0001`,
   `CUST-0001`), never by amounts, balances or account numbers. Invoices are
   addressed by id, not number. A test fails if any parameter could carry an
   amount, a rate, a credential or an account number. The table refuses
   credential names when it is defined.
5. **One guard, `signed-in`, plus each route's `requires`.** These are
   capability names from `Summa.Access`. An anonymous viewer is redirected to
   `/sign-in?returnTo=<canonical>`. A member without a route's capabilities
   gets NotPermitted. Local books are one person's own: everything is
   permitted. This is interface policy only: every command is authorized again
   by `Summa.Access`.
6. **A record id the books do not hold** is the screen's own not-found state,
   decided by the engine after the route parses. The route table cannot know
   which ids exist.
7. **The inventory** `.echelon/routes.json` is `Inventory.render` of the table,
   committed and held to it by a test (regenerate with `SUMMA_WRITE_ROUTES=1`).
8. **Sign-in return is modelled now and wired with sign-in.** The table
   declares `sign-in` with `returnTo`. `Routes.parse` and `Routes.resume` are
   tested for an anonymous viewer, but the application has no sign-in until
   WI-0035.

9. **In-page anchors are not places.** A fragment that does not start with
   `#/` (the skip link's `#main`) keeps the current place, and the engine
   replaces the address back to its canonical location. The browser still
   moves focus to the anchor.
10. **Receivables as of a date are a domain view.** `Payments.asOf` keeps only
    what was issued, received, applied, refunded, reversed, written off or
    voided by the date. Aging that view is the receivables as they stood. The
    date is in the link (`asOf`).
11. **Search terms that could be amounts stay out of the URL.** A list's `q`
    matches names, numbers and references, never amounts. v0.4 §18 asks
    global search to find amounts. That search is a transient overlay, not a
    place: the record it opens is a link, and its terms are not.
12. **Engine-chosen moves.** Issuing an invoice pushes to it. Saving a new
    invoice replaces `/invoices/new` with `/drafts/<id>`, so Back never offers
    an empty form for a draft that exists.

## Consequences

- WI-0030 builds each screen against its place: links are `href`s from
  `Routes.href`, the engine adopts `Initialize` and `LocationChanged`
  locations, and asks for Navigation (push or replace) and Clipboard effects.
- WI-0042 replaced the vendored copy with the package. `.echelon/routes.json`
  is also validated against Limen's `contract/routes.schema.json`.
