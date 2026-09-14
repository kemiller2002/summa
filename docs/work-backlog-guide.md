# Using the ROS Work Backlog

A practical, example-driven guide to capturing and executing work once ROS is
installed in a repository. For the underlying design, see the "Local
backlog" section of [`work-protocol.md`](work-protocol.md) and
[`DF-ROS-2026-A008`](../research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md).

Two things are layered here, and it helps to keep them straight from the
start:

- **The backlog** (`ros add`, `ros work list/ready/show/block/abandon`) is a
  cheap, repository-local place to capture and triage work that doesn't have
  an ID yet. It owns nothing about execution.
- **The tracked protocol** (`ros work begin/block/resume/complete`) is the
  existing, authoritative record of work actually in progress, with
  attribution and evidence requirements. `ros work start` is the bridge
  between the two.

All commands below assume you're at the repository root and `./ros` is
executable (`chmod +x ros` if not, or run `node ros ...`).

---

## 1. Capturing work

### The minimum

```bash
./ros add "Investigate WASM state payload growth"
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
./ros add "Add WASM navigation transition" --tag wasm --tag routing
```

```bash
./ros add "Add WASM navigation transition" -t wasm,routing
```

Mix repeats and comma lists freely — they're merged and de-duplicated:

```bash
./ros add "Investigate state payload growth" -t wasm,state --tag performance
```

### With a priority

```bash
./ros add "Fix flaky retry test" --priority high
```

Valid values: `high`, `medium` (the default if omitted), `low`. Priority
affects nothing about legality — a `low` item that's `ready` is still
`ready`; a `high` item that's `blocked` is still `blocked`. It's a hint for
you or an agent choosing what to pick up next, not a workflow gate.

### With an explicit ID instead of an auto-generated one

Useful when you want the backlog ID to match an external ticket reference:

```bash
./ros add "Migrate legacy config loader" --id CFG-MIGRATE-001
```

This fails loudly if the ID is already used, either in the backlog or by an
in-flight tracked item:

```bash
./ros add "Duplicate" --id CFG-MIGRATE-001
# ERROR work item 'CFG-MIGRATE-001' already exists
```

### Recording where the work came from

Optional provenance, useful when tooling (not a human) files the item:

```bash
./ros add "Address flaky CI failure" \
  --source test-failure \
  --source-reference tests/navigation.spec.ts
```

```bash
./ros add "Follow up on architecture ambiguity" \
  --source agent-discovery \
  --source-reference execution-2026-08-19-0042 \
  --actor agent:reviewer
```

`--actor` records who captured it (defaults to the `ROS_ACTOR` environment
variable, then `"unknown"`).

### With a description

A longer explanation than the title, stored directly on the item (separate
from the optional detail file covered in [§2](#2-browsing-the-backlog)):

```bash
./ros add "Investigate WASM state payload growth" \
  --description "Payload grows superlinearly with history depth; root cause unknown."
```

### With one or more files attached

Attach local files at capture time. Each `--file` is repeatable; a bare path
uses its own filename as the associated name, or give it a different one
with `PATH=NAME`:

```bash
./ros add "Review architecture sketch" --file ./notes/sketch.png
```

```bash
./ros add "Review architecture sketch" \
  --file ./notes/sketch.png=diagram.png \
  --file ./notes/context.md=background.md
```

Two files can be attached under the *same* associated name — they stay
distinct on disk (`ros work show` and the web UI list both).

### All flags at once

```bash
./ros add "Rename WasmStateStore" \
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
./ros work
```

This is shorthand for `ros work list` with no filters — captured items,
ready items, blocked items, and anything already in-flight or completed, all
in one list. There is one queue; you never have to check multiple places.

### By tag

```bash
./ros work list --tag wasm
```

Multiple `--tag`/`-t` flags narrow further (an item must have **all** of
them):

```bash
./ros work list --tag wasm --tag routing
```

### By status

```bash
./ros work list --status captured
```

```bash
./ros work list --status blocked
```

Status values you'll see: `captured`, `ready`, `blocked`, `abandoned` for
backlog-only items, and `active`, `complete` once an item has been started
(see [§5](#5-starting-work)).

### Combined

```bash
./ros work list --tag wasm --status ready
```

### Just what's ready to pick up

```bash
./ros work ready
```

Equivalent to `ros work list --status ready`, with tag filtering supported
the same way:

```bash
./ros work ready --tag wasm
```

This is the query an agent should run instead of guessing from prose — see
[`AGENTS.md`](../AGENTS.md).

### One item, in full

```bash
./ros work show WI-0001
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

./ros work show WI-0001
```

---

## 3. Moving items through the backlog lifecycle

The legal transitions are: `captured -> ready`, `captured -> abandoned`,
`ready -> blocked`, `ready -> abandoned`, `blocked -> ready`,
`blocked -> abandoned`. Anything else is rejected with a clear error.

### Mark ready

```bash
./ros work ready WI-0001
```

Note the dual meaning of `ready`: **no ID** queries ("what's ready");
**with an ID** it mutates ("make this ready"). Same verb, disambiguated by
whether you gave it something to act on.

### Block something before it's even started

```bash
./ros work block WI-0001 --reason "waiting on benchmark results"
```

`--reason` is required — an unexplained blocked item isn't useful to anyone
picking up work later.

### Unblock it

```bash
./ros work ready WI-0001
```

Same command as marking something ready the first time; a blocked item
returning to `ready` is not treated as a special case.

### Block or unblock several at once

```bash
./ros add "First thing" --id WI-A
./ros add "Second thing" --id WI-B
./ros work ready WI-A
./ros work ready WI-B
./ros work block WI-A WI-B --reason "waiting on the same upstream fix"
```

`block` also accepts a mix of backlog IDs and already-started (tracked) IDs
in the same call — each is routed to the right place automatically:

```bash
./ros work start WI-B --type feature
./ros work block WI-A WI-B --reason "upstream outage"
```

Here `WI-A` (still just captured/ready) gets a backlog block; `WI-B`
(already started) gets the existing in-flight block. One command, two
different stores under the hood, correctly dispatched.

### Abandon something you've decided not to do

```bash
./ros work abandon WI-0002 --reason "superseded by WI-0001"
```

Abandonment is **terminal** — there's no `abandoned -> ready` transition.
If you change your mind, capture it again:

```bash
./ros add "Rename WasmStateStore" --tag cleanup
```

---

## 4. Editing an item and attaching files

Description, title, tags, and priority can all be changed later — on a
backlog item or one that's already started, it doesn't matter:

```bash
./ros work update WI-0001 \
  --title "Investigate WASM payload growth (root cause)" \
  --description "Narrowed to the serialization layer." \
  --tag wasm,perf \
  --priority high
```

Only the flags you pass are changed; omit `--tag` entirely to leave tags
untouched (passing `--tag` with an empty value clears them).

### Attaching files after the fact

Same `--file PATH[=NAME]` syntax as `add`, repeatable, works on any known
ID — including one that was `ros work begin`'d directly and never went
through `ros add`:

```bash
./ros work attach WI-0001 --file ./notes/benchmark-results.csv
```

```bash
./ros work attach WI-0001 \
  --file ./notes/before.png=before.png \
  --file ./notes/after.png=after.png
```

Attached files live under `.ros/work/attachments/<ID>/`; `ros work show ID`
lists each one's associated name and size.

---

## 5. Starting work

Once an item is `ready`, hand it to the tracked, attributed protocol:

```bash
./ros work start WI-0001 --type feature
```

This requires `ready` — starting a merely `captured` item fails with a
pointer to the missing step:

```bash
./ros work start WI-0003
# ERROR cannot start backlog item 'WI-0003' from 'captured'; mark it ready first
```

And starting an abandoned item fails permanently:

```bash
./ros work start WI-0002
# ERROR cannot start backlog item 'WI-0002': it was abandoned
```

`--type` determines which completion evidence will be required later
(configured per-type in `ros.json`'s `workProtocol.completionEvidence`).
Common types: `feature`, `bug`, `research`, `mechanical`, `maintenance`,
`infrastructure`.

`start` is a thin wrapper around the existing `ros work begin` — from this
point forward, `ros work show WI-0001` reflects live execution state
(`active`, then `blocked`/`complete`), not the backlog's own status field.
You never have to reconcile the two by hand.

### Checking what you're allowed to do next

```bash
./ros work context WI-0001
```

Reports current state, legal next actions, and exactly which evidence types
completion will require for this item's `--type`.

---

## 6. Finishing work

### With evidence

```bash
./ros work done WI-0001 \
  --evidence implementation=src/wasm/state-store.ts \
  --evidence tests=tests/wasm/state-store.test.ts
```

`done` is a plain alias for the existing `complete` — same rules, same
evidence-path-must-exist check, same event recorded. Use whichever name
reads better to you; they're interchangeable.

```bash
./ros work complete WI-0001 \
  --evidence implementation=src/wasm/state-store.ts \
  --evidence tests=tests/wasm/state-store.test.ts
```

### Missing required evidence is rejected

```bash
./ros work done WI-0001
# ERROR completion evidence missing for 'WI-0001': implementation, tests
```

### Research work with a conclusion

```bash
./ros work start WI-0004 --type research
./ros work done WI-0004 \
  --conclusion inconclusive \
  --evidence research-record=research/findings/wasm-payload-growth.md
```

`--conclusion` accepts `inconclusive` explicitly — research completing
without a supported hypothesis is still a legitimate, recorded outcome.

### Mechanical/no-evidence work

Some work types (configured with an empty evidence list, e.g.
`mechanical`) complete without `--evidence` at all:

```bash
./ros add "Reformat generated config" --id FMT-001
./ros work ready FMT-001
./ros work start FMT-001 --type mechanical
./ros work done FMT-001
```

---

## 7. Checking your work

Run after any batch of changes:

```bash
./ros validate
```

```bash
./ros validate --json
```

The `--json` form gives a stable structured result (`valid`, `findings[]`,
each with a `repair` hint) — useful for scripting or agent consumption.

If canonical Markdown records (decisions, evidence, etc. — not backlog
items, which have no registry) changed:

```bash
./ros registry build
```

```bash
./ros registry build --dry-run
```

A one-shot combined view of state + validation:

```bash
./ros status
```

---

## 8. A complete walkthrough

```bash
# Capture three things as you notice them
./ros add "Add WASM navigation transition" -t wasm,routing --priority high
./ros add "Investigate state payload growth" -t wasm,state
./ros add "Clean up dead JS state handler" -t cleanup --priority low

# See what you've got
./ros work

# Triage: two are worth doing now, one isn't
./ros work ready WI-0001
./ros work ready WI-0002
./ros work abandon WI-0003 --reason "handler was already removed upstream"

# Pick up the higher-priority one
./ros work start WI-0001 --type feature

# ...implement it...
mkdir -p src/wasm tests/wasm
echo "export const navigate = () => {};" > src/wasm/nav.ts
echo "// covers navigate()" > tests/wasm/nav.test.ts

./ros work done WI-0001 \
  --evidence implementation=src/wasm/nav.ts \
  --evidence tests=tests/wasm/nav.test.ts

# Confirm the repository is still consistent
./ros validate
```

Final state, at a glance:

```bash
./ros work
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

Prefer a UI to typing commands? `npm run web` starts a local web interface
over this same backlog — see [`web-interface.md`](web-interface.md).

---

## 9. Where the data actually lives

| File | Role |
|---|---|
| `.ros/work/queue.json` | Canonical backlog storage. Don't hand-edit unless you know what you're doing. |
| `.ros/work/queue.md` | Generated, human-readable projection of the queue. Regenerated automatically. |
| `.ros/work/items/<ID>.md` | Optional, manually-authored detail for one item. |
| `.ros/work/attachments/<ID>/` | Files attached via `ros add --file` / `ros work attach`. |
| `.ros/context/current.json` | Canonical in-flight execution state once `start`/`begin` has run. |
| `.ros/events/events.jsonl` | Immutable event log (started, blocked, resumed, completed) for attribution. |

`.ros/work/**` is excluded from meaningful-change attribution enforcement —
capturing and triaging backlog items is bookkeeping, not application change,
so it never blocks `ros validate` on its own.

---

## 10. Quick reference

| Command | What it does |
|---|---|
| `ros add "title" [--tag a,b] [--priority p] [--id ID] [--description D] [--file PATH[=NAME]]...` | Capture a new backlog item |
| `ros work` / `ros work list [--tag T] [--status S]` | List the unified queue |
| `ros work ready [--tag T]` | Query: items with no blocker |
| `ros work ready ID` | Mutate: captured/blocked → ready |
| `ros work show ID` | Full detail for one item, including any detail file and attachments |
| `ros work update ID [--title T] [--description D] [--tag a,b] [--priority p]` | Change descriptive metadata, any time |
| `ros work attach ID --file PATH[=NAME]...` | Attach one or more files to an existing item |
| `ros work start ID [--type T]` | Promote a ready item into tracked execution |
| `ros work block ID... --reason "..."` | Block a backlog or in-flight item (auto-dispatched) |
| `ros work abandon ID --reason "..."` | Terminal: drop a backlog item |
| `ros work done ID [--evidence T=path]...` | Complete tracked work (alias for `complete`) |
| `ros work context ID` | Legal next actions + required evidence for one item |
| `ros validate` / `ros validate --json` | Check repository consistency |
| `ros status` | Combined state + validation summary |
