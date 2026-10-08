# Summa's web pages

Summa builds its two web pages, the per-repository work backlog (`web/`) and
the project-administration hub (`web-hub/`), on the full Echelon foundation
stack
([`DF-SUMMA-FND-2026-0002`](../research/decisions/DF-SUMMA-FND-2026-0002--build-summa-on-the-full-echelon-foundation-stack.md)).
This document is Summa's own. The Praxis-owned
[`web-interface.md`](web-interface.md) and
[`project-administration-hub.md`](project-administration-hub.md) describe the
web interface and hub that Praxis ships; where they describe how the pages are
built, styled, run or tested, this document is authoritative for Summa. The
HTTP API, its security notes and the hub's registry model are as those
documents describe.

## The accounting application

`app/` is Summa's accounting application: customers, invoices with
readiness, review and issue, the issued invoice as its Folio document,
payments and receivables. It is a third Limen page with its own engine
(`src/Summa.Web.Engine/Accounting.fs`, `src/Summa.Web.Application/AccountingWire.fs`)
and WASM export (`DispatchAccounting`). The engine decides only through Summa's domain commands
([`DF-SUMMA-2026-0009`](../research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md)).

```bash
npm run app     # http://127.0.0.1:4330
```

It needs no server API. When the deployment names no data location, the books
live in this browser's storage. They are checked like stored data every time
they are read, and books that fail their checks are refused. A new browser
starts demo books. Printing opens the browser's print dialog through the
`summa.print` pack (`web-kernel/print.js`). `tests/browser/app.spec.js`
drives the real page.

### Deep links

Every view is a link (SUM-LINK-001..012,
[`DF-SUMMA-2026-0010`](../research/decisions/DF-SUMMA-2026-0010--deep-links-through-limen-routing.md)):
the place, list filters, sort, search, report dates and the invoice's tab live
after the `#`, so a copied link opens the same view on any static host:

```text
#/invoices?q=acme&status=paid,unpaid&from=2026-01-01&sort=due
#/invoices/INV-0001?tab=history
#/receivables?asOf=2026-06-30&customer=CUST-0001
```

- The routes are `src/Summa.Web.Engine/Routes.fs` on Limen's router
  (`vendor/Limen.Routing`, until Limen 0.9.0 ships). Their inventory is
  `.echelon/routes.json`; regenerate it with
  `SUMMA_WRITE_ROUTES=1 dotnet test tests/Summa.Web.Tests`.
- Navigation is plain `<a href="#/...">` links. The engine adopts the address
  the kernel reports, pushes when it moves to another place (issuing an
  invoice), and replaces when a view is refined (a filter, a tab), so Back
  returns to the previous place.
- Links hold opaque ids, dates and declared names only: never an amount, a
  balance, a token or an account number.
- A link to nothing, a value Summa does not understand, or a place the person
  may not open each has its own page. "Copy link" copies the canonical
  address. `tests/browser/deep-links.spec.js` drives all of this, including
  a static host at a sub-path.

## Running them

```bash
npm ci          # once: the pages load Limen, Forma and Folio from node_modules/
npm run web     # the work backlog on http://127.0.0.1:4310
npm run hub     # the hub on http://127.0.0.1:4320
```

Each script publishes the F# engine to WebAssembly (`npm run build:wasm`, into
`build/wasm/`) and then starts its server, serving the repository at the
current working directory. Neither server has authentication, and both bind to
`127.0.0.1` by default; see the security notes in `web-interface.md` and
`project-administration-hub.md` before binding them anywhere else.

After editing the engine (`src/Summa.Web.Engine`, `src/Summa.Web.Application`),
re-run `npm run web` (or `npm run hub`) or `npm run build:wasm`, then reload
the page. Markup and CSS edits (`web/`, `web-hub/`, `web-kernel/`) need only a
reload.

## Architecture

```
browser: web/index.html or web-hub/index.html
         (Forma markup, Limen data-* bindings, Folio print surface)
        | Limen BrowserKernel (web-kernel/limen-wasm.js)
        v
src/Summa.Wasm ([JSExport] shim) -> src/Summa.Web.Application (Limen protocol, Aegis boundary)
        -> src/Summa.Web.Engine (pure F#: state, transitions, requests, view)
        | Http / transfer effects, performed by the kernel
        v
tools/ros_server.mjs or tools/ros_hub_server.mjs (HTTP adapter -- no domain logic)
        | direct function call
        v
tools/ros_cli.mjs (the kernel -- owns every legality/state rule)
```

- **`tools/ros_cli.mjs`** owns legality. The server routes call exactly the
  functions the CLI calls, so a request that would fail on the CLI fails the
  same way over HTTP, with the same message.
- **`tools/ros_server.mjs`** and **`tools/ros_hub_server.mjs`** are
  dependency-free `node:http` servers: a route table over those functions,
  plus static files through `tools/web_static.mjs`, which serves the page at
  `/` and the pinned foundation packages, the shared kernel module and the
  published engine at their repository-relative paths (and nothing else).
- **`src/Summa.Web.Engine`** (Limen engine, pure F#) owns both pages
  (`HubPage.fs` for the hub): one state value,
  `update : Msg -> Page -> Page * Effect list`, and `view`, the named values
  the HTML binds. It describes requests as data; it never performs them.
- **`src/Summa.Web.Application`** speaks Limen's protocol (JSON in; view,
  effects and the handshake out), decodes the server's bodies, and runs every
  message inside **Aegis** (`EchelonFoundry.Aegis.Core`). A body that breaks
  the agreed shape is an operational fault shown through Forma's fault
  component; a server refusal is an ordinary error message. See
  `aegis-boundaries.json`.
- **`web-kernel/limen-wasm.js`** starts Limen's `BrowserKernel` over the
  WebAssembly runtime, with the `limen.files` and `limen.transfer` packs so
  picked files upload as multipart by opaque id. It also registers **Folio**'s
  print elements.
- **`web/index.html`** and **`web-hub/index.html`** are markup only: **Forma**
  patterns (fields, alerts, data grid, status lozenge, dialogs, file upload,
  fault) inside inert `<ef-*>` wrappers. State cues are `data-*` attributes
  plus CSS (Limen refuses `data-bind-style`). Modal interactions, including
  the hub's unregister confirmation, are native `<dialog>`s opened with
  `command="show-modal"` and closed by `<form method="dialog">`; the engine
  only learns what was typed and which button confirmed. Printing shows
  Folio's `<ef-print-document>` projection of the same rows (the aggregated
  queue, on the hub).

## Styling

Each page links one stylesheet that Summa owns: `web/summa.css` and
`web-hub/summa.css`. Each imports, in order:

1. `@echelon-foundry/design-system/dist/all.css` (**Forma**: tokens,
   foundations and components), from the installed package;
2. `@echelon-foundry/print-components/src/styles/print.css` (**Folio**), for
   print media only, from the installed package;
3. `web-kernel/page.css`, the page composition both pages share.

Nothing is copied from the packages, no Forma token is redefined and no Forma
component is restyled; `tests/Summa.Web.Tests/FoundationsConformanceTests.fs`
enforces all of this. The `styles.css` file beside each page is the stylesheet
Praxis installs for its own pages. Summa's pages do not load it, and it is
never edited here: Praxis replaces it on upgrade.

## Tests

- `dotnet test Summa.sln`: the engine's decisions, the page state machines,
  the Limen protocol and Aegis boundary (with a collector sink, proving faults
  are captured and refusals are not), foundation conformance, and the
  agreement between each `index.html`'s bindings and the engine's view and
  events.
- `npm run test:browser` (Playwright): both pages against the real Node
  servers and throwaway repositories: capture with attachments, the full
  lifecycle through the dialogs, filters, refusals, an Aegis fault, the Folio
  print surface, and reflow at 320 CSS px.
