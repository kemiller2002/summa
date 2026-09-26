#!/usr/bin/env node
// Summa-owned, identity-aware hub operations (DF-SUMMA-PROV-2026-0001,
// docs/requirements/SUMMA-PROVENANCE.md INV-PROV-005..007).
//
// tools/ros_hub_cli.mjs and tools/ros_hub_server.mjs are ROS tool-owned
// artifacts (.echelon/ros.json): editing them fails `./ros verify` and an
// upgrade would replace them. This user-owned module therefore adds the
// identity behaviour around them instead of forking them:
// - the requester's identity is passed to the spoke explicitly (ROS_ACTOR_KIND,
//   ROS_ACTOR, ROS_TELEMETRY_*, ROS_EXECUTION_ID) with the hub's own inherited
//   identity variables removed, plus the legacy `--actor` for older spokes;
// - the hub records its own actor separately, in .ros/hub/dispatches.jsonl.
// Registry and read-only operations are delegated to the tool-owned hub.
// The decisions are the pure functions in lib/hub-identity.mjs; this file
// only performs the effects (process, filesystem, clock).

import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

import { listRepos, main as rosHubMain } from "./ros_hub_cli.mjs";
import {
  dispatchRecord, legacyActorArguments, parseJsonText, resolveHubActor, resolveRequester, spokeEnvironment
} from "../lib/hub-identity.mjs";

export const dispatchLogPath = (root) => path.join(root, ".ros", "hub", "dispatches.jsonl");

function findRepo(root, id) {
  const repo = listRepos(root).find((candidate) => candidate.id === id);
  if (!repo) throw new Error(`no registered repository with id '${id}'`);
  return repo;
}

// Same invocation as the tool-owned hub (argv array, no shell), with an
// explicit environment.
function runSpoke(repo, args, env) {
  const rosExecutable = path.join(repo.path, "ros");
  if (!fs.existsSync(rosExecutable)) {
    throw new Error(`'${repo.id}' no longer has a './ros' executable at ${repo.path}`);
  }
  try {
    return JSON.parse(execFileSync(rosExecutable, args, { cwd: repo.path, encoding: "utf8", env }));
  } catch (error) {
    const stderr = error.stderr ? String(error.stderr).trim() : "";
    throw new Error(`${repo.id}: ${stderr.replace(/^ERROR /, "") || error.message}`);
  }
}

/**
 * Creates a work item in a registered spoke with explicit identity propagation.
 * input: { title, tags, priority, description, id, files, actor (legacy string),
 *          requesterActor (Praxis actor), requesterExecution (EXE-/EXT-) }
 * context: { hubEnv, invokingEnv, hubActor, operationId, now }
 *   invokingEnv is only the environment of a command-line invocation, whose
 *   process IS the requester; the HTTP server passes none.
 */
export function createWorkWithIdentity(root, repoId, input = {}, context = {}) {
  if (!input.title || !input.title.trim()) throw new Error("create requires a non-empty title");
  const repo = findRepo(root, repoId);
  const resolved = resolveRequester({
    declared: { actor: input.requesterActor, execution: input.requesterExecution, legacyActor: input.actor },
    invokingEnv: context.invokingEnv
  });
  if (!resolved.ok) throw new Error(resolved.error);
  const hub = resolveHubActor(context.hubActor);
  if (!hub.ok) throw new Error(hub.error);
  const { requester } = resolved;
  const env = spokeEnvironment(context.hubEnv ?? process.env, requester);

  const args = [
    "add", input.title,
    ...(input.tags ?? []).flatMap((tag) => ["--tag", tag]),
    ...(input.priority ? ["--priority", input.priority] : []),
    ...(input.description ? ["--description", input.description] : []),
    ...(input.id ? ["--id", input.id] : []),
    ...legacyActorArguments(requester, input.actor)
  ];
  const item = runSpoke(repo, args, env);

  const dispatched = dispatchRecord({
    operationId: context.operationId ?? `hub-${Date.now()}-${randomBytes(4).toString("hex")}`,
    at: (context.now ?? new Date()).toISOString(),
    repoId: repo.id,
    command: "add",
    hubActor: hub.actor,
    requester,
    legacyActor: input.actor,
    spokeItemId: item.id
  });
  if (!dispatched.ok) throw new Error(dispatched.error);
  fs.mkdirSync(path.dirname(dispatchLogPath(root)), { recursive: true });
  fs.appendFileSync(dispatchLogPath(root), `${JSON.stringify(dispatched.record)}\n`, "utf8");
  const dispatch = dispatched.record;

  if (input.files?.length) {
    runSpoke(repo, [
      "work", "attach", "--id", item.id, "--occurred-at", new Date().toISOString(),
      ...input.files.flatMap(({ sourcePath, name }) => ["--file", name ? `${sourcePath}=${name}` : sourcePath])
    ], env);
    const shown = runSpoke(repo, ["work", "show", item.id], env);
    return { ...item, ...shown, repoId: repo.id, repoName: repo.name, dispatch };
  }
  return { ...item, repoId: repo.id, repoName: repo.name, dispatch };
}

function option(args, name) {
  const index = args.indexOf(name);
  if (index < 0) return undefined;
  if (!args[index + 1] || args[index + 1].startsWith("--")) throw new Error(`${name} requires a value`);
  return args[index + 1];
}

// Identity JSON arrives as text: classified as text (contract 1.2 rule 1), so a
// repeated member name or an unpaired surrogate is refused, never resolved.
function jsonOption(args, name) {
  const value = option(args, name);
  if (value === undefined) return undefined;
  const parsed = parseJsonText(value, name);
  if (!parsed.ok) throw new Error(parsed.error);
  return parsed.value;
}

function values(args, names) {
  return args.flatMap((arg, i) => (names.includes(arg) && args[i + 1] && !args[i + 1].startsWith("--") ? [args[i + 1]] : []));
}

/**
 * `summa-hub create REPO-ID "Title" [...ros-hub create flags] [--actor-json JSON]
 *  [--execution EXE-...] [--hub-actor-json JSON]`; every other command is the
 * tool-owned ros-hub's.
 */
export function main(argv, env = process.env) {
  const rootIndex = argv.indexOf("--root");
  const root = rootIndex >= 0 ? path.resolve(argv[rootIndex + 1] ?? ".") : process.cwd();
  const args = rootIndex >= 0 ? [...argv.slice(0, rootIndex), ...argv.slice(rootIndex + 2)] : [...argv];
  if (args[0] !== "create") return rosHubMain(argv);
  try {
    const [, repoId, title] = args;
    if (!repoId || !title || title.startsWith("--")) throw new Error('create requires a repository ID and title, e.g. summa-hub create REPO-ID "Title"');
    const rest = args.slice(3);
    const item = createWorkWithIdentity(root, repoId, {
      title,
      tags: [...new Set(values(rest, ["--tag", "-t"]).flatMap((value) => value.split(",").map((tag) => tag.trim()).filter(Boolean)))],
      priority: option(rest, "--priority"),
      description: option(rest, "--description"),
      id: option(rest, "--id"),
      actor: option(rest, "--actor"),
      requesterActor: jsonOption(rest, "--actor-json"),
      requesterExecution: option(rest, "--execution"),
      files: values(rest, ["--file"]).map((value) => {
        const separator = value.indexOf("=");
        return separator > 0 ? { sourcePath: value.slice(0, separator), name: value.slice(separator + 1) } : { sourcePath: value, name: undefined };
      })
    }, { invokingEnv: env, hubEnv: env, hubActor: jsonOption(rest, "--hub-actor-json") });
    console.log(JSON.stringify(item, null, 2));
    return 0;
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
