# Summa

This repository is a **project-administration hub** running Repository
Operating System 2.0.1-main.78.1. It is a separate, independent ROS
repository — its job is to coordinate work across *other* ROS repositories,
not to hold their code or evidence.

## What this is (and isn't)

- It registers other ROS repositories by their local filesystem path.
- It creates work items in a registered repository by running **that
  repository's own `./ros`** — it never edits another repository's files
  directly.
- It shows a combined, read-only view of work across every registered
  repository.
- It does **not** own or duplicate any other repository's work-item state.
  Each repository remains independently authoritative for its own work,
  exactly as if you'd run its `./ros` commands yourself from its own
  directory. See [`docs/project-administration-hub.md`](docs/project-administration-hub.md).

This hub also has its own local work backlog (`./ros add`, `./ros work ...`)
for tracking the hub's own administrative work — see
[`docs/work-backlog-guide.md`](docs/work-backlog-guide.md).

## Getting started

```bash
npm install
npm run hub
```

Opens `http://127.0.0.1:4320`. Register a repository, then create or browse
work items across everything you've registered.

The hub's own local backlog UI runs separately:

```bash
npm run web
```

Or from the command line:

```bash
./ros-hub register /path/to/some/other/ros-repo --name "Some Repo"
./ros-hub repos
./ros-hub create SOME-REPO-ID "Fix the thing" --tag bug --priority high
./ros-hub work
```

## Local operating commands (for this repository's own work)

```bash
./ros add "Describe the work"
./ros work
./ros status
./ros registry check
./ros validate
```

The installed snapshot is self-contained; it does not read from the source
ROS repository. `.ros/installation.json` records the package version and
checksums of installed files.
