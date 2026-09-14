# Project Administration Hub

A repository-local backlog (`docs/work-backlog-guide.md`) makes one
repository's own work cheap to capture. The hub extends that across
*multiple* repositories: it registers other ROS repositories by filesystem
path, lets you create a work item in any of them, and shows a combined view
of what's outstanding across all of them.

This lives in a separate, installable ROS profile
(`ros-bootstrap init --profile project-administration`) — not inside a
plain ROS repository — for the same reason described in
[`DF-ROS-2026-A008`](../research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md):
a single repository's work protocol must stay independently usable, and
cross-repository coordination is a distinct concern from repository
execution. The README's ownership table assigns "repository registration,
portfolio data" to a central reporting repository, not to ROS core; this
hub *is* that repository, built from ROS's own reusable tooling rather than
by hand.

## The one rule that keeps this safe

**The hub never touches a registered repository's files directly.** Every
operation on a registered repository shells out to that repository's own
`./ros`, exactly the command a human would type from that repository's own
directory. Creating a work item through the hub and creating one by hand in
that repository are indistinguishable afterward — same ID sequence, same
queue.json, same validation rules, same evidence requirements. The hub adds
no second source of truth for any repository's work.

One consequence: a registered repository must have `./ros` and the backlog
commands (`ros add`, `ros work ...`) available to actually receive work
created through the hub. A repository bootstrapped before the backlog layer
existed needs its ROS installation upgraded first (see
`PACKAGE-USAGE.md`'s upgrade procedure in that repository) — the hub
reports this as a clear per-repository error rather than failing silently
or guessing.

## The registry

Canonical storage is `.ros/hub/registry.json`:

```json
{
  "schemaVersion": "1.0.0",
  "repos": [
    { "id": "repository-operating-system", "name": "ROS Core", "path": "/Users/you/dev/Repository Operating System", "registeredAt": "..." }
  ]
}
```

`.ros/hub/registry.md` is a generated, human-readable projection, same
pattern as the backlog's `queue.md`. `id` is read from the target
repository's own `ros.json` (`repository.id`) at registration time and
never changed afterward — it's the same ID that repository's own `./ros`
already uses, so a row in the hub's aggregated view and `./ros work show`
run directly in that repository always agree.

Registration is filesystem-path based and therefore single-machine: it only
works for repositories checked out locally where the hub runs. Coordinating
repositories across machines is a different, larger problem (authenticated
network transport, the kind the
[external work adapter contract](work-adapter-contract.md) is for) and is
explicitly out of scope here.

## CLI

```bash
./ros-hub register /path/to/some/repo --name "Display Name"
./ros-hub repos
./ros-hub unregister REPO-ID
./ros-hub create REPO-ID "Title" --tag a,b --priority high --description "..." --file PATH[=NAME]
./ros-hub work                       # aggregated, every registered repo
./ros-hub work --repo REPO-ID --status ready
```

`create`'s flags map directly onto the target repository's own `ros add` —
tags, priority, description, and one or more `--file PATH[=NAME]` all work
exactly as documented in `work-backlog-guide.md`, because the hub builds
and runs that exact command.

## Web interface

```bash
npm run hub
```

Serves `http://127.0.0.1:4320` — a register form, a repo list (with
unregister), a create-work-item form (with tag/priority/description/file
inputs, same as the per-repo web UI), a filter bar, and the aggregated
table. **No authentication, localhost by default** — this server can create
work items and run commands in every registered repository, which is a
larger blast radius than the single-repo web interface. Do not bind it to
a non-loopback host without your own authentication in front of it.

### API

| Method | Path | Effect |
|---|---|---|
| `GET` | `/api/repos` | List registered repositories |
| `POST` | `/api/repos` `{path, name?}` | Register a repository |
| `DELETE` | `/api/repos/:id` | Unregister (does not touch the repository itself) |
| `GET` | `/api/work?repo=&tag=&status=` | Aggregated work, one repo or all |
| `POST` | `/api/repos/:id/work` | Create a work item; JSON body, or `multipart/form-data` with `file` parts for attachments |

Aggregation is best-effort per repository: a registered repository whose
path has moved, or whose ROS installation is too old to support a command,
surfaces as a single error entry for that repository rather than failing
the whole view.

## What this deliberately does not do

- No authentication, no multi-user access control, no audit log beyond
  what each spoke repository's own event log already records.
- No portfolio database, no reporting/analytics beyond the raw aggregated
  table — building those is exactly the kind of "central project-management
  framework" ROS's own architecture asks to be introduced only when a
  concrete need demonstrates it, not preemptively.
- No cross-machine repository access. Everything here assumes local paths.
