# Using the Praxis Work Backlog

A practical, example-driven guide to capturing and executing work once Praxis is
installed in a repository. For the underlying design, see the "Local
backlog" section of [`work-protocol.md`](work-protocol.md) and
[`DF-ROS-2026-A008`](https://github.com/kemiller2002/praxis/blob/v3.7.2/research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md).

Two things are layered here, and it helps to keep them straight from the
start:

- **The backlog** (`praxis add`, `praxis work list/ready/show/block/abandon`) is a
  cheap, repository-local place to capture and triage work that doesn't have
  an ID yet. It owns nothing about execution.
- **The tracked protocol** (`praxis work begin/block/resume/complete`) is the
  existing, authoritative record of work actually in progress, with
  attribution and evidence requirements. `praxis work start` is the bridge
  between the two.

All commands below assume you're at the repository root and `./praxis` is
executable (`chmod +x praxis` if not, or run `sh praxis ...`). An older
installation may only have `./ros`, a compatibility alias of `./praxis`.

---

## 1. Capturing work

### The minimum

```bash
./praxis add "Investigate WASM state payload growth"
```

Output:

```json
{
  "id": "WI-0001",
  "title": "Investigate WASM state payload growth",
  "tags": [],
  "priority": "medium",
  "status": "captured",
  "createdAt": "2026-08-19T13:20:51.298Z",
  "updatedAt": "2026-08-19T13:20:51.298Z",
  "createdBy": "unknown",
  "source": "manual",
  "sourceReference": null
}
```

The ID (`WI-0001`, `WI-0002`, ...) is generated for you. Nothing else is
required — no YAML, no directory, no registry update.

### With tags

Tags are free-form classification, not lifecycle state. Two equivalent ways
to pass several:

```bash
./praxis add "Add WASM navigation transition" --tag wasm --tag routing
```

```bash
./praxis add "Add WASM navigation transition" -t wasm,routing
```

Mix repeats and comma lists freely — they're merged and de-duplicated:

```bash
./praxis add "Investigate state payload growth" -t wasm,state --tag performance
```

### With a priority

```bash
./praxis add "Fix flaky retry test" --priority high
```

Valid values: `high`, `medium` (the default if omitted), `low`. Priority
affects nothing about legality — a `low` item that's `ready` is still
`ready`; a `high` item that's `blocked` is still `blocked`. It's a hint for
you or an agent choosing what to pick up next, not a workflow gate.

### With an explicit ID instead of an auto-generated one

Useful when you want the backlog ID to match an external ticket reference:

```bash
./praxis add "Migrate legacy config loader" --id CFG-MIGRATE-001
```

This fails loudly if the ID is already used, either in the backlog or by an
in-flight tracked item:

```bash
./praxis add "Duplicate" --id CFG-MIGRATE-001
# ERROR work item 'CFG-MIGRATE-001' already exists
```

### Recording where the work came from

Optional provenance, useful when tooling (not a human) files the item:

```bash
./praxis add "Address flaky CI failure" \
  --source test-failure \
  --source-reference tests/navigation.spec.ts
```

```bash
./praxis add "Follow up on architecture ambiguity" \
  --source agent-discovery \
  --source-reference execution-2026-08-19-0042 \
  --actor agent:reviewer
```

`--actor` records who captured it (defaults to the `PRAXIS_ACTOR` environment
variable, or the legacy `ROS_ACTOR`, then `"unknown"`). The item also receives `createdByActor`: the
structured actor (`kind`, `id`, and `provider`/`model`/`runtime` when
applicable) resolved exactly as a work execution's identity is. `createdBy`
stays as the legacy free-text field. See `agent-provenance.md`.

### With a description

A longer explanation than the title, stored directly on the item (separate
from the optional detail file covered in [§2](#2-browsing-the-backlog)):

```bash
./praxis add "Investigate WASM state payload growth" \
  --description "Payload grows superlinearly with history depth; root cause unknown."
```

### With one or more files attached

Attach local files at capture time. Each `--file` is repeatable; a bare path
uses its own filename as the associated name, or give it a different one
with `PATH=NAME`:

```bash
./praxis add "Review architecture sketch" --file ./notes/sketch.png
```

```bash
./praxis add "Review architecture sketch" \
  --file ./notes/sketch.png=diagram.png \
  --file ./notes/context.md=background.md
```

Two files can be attached under the *same* associated name — they stay
distinct on disk (`praxis work show` and the web UI list both).

### All flags at once

```bash
./praxis add "Rename WasmStateStore" \
  --tag cleanup,wasm \
  --priority low \
  --description "Purely a rename; no behavior change." \
  --file ./notes/before-after.diff \
  --source manual \
  --actor kevin
```

---

## 2. Browsing the backlog

### Everything, unified

```bash
./praxis work
```

This is shorthand for `praxis work list` with no filters — captured items,
ready items, blocked items, and anything already in-flight or completed, all
in one list. There is one queue; you never have to check multiple places.

### By tag

```bash
./praxis work list --tag wasm
```

Multiple `--tag`/`-t` flags narrow further (an item must have **all** of
them):

```bash
./praxis work list --tag wasm --tag routing
```

### By status

```bash
./praxis work list --status captured
```

```bash
./praxis work list --status blocked
```

Status values you'll see: `captured`, `ready`, `blocked`, `abandoned` for
backlog-only items, and `active`, `complete` once an item has been started
(see [§5](#5-starting-work)).

### Combined

```bash
./praxis work list --tag wasm --status ready
```

### Just what's ready to pick up

```bash
./praxis work ready
```

Equivalent to `praxis work list --status ready`, with tag filtering supported
the same way:

```bash
./praxis work ready --tag wasm
```

This is the query an agent should run instead of guessing from prose — see
[`AGENTS.md`](../AGENTS.md).

### One item, in full

```bash
./praxis work show WI-0001
```

If a detail file exists at `.ros/work/items/WI-0001.md`, its contents are
included in the output. Detail files are entirely optional and manually
authored — nothing forces you to write one:

```bash
mkdir -p .ros/work/items
cat > .ros/work/items/WI-0001.md <<'EOF'
# WI-0001 — Investigate WASM state payload growth

## Objective
Determine why payload size grows superlinearly with history depth.

## Constraints
- No new dependencies.
- Must not change the public WASM boundary.

## Completion
- Root cause identified and documented.
- Follow-up work items filed for any required fix.
EOF

./praxis work show WI-0001
```

---

## 3. Moving items through the backlog lifecycle

The legal transitions are: `captured -> ready`, `captured -> abandoned`,
`ready -> blocked`, `ready -> abandoned`, `blocked -> ready`,
`blocked -> abandoned`. Anything else is rejected with a clear error.

### Mark ready

```bash
./praxis work backlog-transition --action ready --id WI-0001 --occurred-at TIMESTAMP
```

Note the dual meaning of `ready`: **no ID** queries ("what's ready");
**with an ID** it mutates ("make this ready"). Same verb, disambiguated by
whether you gave it something to act on.

### Block something before it's even started

```bash
./praxis work block --id WI-0001 --occurred-at TIMESTAMP --reason "waiting on benchmark results"
```

`--reason` is required — an unexplained blocked item isn't useful to anyone
picking up work later.

### Unblock it

```bash
./praxis work backlog-transition --action ready --id WI-0001 --occurred-at TIMESTAMP
```

Same command as marking something ready the first time; a blocked item
returning to `ready` is not treated as a special case.

### Block or unblock several at once

```bash
./praxis add "First thing" --id WI-A
./praxis add "Second thing" --id WI-B
./praxis work backlog-transition --action ready --id WI-A --occurred-at TIMESTAMP
./praxis work backlog-transition --action ready --id WI-B --occurred-at TIMESTAMP
./praxis work block --id WI-A --id WI-B --occurred-at TIMESTAMP --reason "waiting on the same upstream fix"
```

`block` also accepts a mix of backlog IDs and already-started (tracked) IDs
in the same call — each is routed to the right place automatically:

```bash
./praxis work start --id WI-B --occurred-at TIMESTAMP --type feature
./praxis work block --id WI-A --id WI-B --occurred-at TIMESTAMP --reason "upstream outage"
```

Here `WI-A` (still just captured/ready) gets a backlog block; `WI-B`
(already started) gets the existing in-flight block. One command, two
different stores under the hood, correctly dispatched.

### Abandon something you've decided not to do

```bash
./praxis work backlog-transition --action abandon --id WI-0002 --occurred-at TIMESTAMP --reason "superseded by WI-0001"
```

Abandonment is **terminal** — there's no `abandoned -> ready` transition.
If you change your mind, capture it again:

```bash
./praxis add "Rename WasmStateStore" --tag cleanup
```

---

## 4. Editing an item and attaching files

Description, title, tags, and priority can all be changed later — on a
backlog item or one that's already started, it doesn't matter:

```bash
./praxis work update WI-0001 \
  --title "Investigate WASM payload growth (root cause)" \
  --description "Narrowed to the serialization layer." \
  --tag wasm,perf \
  --priority high
```

Only the flags you pass are changed; omit `--tag` entirely to leave tags
untouched (passing `--tag` with an empty value clears them).

### Attaching files after the fact

Same `--file PATH[=NAME]` syntax as `add`, repeatable, works on any known
ID — including one that was `praxis work begin`'d directly and never went
through `praxis add`:

```bash
./praxis work attach WI-0001 --file ./notes/benchmark-results.csv
```

```bash
./praxis work attach WI-0001 \
  --file ./notes/before.png=before.png \
  --file ./notes/after.png=after.png
```

Attached files live under `.ros/work/attachments/<ID>/`; `praxis work show ID`
lists each one's associated name and size.

---

## 5. Starting work

Once an item is `ready`, hand it to the tracked, attributed protocol:

```bash
./praxis work start --id WI-0001 --occurred-at TIMESTAMP --type feature
```

This requires `ready` — starting a merely `captured` item fails with a
pointer to the missing step:

```bash
./praxis work start --id WI-0003 --occurred-at TIMESTAMP
# ERROR cannot start backlog item 'WI-0003' from 'captured'; mark it ready first
```

And starting an abandoned item fails permanently:

```bash
./praxis work start --id WI-0002 --occurred-at TIMESTAMP
# ERROR cannot start backlog item 'WI-0002': it was abandoned
```

`--type` determines which completion evidence will be required later
(configured per-type in `ros.json`'s `workProtocol.completionEvidence`).
Common types: `feature`, `bug`, `research`, `mechanical`, `maintenance`,
`infrastructure`.

`start` is a thin wrapper around the existing `praxis work begin` — from this
point forward, `praxis work show WI-0001` reflects live execution state
(`active`, then `blocked`/`complete`), not the backlog's own status field.
You never have to reconcile the two by hand.

### Checking what you're allowed to do next

```bash
./praxis work context WI-0001
```

Reports current state, legal next actions, and exactly which evidence types
completion will require for this item's `--type`.

---

## 6. Finishing work

### With evidence

```bash
./praxis work done --id WI-0001 --occurred-at TIMESTAMP \
  --evidence implementation=src/wasm/state-store.ts \
  --evidence tests=tests/wasm/state-store.test.ts
```

`done` is a plain alias for the existing `complete` — same rules, same
evidence-path-must-exist check, same event recorded. Use whichever name
reads better to you; they're interchangeable.

```bash
./praxis work complete --id WI-0001 --occurred-at TIMESTAMP \
  --evidence implementation=src/wasm/state-store.ts \
  --evidence tests=tests/wasm/state-store.test.ts
```

### Missing required evidence is rejected

```bash
./praxis work done --id WI-0001 --occurred-at TIMESTAMP
# ERROR completion evidence missing for 'WI-0001': implementation, tests
```

### Research work with a conclusion

```bash
./praxis work start --id WI-0004 --occurred-at TIMESTAMP --type research
./praxis work done --id WI-0004 --occurred-at TIMESTAMP \
  --conclusion inconclusive \
  --evidence research-record=research/findings/wasm-payload-growth.md
```

`--conclusion` accepts `inconclusive` explicitly — research completing
without a supported hypothesis is still a legitimate, recorded outcome.

### Mechanical/no-evidence work

Some work types (configured with an empty evidence list, e.g.
`mechanical`) complete without `--evidence` at all:

```bash
./praxis add "Reformat generated config" --id FMT-001
./praxis work backlog-transition --action ready --id FMT-001 --occurred-at TIMESTAMP
./praxis work start --id FMT-001 --occurred-at TIMESTAMP --type mechanical
./praxis work done --id FMT-001 --occurred-at TIMESTAMP
```

---

## 7. Checking your work

Run after any batch of changes:

```bash
./praxis validate
```

```bash
./praxis validate --json
```

The `--json` form gives a stable structured result (`valid`, `findings[]`,
each with a `repair` hint) — useful for scripting or agent consumption.

If canonical Markdown records (decisions, evidence, etc. — not backlog
items, which have no registry) changed:

```bash
./praxis registry build
```

```bash
./praxis registry build --dry-run
```

A one-shot combined view of state + validation:

```bash
./praxis status
```

---

## 8. A complete walkthrough

```bash
# Capture three things as you notice them
./praxis add "Add WASM navigation transition" -t wasm,routing --priority high
./praxis add "Investigate state payload growth" -t wasm,state
./praxis add "Clean up dead JS state handler" -t cleanup --priority low

# See what you've got
./praxis work

# Triage: two are worth doing now, one isn't
./praxis work backlog-transition --action ready --id WI-0001 --occurred-at TIMESTAMP
./praxis work backlog-transition --action ready --id WI-0002 --occurred-at TIMESTAMP
./praxis work backlog-transition --action abandon --id WI-0003 --occurred-at TIMESTAMP --reason "handler was already removed upstream"

# Pick up the higher-priority one
./praxis work start --id WI-0001 --occurred-at TIMESTAMP --type feature

# ...implement it...
mkdir -p src/wasm tests/wasm
echo "export const navigate = () => {};" > src/wasm/nav.ts
echo "// covers navigate()" > tests/wasm/nav.test.ts

./praxis work done --id WI-0001 --occurred-at TIMESTAMP \
  --evidence implementation=src/wasm/nav.ts \
  --evidence tests=tests/wasm/nav.test.ts

# Confirm the repository is still consistent
./praxis validate
```

Final state, at a glance:

```bash
./praxis work
```

```json
[
  { "id": "WI-0001", "status": "complete", "...": "..." },
  { "id": "WI-0002", "status": "ready",    "...": "..." },
  { "id": "WI-0003", "status": "abandoned","...": "..." }
]
```

Or just open [`.ros/work/queue.md`](../.ros/work/queue.md) in an editor —
it's regenerated on every backlog change and reads as a plain table.

Prefer a UI to typing commands? `./praxis web serve` starts a local web interface
over this same backlog — see [`web-interface.md`](web-interface.md).

---

## 9. Where the data actually lives

| File | Role |
|---|---|
| `.ros/work/queue.json` | Canonical backlog storage. Don't hand-edit unless you know what you're doing. |
| `.ros/work/queue.md` | Generated, human-readable projection of the queue. Regenerated automatically. |
| `.ros/work/items/<ID>.md` | Optional, manually-authored detail for one item. |
| `.ros/work/attachments/<ID>/` | Files attached via `praxis add --file` / `praxis work attach`. |
| `.ros/context/current.json` | Canonical in-flight execution state once `start`/`begin` has run. |
| `.ros/events/events.jsonl` | Immutable event log (started, blocked, resumed, completed) for attribution. |

`.ros/work/**` is excluded from meaningful-change attribution enforcement —
capturing and triaging backlog items is bookkeeping, not application change,
so it never blocks `praxis validate` on its own.

---

## 10. Quick reference

| Command | What it does |
|---|---|
| `praxis add "title" [--tag a,b] [--priority p] [--id ID] [--description D] [--file PATH[=NAME]]...` | Capture a new backlog item |
| `praxis work` / `praxis work list [--tag T] [--status S]` | List the unified queue |
| `praxis work ready [--tag T]` | Query: items with no blocker |
| `praxis work backlog-transition --action ready --id ID --occurred-at TIMESTAMP` | Mutate: captured/blocked → ready |
| `praxis work show ID` | Full detail for one item, including any detail file and attachments |
| `praxis work update ID [--title T] [--description D] [--tag a,b] [--priority p]` | Change descriptive metadata, any time |
| `praxis work attach ID --file PATH[=NAME]...` | Attach one or more files to an existing item |
| `praxis work start --id ID --occurred-at TIMESTAMP [--type T]` | Promote a ready item into tracked execution |
| `praxis work block ID... --reason "..."` | Block a backlog or in-flight item (auto-dispatched) |
| `praxis work backlog-transition --action abandon --id ID --occurred-at TIMESTAMP --reason "..."` | Terminal: drop a backlog item |
| `praxis work done --id ID --occurred-at TIMESTAMP [--evidence T=path]...` | Complete tracked work (alias for `complete`) |
| `praxis work context ID` | Legal next actions + required evidence for one item |
| `praxis validate` / `praxis validate --json` | Check repository consistency |
| `praxis status` | Combined state + validation summary |
