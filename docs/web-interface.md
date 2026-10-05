# Web Interface

A local web UI for the [work backlog](work-backlog-guide.md), backed by a
thin HTTP service. It adds no new capability over the CLI -- everything it
does, `./ros` already does -- it's a second, visual way to drive the same
kernel.

## Running it

```bash
npm run web
```

This publishes the F# engine to WebAssembly (`npm run build:wasm`, into
`build/wasm/`) and starts the server on `http://127.0.0.1:4310`, serving the
repository at the current working directory. Run `npm ci` once first: the page
loads Limen, Forma and Folio from `node_modules/`.

Options:

```bash
node tools/ros_server.mjs --root /path/to/other/repo --port 4321 --host 0.0.0.0
```

**The server binds to `127.0.0.1` by default and has no authentication.**
Anyone who can reach it can capture, block, abandon, start, or complete work
items, upload files into your repository, and write evidence-bearing
completions into your repository's history. Uploads are capped at 25 MB per
request. Only pass `--host 0.0.0.0` (or otherwise expose it beyond your own
machine) if you've put your own authentication or network boundary in front
of it.

After editing the engine (`src/Summa.Web.Engine`, `src/Summa.Web.Application`),
re-run `npm run web` or `npm run build:wasm`, then reload the page. Markup and
CSS edits (`web/`, `web-kernel/`) need only a reload.

## Architecture

This follows the same layering discipline as the rest of ROS's work
protocol: one place owns meaning, everything else is a thin adapter over it.

```
browser: web/index.html (Forma markup, Limen data-* bindings, Folio print surface)
        | Limen BrowserKernel (web-kernel/limen-wasm.js)
        v
src/Summa.Wasm ([JSExport] shim) -> src/Summa.Web.Application (Limen protocol, Aegis boundary)
        -> src/Summa.Web.Engine (pure F#: state, transitions, requests, view)
        | Http / transfer effects, performed by the kernel
        v
tools/ros_server.mjs (HTTP adapter -- no domain logic)
        | direct function call
        v
tools/ros_cli.mjs (the kernel -- owns every legality/state rule)
```

The page is built the same way as the other Echelon applications
([`DF-SUMMA-FND-2026-0002`](../research/decisions/DF-SUMMA-FND-2026-0002--build-summa-on-the-full-echelon-foundation-stack.md)):

- **`tools/ros_cli.mjs`** owns legality. The server routes call exactly the
  functions the CLI calls, so a request that would fail on the CLI fails the
  same way over HTTP, with the same message.
- **`tools/ros_server.mjs`** is a dependency-free `node:http` server: a route
  table over those functions, plus static files through
  `tools/web_static.mjs`, which serves the page at `/` and the pinned
  foundation packages, the shared kernel module and the published engine at
  their repository-relative paths (and nothing else).
- **`src/Summa.Web.Engine`** (Limen engine, pure F#) owns the page: one state
  value, `update : Msg -> Page -> Page * Effect list`, and `view`, the named
  values the HTML binds. It describes requests as data; it never performs them.
- **`src/Summa.Web.Application`** speaks Limen's protocol (JSON in; view,
  effects and the handshake out), decodes the server's bodies, and runs every
  message inside **Aegis** (`EchelonFoundry.Aegis.Core`). A body that breaks the
  agreed shape is an operational fault shown through Forma's fault component;
  a server refusal is an ordinary error message. See `aegis-boundaries.json`.
- **`web-kernel/limen-wasm.js`** starts Limen's `BrowserKernel` over the
  WebAssembly runtime, with the `limen.files` and `limen.transfer` packs so
  picked files upload as multipart by opaque id. It also registers **Folio**'s
  print elements.
- **`web/index.html`** is markup only: **Forma** patterns (fields, alerts,
  data grid, status lozenge, dialogs, file upload, fault) inside inert `<ef-*>`
  wrappers, styled by the installed `@echelon-foundry/design-system`. State cues
  are `data-*` attributes plus CSS (Limen refuses `data-bind-style`). Modal
  interactions are native `<dialog>`s opened with `command="show-modal"` and
  closed by `<form method="dialog">`; the engine only learns what was typed
  and which button confirmed. Printing shows Folio's `<ef-print-document>`
  projection of the same rows.

## API reference

All endpoints are JSON. Mutating endpoints return the same unified row shape
as `GET /api/work/:id` (backlog fields plus `liveWorkItem` once an item has
been started), so the client never has to special-case a response shape by
action.

| Method | Path | Equivalent CLI command |
|---|---|---|
| `GET` | `/api/work?tag=T&status=S` | `ros work list --tag T --status S` |
| `GET` | `/api/work/ready?tag=T` | `ros work ready --tag T` |
| `GET` | `/api/work/:id` | `ros work show ID` |
| `POST` | `/api/work` `{title, tags, priority, description?, id?, source?, sourceReference?, actor?}` | `ros add` |
| `POST` | `/api/work/:id/update` `{title?, description?, tags?, priority?}` | `ros work update ID` |
| `POST` | `/api/work/:id/attachments` `multipart/form-data`, one or more `file` parts | `ros work attach ID --file ...` |
| `GET` | `/api/work/:id/attachments/:attachmentId` | binary download (not a JSON route) |
| `POST` | `/api/work/:id/ready` | `ros work ready ID` |
| `POST` | `/api/work/:id/block` `{reason}` | `ros work block ID --reason ...` |
| `POST` | `/api/work/:id/abandon` `{reason}` | `ros work abandon ID --reason ...` |
| `POST` | `/api/work/:id/start` `{type, actor?}` | `ros work start ID --type ...` |
| `POST` | `/api/work/:id/resume` | `ros work resume ID` |
| `POST` | `/api/work/:id/complete` `{evidence: [{type, path}], conclusion?}` | `ros work done ID --evidence ...` |
| `GET` | `/api/validate` | `ros validate --json` |
| `GET` | `/api/status` | `ros status` |

`POST /api/work/:id/update` and `.../attachments` upsert a minimal backlog
record if `:id` was only ever `ros work begin`'d directly (never `add`ed) --
same behavior as the CLI's `update`/`attach`, so descriptive metadata and
files can be attached to any known work item, not just ones captured
through the backlog.

Errors are `4xx` with `{"error": "..."}`; the message is whatever
`ros_cli.mjs` threw.

## Tests

- `dotnet test Summa.sln`: the engine's decisions (carried over from the
  TypeScript engine tests it replaced), the page state machines, the Limen
  protocol and Aegis boundary (with a collector sink, proving faults are
  captured and refusals are not), foundation conformance, and the agreement
  between `index.html`'s bindings and the engine's view and events.
- `npm run test:browser` (Playwright): both pages against the real Node
  servers and throwaway repositories: capture with attachments, the full
  lifecycle through the dialogs, filters, refusals, an Aegis fault, the Folio
  print surface, and reflow at 320 CSS px.
