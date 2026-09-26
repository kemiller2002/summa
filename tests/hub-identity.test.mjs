// Hub identity propagation (INV-PROV-005..007, DF-SUMMA-PROV-2026-0001).
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  HUB_ACTOR, IDENTITY_VARIABLES, resolveRequester, resolveHubActor, spokeEnvironment, legacyActorArguments,
  dispatchRecord, actorFromLegacyString, actorFromEnvironment,
} from "../lib/hub-identity.mjs";
import { classify, originator, withRole } from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";
import { registerRepo } from "../tools/ros_hub_cli.mjs";
import { createWorkWithIdentity, dispatchLogPath, main } from "../tools/summa_hub.mjs";

const AGENT = { kind: "agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" };
const HUMAN = { kind: "human", id: "kevin" };
const EXE = "EXE-20260926T080000000Z-a1a1a1a1";
const AT = "2026-09-26T08:00:00.000Z";
// The environment of a hub process that is itself running inside an agent
// session on a CI runner: every identity variable Praxis reads is set.
const HUB_ENV = {
  PATH: process.env.PATH, HOME: "/home/hub",
  ROS_ACTOR_KIND: "agent", ROS_ACTOR: "anthropic/claude-code", ROS_TELEMETRY_PROVIDER: "anthropic",
  ROS_TELEMETRY_MODEL: "hub-model", ROS_TELEMETRY_MODEL_VERSION: "1", ROS_TELEMETRY_RUNTIME: "claude-code",
  ROS_TELEMETRY_RUNTIME_VERSION: "2", ROS_TELEMETRY_SESSION_ID: "hub-session-1", ROS_TELEMETRY_CONVERSATION_ID: "hub-conv",
  ROS_TELEMETRY_RUN_ID: "hub-run", ROS_EXECUTION_ID: "EXE-hub-own-run",
  CLAUDE_CODE_SESSION_ID: "hub-session-1", CODEX_SESSION_ID: "c", CODEX_THREAD_ID: "t", GEMINI_SESSION_ID: "g",
  COPILOT_SESSION_ID: "p", GITHUB_ACTIONS: "true", GITHUB_RUN_ID: "999", OLLAMA_HOST: "127.0.0.1:11434",
};
const identityOnly = (env) => Object.fromEntries(Object.entries(env).filter(([name]) => IDENTITY_VARIABLES.includes(name)));

test("a structured requester is taken verbatim, with its execution", () => {
  const { requester } = resolveRequester({ declared: { actor: AGENT, execution: EXE } });
  assert.deepEqual(requester, { actor: AGENT, execution: EXE, source: "declared" });
});

test("an HTTP request with no declaration is unknown: the server's own environment is never the requester", () => {
  const { requester } = resolveRequester({ declared: {} });
  assert.equal(requester.actor.kind, "unknown");
  assert.equal(requester.source, "unknown");
  const env = spokeEnvironment(HUB_ENV, requester);
  assert.deepEqual(identityOnly(env), { ROS_ACTOR_KIND: "unknown", ROS_ACTOR: "unknown" }, "nothing of the hub's identity, session, runtime, CI run, or model server leaks");
  assert.equal(env.HOME, "/home/hub", "unrelated variables are kept");
  assert.deepEqual(legacyActorArguments(requester, undefined), []);
});

test("a command-line invocation's declared identity is the requester", () => {
  const invokingEnv = { ROS_ACTOR_KIND: "agent", ROS_ACTOR: "openai/codex", ROS_TELEMETRY_PROVIDER: "openai", ROS_TELEMETRY_MODEL: "gpt-5-codex", ROS_TELEMETRY_RUNTIME: "codex", ROS_EXECUTION_ID: EXE };
  const { requester } = resolveRequester({ declared: {}, invokingEnv });
  assert.deepEqual(requester, { actor: AGENT, execution: EXE, source: "environment" });
  assert.equal(actorFromEnvironment({}), undefined, "no declaration is not an identity");
});

test("the requester's identity is passed to the spoke explicitly, replacing the hub's", () => {
  const { requester } = resolveRequester({ declared: { actor: AGENT, execution: EXE } });
  const env = spokeEnvironment(HUB_ENV, requester);
  assert.deepEqual(Object.keys(identityOnly(env)).sort(), ["ROS_ACTOR", "ROS_ACTOR_KIND", "ROS_EXECUTION_ID", "ROS_TELEMETRY_MODEL", "ROS_TELEMETRY_PROVIDER", "ROS_TELEMETRY_RUNTIME"]);
  assert.equal(env.ROS_ACTOR_KIND, "agent");
  assert.equal(env.ROS_ACTOR, "openai/codex");
  assert.equal(env.ROS_TELEMETRY_PROVIDER, "openai");
  assert.equal(env.ROS_TELEMETRY_MODEL, "gpt-5-codex");
  assert.equal(env.ROS_TELEMETRY_RUNTIME, "codex");
  assert.equal(env.ROS_EXECUTION_ID, EXE);
  assert.deepEqual(legacyActorArguments(requester, undefined), ["--actor", "openai/codex"], "older spokes still get the legacy flag");
});

test("a human requester carries no provider/model/runtime and no invented execution", () => {
  const { requester } = resolveRequester({ declared: { actor: HUMAN } });
  const env = spokeEnvironment(HUB_ENV, requester);
  assert.equal(env.ROS_ACTOR_KIND, "human");
  assert.equal(env.ROS_ACTOR, "kevin");
  assert.equal(env.ROS_TELEMETRY_PROVIDER, undefined);
  assert.equal(env.ROS_EXECUTION_ID, undefined);
});

test("unknown non-human attributes are omitted so the spoke records 'unknown' itself", () => {
  const { requester } = resolveRequester({ declared: { actor: { kind: "agent", id: "someone", provider: "unknown", model: "unknown", runtime: "unknown" } } });
  const env = spokeEnvironment(HUB_ENV, requester);
  assert.equal(env.ROS_TELEMETRY_PROVIDER, undefined);
  assert.equal(env.ROS_TELEMETRY_MODEL, undefined);
  assert.equal(env.ROS_TELEMETRY_RUNTIME, undefined);
});

test("the legacy --actor string is forwarded verbatim and parsed without guessing", () => {
  const { requester } = resolveRequester({ declared: { legacyActor: "agent:chatgpt" } });
  assert.deepEqual(requester.actor, { kind: "agent", id: "chatgpt", provider: "unknown", model: "unknown", runtime: "unknown" });
  assert.deepEqual(legacyActorArguments(requester, "agent:chatgpt"), ["--actor", "agent:chatgpt"]);
  assert.deepEqual(actorFromLegacyString("kevin"), { kind: "unknown", id: "kevin", provider: "unknown", model: "unknown", runtime: "unknown" }, "a bare id is not assumed to be human");
});

test("malformed or credential-bearing requesters are rejected", () => {
  assert.equal(resolveRequester({ declared: { actor: { kind: "robot", id: "x" } } }).ok, false);
  assert.equal(resolveRequester({ declared: { actor: AGENT, execution: "run-1" } }).ok, false);
  assert.equal(resolveRequester({ declared: { actor: { kind: "human", id: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } } }).ok, false);
  assert.equal(resolveRequester({ declared: { actor: "agent:x" } }).ok, false);
});

test("the hub's own actor is automation echelon/summa-hub or an explicitly declared human operator, never an agent", () => {
  assert.deepEqual(resolveHubActor(undefined).actor, HUB_ACTOR);
  assert.deepEqual(resolveHubActor(HUMAN).actor, HUMAN);
  assert.equal(resolveHubActor(AGENT).ok, false);
});

test("the dispatch record keeps the hub actor and the requester apart", () => {
  const { requester } = resolveRequester({ declared: { actor: AGENT, execution: EXE } });
  const { record } = dispatchRecord({ operationId: "hub-1", at: AT, repoId: "chrona", command: "add", hubActor: HUB_ACTOR, requester, spokeItemId: "WI-7" });
  assert.deepEqual(record.hubActor, HUB_ACTOR);
  assert.deepEqual(record.requester.actor, AGENT);
  assert.equal(classify(record.provenance).verdict, "supported");
  assert.equal(originator(record.provenance).key, EXE, "the requester, not the hub, originated the request");
  assert.deepEqual(withRole(record.provenance, "transformed").map((item) => item.key), ["EXT-summa.hub-1"]);
  assert.deepEqual(record.provenance.contributions["EXT-summa.hub-1"].actor, HUB_ACTOR);
});

test("an unknown requester is keyed by the operation, and a human operator is recorded as the hub actor", () => {
  const { requester } = resolveRequester({ declared: {} });
  const { record } = dispatchRecord({ operationId: "hub-2", at: AT, repoId: "chrona", command: "add", hubActor: HUMAN, requester });
  assert.deepEqual(Object.keys(record.provenance.contributions), ["EXT-op.hub-2", "EXT-summa.hub-2"]);
  assert.equal(record.requester.actor.kind, "unknown");
  assert.deepEqual(record.hubActor, HUMAN);
});

test("the pure functions never mutate their inputs", () => {
  const env = Object.freeze({ ...HUB_ENV });
  const declared = Object.freeze({ actor: Object.freeze({ ...AGENT }), execution: EXE });
  const { requester } = resolveRequester({ declared });
  spokeEnvironment(env, requester);
  assert.equal(env.ROS_ACTOR, "anthropic/claude-code");
});

// ---- effectful create against a fake spoke ---------------------------------

// A spoke whose ./ros records the identity it was given, like an older or
// newer ROS would read it, and answers `add` with a work item.
const makeSpoke = (dir) => {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, "ros.json"), JSON.stringify({ repository: { id: "spoke" } }));
  fs.writeFileSync(path.join(dir, "ros"), `#!/usr/bin/env node
const fs = require("node:fs");
const seen = Object.fromEntries(${JSON.stringify(IDENTITY_VARIABLES)}.map((name) => [name, process.env[name] ?? null]));
fs.appendFileSync(__dirname + "/calls.jsonl", JSON.stringify({ args: process.argv.slice(2), env: seen }) + "\\n");
console.log(JSON.stringify({ id: "WI-1", title: process.argv[3] }));
`, { mode: 0o755 });
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ type: "commonjs" }));
};

const setup = () => {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), "summa-hub-test-"));
  const hubRoot = path.join(base, "hub");
  const spoke = path.join(base, "spoke");
  fs.mkdirSync(hubRoot);
  makeSpoke(spoke);
  registerRepo(hubRoot, spoke);
  const calls = () => fs.readFileSync(path.join(spoke, "calls.jsonl"), "utf8").trim().split("\n").map((line) => JSON.parse(line));
  const dispatches = () => fs.readFileSync(dispatchLogPath(hubRoot), "utf8").trim().split("\n").map((line) => JSON.parse(line));
  return { hubRoot, calls, dispatches };
};

test("create (HTTP path): the spoke receives the requester, the hub records itself separately", () => {
  const { hubRoot, calls, dispatches } = setup();
  const item = createWorkWithIdentity(hubRoot, "spoke", { title: "Bill September", requesterActor: AGENT, requesterExecution: EXE },
    { hubEnv: HUB_ENV, operationId: "hub-9", now: new Date(AT) });
  const [call] = calls();
  assert.deepEqual(Object.fromEntries(Object.entries(call.env).filter(([, value]) => value !== null)), {
    ROS_ACTOR_KIND: "agent", ROS_ACTOR: "openai/codex", ROS_TELEMETRY_PROVIDER: "openai",
    ROS_TELEMETRY_MODEL: "gpt-5-codex", ROS_TELEMETRY_RUNTIME: "codex", ROS_EXECUTION_ID: EXE,
  });
  assert.deepEqual(call.args.slice(-2), ["--actor", "openai/codex"]);
  const [dispatch] = dispatches();
  assert.deepEqual(dispatch.hubActor, HUB_ACTOR);
  assert.deepEqual(dispatch.requester.actor, AGENT);
  assert.equal(dispatch.spokeItemId, "WI-1");
  assert.deepEqual(item.dispatch, dispatch);
});

test("create with nothing declared: the hub's own identity is not forwarded", () => {
  const { hubRoot, calls, dispatches } = setup();
  createWorkWithIdentity(hubRoot, "spoke", { title: "Anonymous" }, { hubEnv: HUB_ENV, operationId: "hub-10", now: new Date(AT) });
  const [call] = calls();
  assert.deepEqual(Object.fromEntries(Object.entries(call.env).filter(([, value]) => value !== null)), { ROS_ACTOR_KIND: "unknown", ROS_ACTOR: "unknown" },
    "regression: the hub operator's provider/runtime/session never reach an unknown requester's spoke");
  assert.ok(!call.args.includes("--actor"));
  assert.equal(dispatches()[0].requester.source, "unknown");
});

test("summa-hub create (CLI path): the invoking process's declaration is the requester; legacy --actor still works", () => {
  const { hubRoot, calls, dispatches } = setup();
  const logged = [];
  const original = console.log;
  console.log = (text) => logged.push(text);
  try {
    assert.equal(main(["--root", hubRoot, "create", "spoke", "Legacy", "--actor", "human:kevin"], { PATH: process.env.PATH, ROS_ACTOR_KIND: "agent", ROS_ACTOR: "x" }), 0);
    assert.equal(main(["--root", hubRoot, "create", "spoke", "Declared"], { PATH: process.env.PATH, ROS_ACTOR_KIND: "human", ROS_ACTOR: "kevin" }), 0);
  } finally {
    console.log = original;
  }
  const [legacy, declared] = calls();
  assert.deepEqual(legacy.args.slice(-2), ["--actor", "human:kevin"], "the legacy string wins and is forwarded verbatim");
  assert.equal(legacy.env.ROS_ACTOR_KIND, "human");
  assert.equal(declared.env.ROS_ACTOR_KIND, "human");
  assert.equal(declared.env.ROS_ACTOR, "kevin");
  assert.deepEqual(dispatches().map((item) => item.hubActor.id), ["echelon/summa-hub", "echelon/summa-hub"]);
  assert.equal(logged.length, 2);
});

test("summa hub server: the request body declares the requester; other routes are the tool-owned hub's", async () => {
  const { createServer } = await import("../tools/summa_hub_server.mjs");
  const { hubRoot, calls, dispatches } = setup();
  const server = createServer(hubRoot, { hubActor: HUMAN, hubEnv: HUB_ENV });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    const created = await fetch(`${base}/api/repos/spoke/work`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: "From the UI", actorJson: AGENT, execution: EXE }),
    });
    assert.equal(created.status, 200);
    const body = await created.json();
    assert.deepEqual(body.dispatch.hubActor, HUMAN, "the declared human operator is the hub actor");
    assert.deepEqual(body.dispatch.requester.actor, AGENT);
    assert.equal(calls()[0].env.ROS_ACTOR, "openai/codex");
    assert.equal(calls()[0].env.ROS_EXECUTION_ID, EXE);
    assert.equal(dispatches().length, 1);
    const bad = await fetch(`${base}/api/repos/spoke/work`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: "Bad", actorJson: { kind: "robot", id: "r" } }),
    });
    assert.equal(bad.status, 400);
    const repos = await (await fetch(`${base}/api/repos`)).json();
    assert.deepEqual(repos.map((repo) => repo.id), ["spoke"]);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
  assert.throws(() => createServer(hubRoot, { hubActor: AGENT }), /must be human or automation/);
});

test("contract 1.1: the scrub list is the reference identity-environment list", async () => {
  const { IDENTITY_ENVIRONMENT_VARIABLES } = await import("../vendor/praxis-provenance/lib/provenance-interchange.mjs");
  const fixture = JSON.parse(fs.readFileSync(new URL("../vendor/praxis-provenance/fixtures/identity-environment.json", import.meta.url), "utf8"));
  assert.deepEqual([...IDENTITY_VARIABLES].sort(), [...fixture.variables].sort());
  assert.equal(IDENTITY_VARIABLES, IDENTITY_ENVIRONMENT_VARIABLES);
  for (const name of fixture.variables) assert.ok(name in HUB_ENV, `test environment covers ${name}`);
});

test("contract 1.1: an environment execution id without a declared identity is not inherited", () => {
  const { requester } = resolveRequester({ declared: {}, invokingEnv: { ROS_EXECUTION_ID: EXE } });
  assert.equal(requester.actor.kind, "unknown");
  assert.equal(requester.execution, undefined);
});

test("contract 1.1: dispatch keys derived from operation ids are escaped injectively", () => {
  const { requester } = resolveRequester({ declared: {} });
  const { record } = dispatchRecord({ operationId: "hub 1", at: AT, repoId: "chrona", command: "add", hubActor: HUB_ACTOR, requester });
  assert.deepEqual(Object.keys(record.provenance.contributions), ["EXT-op.hub_201", "EXT-summa.hub_201"]);
});
