#!/usr/bin/env node
// The project-administration hub's kernel: a registry of other ROS
// repositories by filesystem path, plus operations that shell out to each
// registered repository's own `./ros` -- never anything that reads or
// writes a spoke repository's files directly. Each spoke repository stays
// independently authoritative for its own work; the hub only ever invokes
// the same command line a human would type in that repository.

import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

function readJson(file, fallback = null) {
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : fallback;
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function registryPath(root) { return path.join(root, ".ros", "hub", "registry.json"); }
function registryMarkdownPath(root) { return path.join(root, ".ros", "hub", "registry.md"); }

export function loadRegistry(root) {
  return readJson(registryPath(root), { schemaVersion: "1.0.0", repos: [] });
}

function renderRegistryMarkdown(repos) {
  const header = "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n";
  const body = repos.map((repo) => `| ${repo.id} | ${repo.name} | ${repo.path} |`).join("\n");
  return `${header}${body}${body ? "\n" : ""}`;
}

function saveRegistry(root, registry) {
  writeJson(registryPath(root), registry);
  fs.writeFileSync(registryMarkdownPath(root), renderRegistryMarkdown(registry.repos), "utf8");
}

// A repository is only ever addressed by its own repository.id (from its
// ros.json) or the filesystem path used to register it -- never re-derived
// or renamed by the hub, so `ros work show ID` in the spoke repo and the
// hub's own listings always agree.
function readSpokeRepositoryId(repoPath) {
  const config = readJson(path.join(repoPath, "ros.json"), {});
  return config.repository?.id ?? config.name ?? path.basename(repoPath);
}

export function registerRepo(root, repoPath, options = {}) {
  const resolved = path.resolve(repoPath);
  if (!fs.existsSync(resolved) || !fs.statSync(resolved).isDirectory()) {
    throw new Error(`not a directory: ${resolved}`);
  }
  if (!fs.existsSync(path.join(resolved, "ros.json"))) {
    throw new Error(`not a ROS repository (no ros.json found): ${resolved}`);
  }
  if (!fs.existsSync(path.join(resolved, "ros"))) {
    throw new Error(`no './ros' executable found in: ${resolved}`);
  }
  const registry = loadRegistry(root);
  if (registry.repos.some((repo) => repo.path === resolved)) {
    throw new Error(`already registered: ${resolved}`);
  }
  const id = readSpokeRepositoryId(resolved);
  if (registry.repos.some((repo) => repo.id === id)) {
    throw new Error(`a repository with id '${id}' is already registered`);
  }
  const entry = {
    id,
    name: options.name?.trim() || id,
    path: resolved,
    registeredAt: new Date().toISOString()
  };
  registry.repos.push(entry);
  saveRegistry(root, registry);
  return entry;
}

export function unregisterRepo(root, id) {
  const registry = loadRegistry(root);
  const index = registry.repos.findIndex((repo) => repo.id === id);
  if (index === -1) throw new Error(`no registered repository with id '${id}'`);
  const [removed] = registry.repos.splice(index, 1);
  saveRegistry(root, registry);
  return removed;
}

export function listRepos(root) {
  return loadRegistry(root).repos;
}

function findRepo(root, id) {
  const repo = loadRegistry(root).repos.find((candidate) => candidate.id === id);
  if (!repo) throw new Error(`no registered repository with id '${id}'`);
  return repo;
}

// The only privileged operation here: run the spoke's own CLI, exactly as a
// human would from its own directory. execFile with an argv array never
// invokes a shell, so nothing in a work item's title/description/tags can
// be interpreted as shell syntax.
function runRepoCli(repo, args) {
  if (!fs.existsSync(repo.path)) {
    throw new Error(`registered path for '${repo.id}' no longer exists: ${repo.path}`);
  }
  const rosExecutable = path.join(repo.path, "ros");
  if (!fs.existsSync(rosExecutable)) {
    throw new Error(`'${repo.id}' no longer has a './ros' executable at ${repo.path}`);
  }
  try {
    const output = execFileSync(rosExecutable, args, { cwd: repo.path, encoding: "utf8" });
    return JSON.parse(output);
  } catch (error) {
    const stderr = error.stderr ? String(error.stderr).trim() : "";
    const message = stderr.replace(/^ERROR /, "") || error.message;
    throw new Error(`${repo.id}: ${message}`);
  }
}

function tagArgs(tags) {
  return (tags ?? []).flatMap((tag) => ["--tag", tag]);
}

function fileArgs(files) {
  return (files ?? []).flatMap(({ sourcePath, name }) => ["--file", name ? `${sourcePath}=${name}` : sourcePath]);
}

export function createWorkInRepo(root, repoId, input = {}) {
  if (!input.title || !input.title.trim()) throw new Error("create requires a non-empty title");
  const repo = findRepo(root, repoId);
  // Files are attached via a separate `work attach` call rather than inline
  // on `add`: an F#-backed spoke's `add` deliberately does not accept
  // `--file` (its own equivalent effect is `work attach`), so this is the
  // one call shape that lands attachments the same way on either backend.
  const args = ["add", input.title, ...tagArgs(input.tags)];
  if (input.priority) args.push("--priority", input.priority);
  if (input.description) args.push("--description", input.description);
  if (input.id) args.push("--id", input.id);
  if (input.actor) args.push("--actor", input.actor);
  const item = runRepoCli(repo, args);
  if (input.files?.length) {
    runRepoCli(repo, [
      "work", "attach",
      "--id", item.id,
      "--occurred-at", new Date().toISOString(),
      ...fileArgs(input.files)
    ]);
    // `work attach`'s own stdout is a queue-row projection without
    // attachments; `work show` returns the full item that has them.
    const shown = runRepoCli(repo, ["work", "show", item.id]);
    return { ...item, ...shown, repoId: repo.id, repoName: repo.name };
  }
  return { ...item, repoId: repo.id, repoName: repo.name };
}

export function listWorkInRepo(root, repoId, { tags, status } = {}) {
  const repo = findRepo(root, repoId);
  const args = ["work", "list", ...tagArgs(tags)];
  if (status) args.push("--status", status);
  try {
    const rows = runRepoCli(repo, args);
    return rows.map((row) => ({ ...row, repoId: repo.id, repoName: repo.name }));
  } catch (error) {
    return [{ repoId: repo.id, repoName: repo.name, error: error.message }];
  }
}

// Aggregation is best-effort per repository: one unreachable or outdated
// spoke (moved path, stale ROS install missing a command) surfaces as a
// single error row for that repo rather than failing the whole view.
export function listWorkAcrossRepos(root, { repoId, tags, status } = {}) {
  const repos = repoId ? [findRepo(root, repoId)] : listRepos(root);
  return repos.flatMap((repo) => listWorkInRepo(root, repo.id, { tags, status }));
}

function tagOptions(args) {
  const tags = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i] === "--tag" || args[i] === "-t") {
      const value = args[i + 1];
      if (!value || value.startsWith("--")) throw new Error(`${args[i]} requires a value`);
      tags.push(...value.split(",").map((tag) => tag.trim()).filter(Boolean));
      i += 1;
    }
  }
  return [...new Set(tags)];
}

function fileOptions(args) {
  const files = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i] === "--file") {
      const value = args[i + 1];
      if (!value || value.startsWith("--")) throw new Error("--file requires PATH or PATH=NAME");
      const separator = value.indexOf("=");
      files.push(separator > 0
        ? { sourcePath: value.slice(0, separator), name: value.slice(separator + 1) }
        : { sourcePath: value, name: undefined });
      i += 1;
    }
  }
  return files;
}

function option(args, name) {
  const index = args.indexOf(name);
  if (index < 0) return undefined;
  if (!args[index + 1] || args[index + 1].startsWith("--")) throw new Error(`${name} requires a value`);
  return args[index + 1];
}

function parseCli(argv) {
  let root = process.cwd();
  const args = [...argv];
  const rootIndex = args.indexOf("--root");
  if (rootIndex >= 0) {
    if (!args[rootIndex + 1]) throw new Error("--root requires a value");
    root = path.resolve(args[rootIndex + 1]);
    args.splice(rootIndex, 2);
  }
  return { root: path.resolve(root), args };
}

export function main(argv) {
  try {
    const { root, args } = parseCli(argv);
    if (args[0] === "register") {
      const repoPath = args[1];
      if (!repoPath || repoPath.startsWith("--")) throw new Error("register requires a path");
      const entry = registerRepo(root, repoPath, { name: option(args, "--name") });
      console.log(JSON.stringify(entry, null, 2)); return 0;
    }
    if (args[0] === "unregister") {
      const id = args[1];
      if (!id) throw new Error("unregister requires a repository ID");
      console.log(JSON.stringify(unregisterRepo(root, id), null, 2)); return 0;
    }
    if (args[0] === "repos") {
      console.log(JSON.stringify(listRepos(root), null, 2)); return 0;
    }
    if (args[0] === "create") {
      const repoId = args[1];
      const title = args[2];
      if (!repoId || !title || title.startsWith("--")) throw new Error('create requires a repository ID and title, e.g. ros-hub create REPO-ID "Title"');
      const item = createWorkInRepo(root, repoId, {
        title,
        tags: tagOptions(args.slice(3)),
        priority: option(args, "--priority"),
        description: option(args, "--description"),
        id: option(args, "--id"),
        actor: option(args, "--actor"),
        files: fileOptions(args.slice(3))
      });
      console.log(JSON.stringify(item, null, 2)); return 0;
    }
    if (args[0] === "work") {
      const rest = args.slice(1);
      const repoId = option(rest, "--repo");
      const status = option(rest, "--status");
      const tags = tagOptions(rest);
      console.log(JSON.stringify(listWorkAcrossRepos(root, { repoId, tags: tags.length ? tags : undefined, status }), null, 2)); return 0;
    }
    console.error("Usage: ros-hub [--root PATH] register PATH [--name NAME] | unregister ID | repos | create REPO-ID \"title\" [--tag T] [--priority P] [--description D] [--file PATH[=NAME]] | work [--repo ID] [--tag T] [--status S]");
    return 2;
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
