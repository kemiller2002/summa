---
id: DF-SUMMA-ROSCLI-2026-0001
title: CI runs tools/ros_cli.mjs instead of the canonical ./ros F# launcher
status: draft
version: 1.0.0
created: 2026-09-14
updated: 2026-09-14
owners:
  - repository-governance
review_cycle: on-resolution
supersedes: []
superseded_by: []
related_documents:
  - AGENTS.md
  - docs/work-protocol.md
  - .github/workflows/ros-validation.yml
  - ros.json
  - input-documents/summa-requirement-set-0.txt
tags: [governance, deviation, ci, ros, tooling]
---

# DF-SUMMA-ROSCLI-2026-0001 — CI runs `tools/ros_cli.mjs` instead of the canonical `./ros` F# launcher

- **Date:** 2026-09-14
- **Status:** draft (proposed deviation; awaiting owner acceptance)
- **Decision type:** temporary deviation from canonical policy, with migration path

## Summary

`.github/workflows/ros-validation.yml` as scaffolded by `ros-bootstrap init` invoked
`./ros registry check` and `./ros validate`. Both commands fail in this repository
because the `./ros` launcher cannot obtain its binary. The workflow was changed to call
`node tools/ros_cli.mjs` instead, which is the only way CI currently passes.

`AGENTS.md` states that Node is not a CLI and is not a rollback path. This record exists
because that change contradicts canonical governance and must not be made silently.

## What was found

### 1. `./ros` cannot execute in this repository

`ros.json` pins:

```json
"rosVersion": "2.0.1-main.78.1"
```

`./ros` is a launcher (`tools/ros_fs_launcher.mjs`) that downloads a self-contained F#
binary matching that pinned version from the upstream project's GitHub Releases, verifies
it against `checksums.txt`, caches it, then execs it. Observed failure:

```
$ ./ros registry check
ros binary for linux-x64 not cached; downloading from
https://github.com/kemiller2002/repository-operating-system/releases/download/v2.0.1-main.78.1/ros-fs-linux-x64
./ros: failed to obtain the linux-x64 binary for version 2.0.1-main.78.1:
request to .../v2.0.1-main.78.1/checksums.txt failed: 404 Not Found
EXIT: 1
```

The release assets for the pinned prerelease tag do not exist. Asset probes:

| Tag | `checksums.txt` | `ros-fs-linux-x64` |
|---|---|---|
| `v2.0.1-main.78.1` (pinned) | **404** | **404** |
| `v2.0.1` (stable) | 302 | 302 |

The stable tag publishes assets; the pinned prerelease does not. This is an upstream
packaging gap, not a misconfiguration of this repository.

### 2. The scaffolded CI workflow depends on `./ros`

As installed, both workflow steps invoked the launcher:

```yaml
      - run: ./ros registry check
      - run: ./ros validate
```

Both would therefore have failed on every run, for the reason in (1).

### 3. Canonical governance forbids treating Node as the CLI

`AGENTS.md` (`GV-START-001` v1.3.0), section **F# CLI**:

> `./ros` in this source checkout, and every project bootstrapped via `npx ros-bootstrap
> init` (both profiles), runs the F# CLI (`DF-ROS-2026-A030`, `DF-ROS-2026-A032`). **Node
> is no longer a CLI anywhere in this project or what it scaffolds.** Node's own
> implementation (`tools/ros_cli.mjs` and its companions) remains in this repository and in
> the `project-administration` starter profile only, as `tools/ros_server.mjs`'s /
> `ros_hub_cli.mjs`'s in-process internal library dependency (`DF-ROS-2026-A033`) — **it is
> no longer characterized or scaffolded as a CLI rollback path**.

The `./ros` launcher's own header is the source that names `tools/ros_cli.mjs` as still
usable, and it is weaker than `AGENTS.md`:

> Node's own CLI implementation remains available at tools/ros_cli.mjs if you ever need it.

`AGENTS.md` is canonical governance and supersedes a comment in a scaffolded file.

### 4. Requirement Set 0 forbids deviation

`input-documents/summa-requirement-set-0.txt` §0.1 makes ROS, SDE, and the TypeScript WASM
kernel "mandatory implementation constraints, not optional guidance," and §0.1.1 ("No
Deviation") requires the implementation agent to "follow ROS and SDE absolutely and without
deviation." A non-canonical CLI path in CI is within the scope of that constraint.

## Decision

Pending owner acceptance, CI calls the Node implementation directly:

```yaml
      - run: node tools/ros_cli.mjs registry check
      - run: node tools/ros_cli.mjs validate
```

This was chosen over the alternatives considered:

| Option | Rejected because |
|---|---|
| Leave CI calling `./ros` | Every run fails; the repository has no green baseline and no enforcement of work attribution. |
| Re-bootstrap from `latest` (2.0.1) | `--profile project-administration` does not exist in 2.0.1 (`greenfield` only). Loses the required profile. |
| Repoint `ros.json` to `2.0.1` | The scaffold is a main build; running a stable binary against main-build state is unverified and risks silent divergence. |
| Publish the missing release assets | Correct fix, but requires a change in `kemiller2002/repository-operating-system`, which is outside this repository. |

## Consequences

1. **CI enforcement is preserved.** `registry check` and `validate` run on every push and
   pull request, including work-item attribution enforcement
   (`workProtocol.enforceAttribution: true`).
2. **CI does not exercise the canonical binary.** Any behavioural divergence between the F#
   CLI and `tools/ros_cli.mjs` is invisible to CI while this deviation stands. Validation
   results are only as trustworthy as that equivalence.
3. **Managed-file drift.** `.github/workflows/ros-validation.yml` is a ROS-managed file, so
   `ros-bootstrap verify` reports exactly one finding for as long as the patch is in place:

   ```
   ERROR .github/workflows/ros-validation.yml: differs from installed 2.0.1-main.78.1 snapshot
   verification failed with 1 finding(s)
   ```

   This is expected and is resolved by the migration below, not by suppressing the check.
4. **Command-surface differences are load-bearing.** `AGENTS.md` documents that the F# CLI
   requires explicit `--id ID` and `--occurred-at TIMESTAMP` on mutating commands, whereas
   the Node implementation takes a positional ID and reads the clock itself. Work items
   recorded through the Node path therefore do not exercise the canonical timestamp
   discipline that `AGENTS.md` warns about.
5. **This record was itself produced under the deviation.** `./ros` is unavailable, so the
   work item accompanying this decision was created with `node tools/ros_cli.mjs`.

## Migration path

1. Publish release assets (`checksums.txt` and the `ros-fs-*` binaries) for tag
   `v2.0.1-main.78.1` in `kemiller2002/repository-operating-system`, matching what
   `v2.0.1` already publishes. Alternatively, pin `ros.json` to a version that has assets
   and is built from an equivalent source revision.
2. Confirm `./ros registry check` and `./ros validate` succeed locally.
3. Revert `.github/workflows/ros-validation.yml` to the scaffolded `./ros` invocations.
4. Confirm `ros-bootstrap verify` reports zero findings, clearing consequence (3).
5. Set this record's status to `superseded` or `withdrawn` and record the resolution.

Until step 1 completes, this deviation cannot be closed from within this repository.

## Verification performed

Commands run, with results, at the time of writing:

| Command | Result |
|---|---|
| `./ros registry check` | exit 1 — 404 obtaining launcher binary |
| `node tools/ros_cli.mjs registry check` | `registries are current` (exit 0) |
| `ROS_BASE_REF=<base> node tools/ros_cli.mjs validate` | `validation passed` (exit 0) |
| `ros-bootstrap verify` | exit 1 — 1 drift finding on the workflow file |
| GitHub Actions `ROS validation` on `main` | success |

No check is claimed here that was not run. The `./ros` path was not made to pass; it
remains broken and is the subject of the migration above.

## Open questions

- Is `tools/ros_cli.mjs` behaviourally equivalent to the F# CLI for `registry check` and
  `validate` at this version? Unverified — the canonical binary cannot be obtained to
  compare against.
- Was the absence of assets on `v2.0.1-main.78.1` intentional (prerelease not meant for
  consumption) or an oversight in the upstream release workflow? If intentional, pinning a
  main build in a bootstrapped project may itself be the defect.
