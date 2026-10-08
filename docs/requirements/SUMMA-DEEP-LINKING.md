---
id: SUM-LINK
title: Summa deep links - every navigable view in the URL
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
related_documents:
  - docs/requirements/implementation-gap-analysis.md
  - .echelon/routes.json
tags: [requirements, ui, routing, deep-links, limen]
provenance:
  contributions:
    EXE-20261008T194235483Z-c793abf6:
      operations: [created]
      at: 2026-10-08T19:48:35.673Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the user's portfolio deep-linking requirement as SUM-LINK-001..012 (WI-0041)"
    EXE-20261008T214640906Z-b2269183:
      operations: [modified]
      at: 2026-10-08T21:46:42.066Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Point SUM-LINK at the Limen 0.9.0 package (WI-0042)"
---

# SUM-LINK — deep links

A portfolio requirement from the user (2026-10-08): every Echelon
application puts all navigable state in the URL, so that a copied link opens
the same view. Summa meets it through Limen's shared router (Limen 0.9.0,
`Limen.Routing`; requirements `LCP-088..112`, decision `DF-LIMEN-2026-0006`).
Summa uses the `EchelonFoundry.Limen.Routing` 0.9.0 package (`DF-SUMMA-2026-0010`).

The work item is **WI-0041**; the screens are built in **WI-0030**.

**SUM-LINK-001 Every view has a location.** Every place a person can
navigate to MUST have its own location: home, customers and a customer,
engagements and an engagement, invoices, a draft, an invoice and its tab,
payments and a payment, credit memos and a credit memo, receivables,
accounting periods and a period, the general ledger and a journal entry, the
reports, settings, sign-in and not found.

**SUM-LINK-002 A copied link opens the same view.** Opening a location MUST
show the same view, with the same list filters, sort order, search and date
ranges, that produced it. Every list's filters, sort and search and every
report's dates and basis MUST be in the location.

**SUM-LINK-003 One canonical location per view.** Locations MUST be built
in canonical form: declared parameters only, in declaration order, defaults
omitted, set members sorted and de-duplicated, and percent-encoding with `%20`
and uppercase hexadecimal. A location that is not canonical (extra, reordered
or default parameters) MUST be replaced by its canonical form, without
adding a history entry.

**SUM-LINK-004 Nothing sensitive in a location.** A location MUST NOT carry
an amount, balance, rate, token, secret, credential, account number or other
sensitive financial data. Records are named by their opaque ids. Parameter
names reserved for credentials (`token`, `secret`, `key`, `session`, `auth`,
`code` and the like) MUST be refused when the route table is defined.

**SUM-LINK-005 Static hosting.** Deep links MUST work on a static host
(GitHub Pages) at any base path: hash routes (`#/invoices/INV-0001?tab=history`),
relative links, and no `<base href>`.

**SUM-LINK-006 History follows places.** Going to another place MUST add a
history entry. Refining the current view (a filter, sort, search, tab or
date) MUST replace the current entry, so Back returns to the previous place.
Back and Forward MUST show the view of the location they reach.

**SUM-LINK-007 Bad locations say so.** A location naming no place, a record
that does not exist, a parameter of the wrong type, a malformed or oversized
location, or a place the person may not open MUST show its own clear page
(not found, not permitted, or invalid with the parameter named). It MUST NOT
show a blank page or another view, and MUST NOT reveal whether a record the
person may not see exists.

**SUM-LINK-008 Deep links survive sign-in.** A deep link opened by someone
who is not signed in MUST go to `#/sign-in?returnTo=<canonical location>` and,
after sign-in, to that location (replacing sign-in in history), once its
permissions are checked again. A return target MUST be one of Summa's own
relative locations; anything else (another origin, `//host`, a backslash, a
script URL, sign-in itself) MUST go home.

**SUM-LINK-009 Copy link.** Every view that can be opened from a link MUST
offer a "Copy link" control that copies the absolute URL of its canonical
location and says whether the copy succeeded.

**SUM-LINK-010 One shared router.** Summa's routing MUST use Limen's
`Limen.Routing` API (`RouteTable.define`, `RouteCodec.create/parse/format`,
`Navigation.adopt/navigate/refine`, `ReturnTo.capture/resume`, `Link.share`),
with parameter types string, int, bool, date, month, enum and set, so that
moving to the published package is mechanical.

**SUM-LINK-011 Route inventory.** The route table MUST be published as
`.echelon/routes.json` in the `echelon.routes/v1` schema (sorted keys,
two-space indentation, final newline), and a test MUST fail when the file
and the table differ.

**SUM-LINK-012 The engine decides.** Routing MUST be pure engine code: the
kernel reports locations (`Initialize`, `LocationChanged`) and performs the
Navigation and Clipboard effects the engine asks for, and decides nothing.
