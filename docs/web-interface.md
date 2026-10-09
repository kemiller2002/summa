# Web Interface

A local web UI for the [work backlog](work-backlog-guide.md), served by the
Praxis command-line tool itself. It adds no new capability over the CLI --
everything it does, `./praxis` already does -- it's a second, visual way to
drive the same kernel. No Node.js, npm, or browser JavaScript is involved:
the pages are HTML rendered by the F# CLI, and every action is a plain
`<form method="post">`.

## Running it

```bash
./praxis web serve
```

This starts the server on `http://127.0.0.1:4310`, serving the repository at
the current working directory (or `--root PATH`). Options:

```bash
./praxis --root /path/to/other/repo web serve --port 4321 --host 0.0.0.0
```

Stop it with Ctrl+C (or SIGTERM).

**The server binds to `127.0.0.1` by default and has no authentication.**
Anyone who can reach it can capture, block, abandon, start, or complete work
items, upload files into your repository, and write evidence-bearing
completions into your repository's history. Uploads are capped at 25 MB per
request (1 MB for other request bodies). Only pass `--host 0.0.0.0` (or
otherwise expose it beyond your own machine) if you've put your own
authentication or network boundary in front of it.

## Using it

- **Queue** (`/`): capture a work item (title, tags, priority, description,
  and up to three files, each with an optional display name), filter by tag
  and status, and see every item with the actions the kernel currently
  allows it (one-click *Mark ready* / *Resume*; the others link to the
  item's page because they need input).
- **Item** (`/work/ID`): description, attachments (download links), and a
  form per allowed action -- block or abandon with a reason, start with a
  work type, complete with evidence `TYPE`/`PATH` rows and an optional
  research conclusion -- plus *Edit* (title, description, tags, priority;
  clearing the tag field removes all tags) and *Attach files*, and the raw
  `work show` record.
- **Validate** (`/validate`): the `validate --json` findings as a table. The
  header shows repository, protocol version, and validation state from
  `status --json`.

Every form post redirects back (post/redirect/get) with a notice, or with
the exact error message the CLI reported -- the page never re-implements a
rule such as "block requires a reason" or "completion requires
implementation and tests evidence".

### URLs (SAF-URL-1..10)

Every page is addressable, and a copied URL opens the same view. The route
tables are `Praxis.Application.Web.UrlState`, built on the pinned
`EchelonFoundry.Limen.Routing` engine library (`DF-ROS-2026-A057`). Their
inventories are `.echelon/routes.json` (this server) and
`.echelon/routes.hub.json` (the hub).

- **Canonical URLs.** Filters are `?tag=a,b&status=ready`: tags form one
  sorted set and empty fields are omitted. Any other spelling answers 303 to
  the canonical URL, so the address bar always holds it. That covers an empty
  form field, repeated or space-separated tags, an undeclared parameter, and
  `/index.html`.
- **Typed outcomes.** An unknown page is 404 "Not found", and an invalid
  filter value is 400 "Invalid link". Both have a way back.
- **Feedback.** The notice or error after a form post travels in a
  short-lived `praxis-flash` cookie (HttpOnly, SameSite=Strict), shown once,
  never in the URL.
- **Link to this view.** Every page ends with its canonical URL as a link,
  plus the absolute address in a read-only field.
- **Replace on refinement, one-action copy.** One approved script,
  `/url-state.js` (`DF-ROS-2026-A058`), does exactly two things: a filter
  submit replaces the history entry instead of pushing one (SAF-URL-3), and a
  `Copy link` button copies "Link to this view" in one click (SAF-URL-10). It
  is a progressive enhancement. With JavaScript off, filters push as before,
  the button stays hidden and the address is selected by hand.
- **Content-Security-Policy.** Every response carries a strict CSP: scripts
  only from this origin (the page pins `url-state.js` by its SHA-256
  integrity), no inline script, no `eval`, `connect-src 'none'` and Trusted
  Types. The script makes no network request and keeps no storage, and
  `UrlEnhancementTests` fails if it grows any such capability.

## Architecture

This follows the same layering discipline as the rest of Praxis's work
protocol: one place owns meaning, everything else is a thin adapter over it.

```
browser (server-rendered HTML, plain form posts; optional url-state.js)
        | HTTP
        v
praxis web serve (Praxis.Cli.WebInterface: routing + rendering, no domain logic)
        | runs this same CLI as a child process: praxis --root ROOT work ...
        v
praxis work capture/update/attach/backlog-transition/start/resume/block/complete,
work list/show, validate --json, status --json  (the kernel)
```

- **One orchestration path.** Every read and every change is exactly one
  invocation of the CLI's own command (`Praxis.Cli.WebInterface.commandLine`
  maps each operation to its argument vector; no shell is involved, so no
  title or description can be interpreted as shell syntax). A request that
  would fail on the CLI fails the same way over HTTP, with the same message.
  Mutations answer with the item's `work show` record.
- **Pure core, effects at the edge.** Routing, form and `multipart/form-data`
  parsing, the operation-to-command mapping, and HTML rendering (which
  escapes every repository-provided value) are pure functions with unit
  tests; the only effects are the HTTP listener (`System.Net.HttpListener`),
  the child process, short-lived upload temp files (deleted after the CLI's
  `work attach` copies them), and the attachment download, which reads the
  stored file through `FileWorkListRepository.readAttachment`.
- **Attachments.** A file's display name is the optional name typed next to
  its picker, else the uploaded file's own name; on-disk storage names are
  generated by `work attach` so two attachments can share a display name.
  The CLI records no content type for attachments, so downloads are served
  as `application/octet-stream` with the display name as the filename.
- **Presentation: Forma.** Pages are presented by a pinned Forma release
  (SAF-FORMA-1..6, PRX-UI-030). `vendor/forma/forma.lock` names the version,
  the release URL and the sha256 of the unmodified release tarball beside it;
  the CLI embeds both, verifies the digest, and serves Forma's `dist/all.css`
  at `/forma/<version>/all.css`, linked first by every page. Markup uses Forma
  patterns: skip link, site typography, data grids, fields, actions, status
  lozenges (the status word is always text, never color alone), empty states,
  inline faults for refused operations and the fault banner (with the Aegis
  `AG-` reference) for operational failures. Native HTML stays the semantic
  authority; Praxis ships no presentation of its own. `web/styles.css`, when
  present, is loaded after Forma as an optional project override and ships
  empty. To move to a new Forma release, replace the tarball and lock
  together and run the web, hub and Forma tests.
- **Limen (PRX-UI-031).** The web interface is server-rendered and
  script-optional: every action is a form post, legality comes from the CLI,
  and the one approved script (`DF-ROS-2026-A058`) only enhances history and
  copying. There is no
  browser runtime for Limen to host, so `.echelon/foundations.json` declares
  Limen not applicable for this architecture. A future browser application
  would adopt Limen.

## API reference

The JSON API remains for scripts and other tools. Mutating endpoints return
the same unified row shape as `GET /api/work/:id` (backlog fields plus
`liveWorkItem` once an item has been started). Request bodies are JSON (the
attachment route takes `multipart/form-data`; the other `POST` routes also
accept form-encoded fields).

| Method | Path | Equivalent CLI command |
|---|---|---|
| `GET` | `/api/work?tag=T&status=S` | `praxis work list --tag T --status S` |
| `GET` | `/api/work/ready?tag=T` | `praxis work ready --tag T` |
| `GET` | `/api/work/:id` | `praxis work show ID` |
| `POST` | `/api/work` `{title, tags, priority, description?, id?, source?, sourceReference?, actor?}` | `praxis work capture --title ...` |
| `POST` | `/api/work/:id/update` `{title?, description?, tags?, priority?}` | `praxis work update --id ID ...` |
| `POST` | `/api/work/:id/attachments` `multipart/form-data`, one or more `file` parts | `praxis work attach --id ID --file PATH=NAME ...` |
| `GET` | `/api/work/:id/attachments/:attachmentId` | binary download (not a JSON route) |
| `POST` | `/api/work/:id/ready` | `praxis work backlog-transition --id ID --action ready` |
| `POST` | `/api/work/:id/block` `{reason}` | `praxis work block --id ID --reason ...` |
| `POST` | `/api/work/:id/abandon` `{reason}` | `praxis work backlog-transition --id ID --action abandon --reason ...` |
| `POST` | `/api/work/:id/start` `{type, actor?}` | `praxis work start --id ID --type ...` |
| `POST` | `/api/work/:id/resume` `{actor?}` | `praxis work resume --id ID` |
| `POST` | `/api/work/:id/complete` `{evidence: [{type, path}], conclusion?, actor?}` | `praxis work complete --id ID --evidence TYPE=PATH ...` |
| `GET` | `/api/validate` | `praxis validate --json` |
| `GET` | `/api/status` | `praxis status --json` |

Every event is recorded at the server's current time (`--occurred-at`).
`update` and `attachments` upsert a minimal backlog record if `:id` was only
ever started directly (never captured) -- the CLI's own behaviour.

Errors are `4xx` with `{"error": "..."}`, where the message is the CLI's own
`ERROR` line; an unknown `/api/...` route is `404`, as is an unknown
attachment.

## Executions: the local control plane and operator views

The same host serves governed executions (`docs/execution-runtime.md`,
PRX-CTL-001/005, PRX-UI-001/020). Every route runs `praxis execution ...
--json` and returns or renders exactly what the CLI computed: legal actions,
their reasons and actor requirements come from `LegalActions`, never from the
host (PRX-UI-034, PRX-CTL-006).

| Method | Path | Runs |
|---|---|---|
| `GET` | `/api/control-plane` | the declared listen scope (`praxis.control-plane/1`: host, port, `loopbackOnly`, authentication none) |
| `GET` | `/api/executions?workItem=ID` | `praxis execution list [--work-item ID] --json` |
| `GET` | `/api/executions/:id` | `praxis execution show ID --json`: envelope, actor and execution host, role, steps with receipt state and attributions, scope effects, verification, divergence, containment profile, legal actions |
| `GET` | `/api/executions/:id/actions` | `praxis execution actions ID --json` |
| `POST` | `/api/executions/:id/transitions` `{action, reason?}` | `praxis execution transition ID --action ACTION` (or `rebind`); answers the new state, or `409 {"error", "refused": true}` for a governance refusal (exit 3) with nothing changed |
| `GET` | `/executions/:id` | the execution page |
| `POST` | `/executions/:id/transitions` (form) | the same transition, then a redirect with the CLI's notice or error |

The work detail page lists the item's executions. The execution page shows
state and blocked reason, actor and execution host (provider, model and
runtime are attributes of the actor, never the owner of the work,
PRX-CTL-012), evaluator and verification, step receipts labelled `receipt
matches`, `receipt mismatch` or `unknown effect`, obligations (unresolved
scope effects, workspace divergence), unknown effects, containment per
restriction, and every legal action with its availability, the engine's
reasons when unavailable, and who may take it (PRX-UI-007/021). A
human-required action is labelled `human required` in text (PRX-UI-026); its
form asks for the operator's name and a confirmation and records the
transition as a human actor (`--actor-kind human --actor NAME`) through the
same CLI path, so it carries normal provenance (PRX-UI-004/027). Like all
Praxis identity, that declaration is self-reported, not authenticated: the
server has no authentication and binds to loopback by default. Requests made
through the JSON API carry the server process's own identity.

## Tests

`tests/Praxis.Tests/WebInterfaceTests.fs` unit-tests the pure pieces (escaping,
form and multipart parsing, routing, the operation-to-command mapping, the
allowed-action projection) and starts the real `praxis web serve` on a free
loopback port against a temporary repository, driving it with `HttpClient`:
the `ready` gate before `start`, evidence requirements on `complete`,
terminal `abandon`, `block` dispatching to the backlog or the in-flight item,
multipart uploads (several files, custom names, duplicate display names,
byte-for-byte download), the HTML form flows (capture with a file, redirects
carrying the CLI's own errors, clearing tags, completing with evidence), and
that no page carries script and hostile titles are escaped.
`tests/Praxis.Tests/WebExecutionTests.fs` covers the execution routes: a
work-bound execution listed with legal actions, reasons and actor
requirements, the declared listen scope, a refused transition as a `409`
with state unchanged, and a human form transition recorded as a human.
