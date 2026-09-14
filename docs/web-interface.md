# Web Interface

A local web UI for the [work backlog](work-backlog-guide.md), backed by a
thin HTTP service. It adds no new capability over the CLI -- everything it
does, `./ros` already does -- it's a second, visual way to drive the same
kernel.

## Running it

```bash
npm run web
```

This builds `web/app.ts` and starts the server on `http://127.0.0.1:4310`,
serving the repository at the current working directory.

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

After editing `web/app.ts`, either re-run `npm run web` (which rebuilds
first) or `npm run build:web` on its own, then reload the page.

## Architecture

This follows the same layering discipline as the rest of ROS's work
protocol: one place owns meaning, everything else is a thin adapter over it.

```
browser (HTML + TypeScript)
        | fetch (command)
        v
tools/ros_server.mjs (HTTP adapter -- no domain logic)
        | direct function call
        v
tools/ros_cli.mjs (the kernel -- owns every legality/state rule)
```

- **`tools/ros_cli.mjs`** is unchanged in behavior. The functions the server
  calls (`captureWork`, `backlogTransition`, `startWork`, `transition`,
  `blockWork`, `showWork`, `mergedWorkView`, `statusView`, `updateWork`,
  `attachFile`, `attachmentFilePath`, `validate`) are the exact same
  functions `ros`'s CLI commands call -- the server does not duplicate any
  transition, evidence, or validation rule. A request that would fail on
  the CLI fails the same way over HTTP, with the same message.
- **`tools/ros_server.mjs`** is a dependency-free `node:http` server. It
  parses JSON (and, for file uploads, standard browser-generated
  `multipart/form-data` via a small hand-written parser -- no upload
  library), matches a small route table, calls one kernel function per
  route, and serializes the result. It makes no decisions about what's
  legal. A file's associated name comes from the browser `File`'s name at
  upload time (or an override the UI sends alongside it); on-disk storage
  names are generated separately so two attachments can share a display
  name without colliding.
- **`web/app.ts`** is a framework-free, purely functional TypeScript client:
  a single `state` value, one `setState` that re-renders, and pure functions
  from `state` to DOM. There is no two-way data binding -- typing in a
  filter box never mutates `state` directly, it triggers a fetch whose
  result replaces `state` wholesale. Modal interactions (block/abandon
  reason, start type, completion evidence) use the browser's native
  `<dialog>` + `<form method="dialog">`, which close themselves and report
  which button was pressed without any custom modal JavaScript.
- Client-side "validation" doesn't exist as a separate layer: the UI shows
  whatever error message the server (i.e. the kernel) returns, rather than
  re-implementing rules like "block requires a reason" or "completion
  requires implementation and tests" in JavaScript.

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

`tests/ros-server.test.mjs` drives the HTTP API directly (no browser), and
asserts the same lifecycle rules the CLI tests assert: the `ready` gate
before `start`, evidence requirements on `complete`, terminal `abandon`, and
`block` correctly dispatching to the backlog or the in-flight item depending
on where the ID currently lives. It also drives real multipart uploads
(Node's native `FormData`/`File`), covering multiple files in one request, a
custom name overriding the original filename, two attachments sharing a
display name staying byte-distinct, and byte-for-byte download. There's no
automated browser test for `web/app.ts` itself; it was verified manually
end-to-end (capture with a description and an attached file, filter,
start, block/complete with evidence, editing an item, and attaching further
files through the dialog -- including the native dialogs) against a scratch
repository.
