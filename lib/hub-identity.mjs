// Summa project-administration hub: identity propagation to spoke repositories.
//
// Requirements: docs/requirements/SUMMA-PROVENANCE.md (INV-PROV-005..007, INV-PROV-012),
// decision DF-SUMMA-PROV-2026-0001; Praxis RQ-ROS-2026-A016 1.2.0 (identity
// propagation, one identity source) and DF-ROS-2026-A037 (contract revision 1.2).
//
// The hub keeps two identities apart:
// - the REQUESTER: who asked for the spoke operation, taken WHOLLY from one
//   explicit source (contract 1.2 rule 5): a structured actor or the legacy
//   `--actor` string, each with only the execution declared with it; or, for a
//   command-line invocation, the invoking process's own
//   ROS_ACTOR_KIND/ROS_ACTOR/ROS_TELEMETRY_* together with its
//   ROS_EXECUTION_ID; otherwise unknown. This is what the spoke is told.
// - the HUB ACTOR: the hub itself (automation `echelon/summa-hub`), or the
//   human operating it when explicitly declared. This is recorded by the hub
//   in its own dispatch record and is never forwarded as the requester.
//
// Every function is pure: no clock, environment, or filesystem access; the
// caller passes those in. Inputs are never mutated.

import {
  actorProblems, credentialFindings, appendContribution, classifyText, emptyBlock, escapeKeySegment,
  keyFromEnvelopeV1, SCHEMA_TAG, IDENTITY_ENVIRONMENT_VARIABLES, identityEnvironment,
} from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const UNKNOWN = "unknown";
const EXECUTION = /^(EXE-[A-Za-z0-9._-]+|EXT-[a-z][a-z0-9-]*\.[A-Za-z0-9._-]+)$/;
const LEGACY_ACTOR = /^(agent|human|automation|unknown):(.+)$/;
const KINDS = new Set(["agent", "human", "automation", "unknown"]);

/**
 * Every variable Praxis identity discovery reads (contract revision 1.1,
 * vendored fixtures/identity-environment.json): the ROS_* declarations, the
 * ROS_TELEMETRY_* session/run/version keys, agent-runtime session ids
 * (Claude Code, Codex, Gemini, Copilot), GitHub Actions markers, and
 * OLLAMA_HOST. All of them are removed before a spoke is launched.
 */
export const IDENTITY_VARIABLES = IDENTITY_ENVIRONMENT_VARIABLES;

/** The hub's own default identity: a deterministic non-agent process. */
export const HUB_ACTOR = Object.freeze({ kind: "automation", id: "echelon/summa-hub", provider: "echelon", model: UNKNOWN, runtime: "summa-hub" });

export const UNKNOWN_ACTOR = Object.freeze({ kind: UNKNOWN, id: UNKNOWN, provider: UNKNOWN, model: UNKNOWN, runtime: UNKNOWN });

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
// Contract 1.2 rule 2: "blank" means empty after trimming ASCII whitespace only
// (tab, LF, VT, FF, CR, space); every other character, U+0085 and U+FEFF included, is content.
const asciiTrim = (value) => value.replace(/^[\t\n\v\f\r ]+|[\t\n\v\f\r ]+$/g, "");
const isNonEmptyString = (value) => typeof value === "string" && asciiTrim(value).length > 0;
const known = (value) => isNonEmptyString(value) && value !== UNKNOWN;
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
// A key derived from an id that cannot form a key (empty, or an unpaired surrogate)
// is an error, never an invented key (contract 1.2 rule 4).
const tryKey = (derive) => {
  try { return { ok: true, key: derive() }; } catch (failure) { return { ok: false, error: failure.message }; }
};

/** A Praxis actor with non-human attributes filled with the literal "unknown" (never guessed). */
const completeActor = (actor) => (actor.kind === "human"
  ? { ...actor }
  : { ...actor, provider: actor.provider ?? UNKNOWN, model: actor.model ?? UNKNOWN, runtime: actor.runtime ?? UNKNOWN });

const validated = (actor, execution, source, prefix) => {
  const problems = [
    ...actorProblems(actor, prefix),
    ...(execution === undefined || EXECUTION.test(execution) ? [] : [`${prefix} execution '${execution}' must be EXE-... or EXT-<system>.<run-id>`]),
    ...credentialFindings({ actor, execution }).map((path) => `${prefix} ${path}: credential-like value; identity must never carry authentication material`),
  ];
  return problems.length > 0
    ? { ok: false, error: problems.join("; ") }
    : { ok: true, requester: { actor, ...(execution !== undefined ? { execution } : {}), source } };
};

/**
 * Parses the legacy `--actor` string. `kind:id` with a Praxis kind is a
 * declaration of both; any other string is only an id with an unknown kind.
 */
export const actorFromLegacyString = (text) => {
  const match = LEGACY_ACTOR.exec(asciiTrim(text));
  if (match && KINDS.has(match[1])) return completeActor({ kind: match[1], id: asciiTrim(match[2]) });
  return completeActor({ kind: UNKNOWN, id: asciiTrim(text) });
};

/** The actor a process declared through the ROS_* identity variables, or undefined when it declared none. */
export const actorFromEnvironment = (env) => {
  const trimmed = (name) => (typeof env[name] === "string" ? asciiTrim(env[name]) : undefined);
  const kind = trimmed("ROS_ACTOR_KIND");
  const id = trimmed("ROS_ACTOR");
  if (!isNonEmptyString(kind) && !isNonEmptyString(id)) return undefined;
  const actorKind = isNonEmptyString(kind) ? kind : UNKNOWN;
  const base = { kind: actorKind, id: isNonEmptyString(id) ? id : UNKNOWN };
  if (actorKind === "human") return base;
  return {
    ...base,
    provider: trimmed("ROS_TELEMETRY_PROVIDER") || UNKNOWN,
    model: trimmed("ROS_TELEMETRY_MODEL") || UNKNOWN,
    runtime: trimmed("ROS_TELEMETRY_RUNTIME") || UNKNOWN,
  };
};

/**
 * Resolves the requester from explicit declarations only, taking the actor
 * WHOLLY from one source (contract 1.2 rule 5, RQ-ROS-2026-A016 1.2.0), in
 * precedence order:
 * 1. `declared.actor` (a structured Praxis actor) with optional `declared.execution`;
 * 2. `declared.legacyActor` (the `--actor` string older clients send), with
 *    optional `declared.execution`;
 * 3. `invokingEnv` -- the environment of a command-line invocation, where the
 *    invoking process IS the requester. The HTTP server passes no environment:
 *    the server's own identity is not the requester's.
 * A declaration replaces the environment completely: no field falls back to it,
 * and it never inherits ROS_EXECUTION_ID. ROS_EXECUTION_ID is honoured only with
 * an identity declared in that same environment. An execution declared without
 * an actor names no requester and is rejected rather than silently dropped.
 * Otherwise the requester is unknown. Returns { ok, requester } or { ok: false, error }.
 */
export const resolveRequester = ({ declared = {}, invokingEnv } = {}) => {
  if (declared.actor !== undefined) {
    if (!isObject(declared.actor)) return { ok: false, error: "requester actor must be a JSON object" };
    return validated(completeActor(declared.actor), declared.execution, "declared", "requester");
  }
  if (isNonEmptyString(declared.legacyActor)) {
    return validated(actorFromLegacyString(declared.legacyActor), declared.execution, "legacy-actor", "requester");
  }
  if (declared.execution !== undefined) {
    return { ok: false, error: "requester execution was declared without a requester actor; declare the actor (--actor-json or --actor) with it" };
  }
  const fromEnv = invokingEnv === undefined ? undefined : actorFromEnvironment(invokingEnv);
  if (fromEnv !== undefined) {
    const execution = (typeof invokingEnv.ROS_EXECUTION_ID === "string" ? asciiTrim(invokingEnv.ROS_EXECUTION_ID) : "") || undefined;
    return validated(fromEnv, execution, "environment", "requester");
  }
  return { ok: true, requester: { actor: { ...UNKNOWN_ACTOR }, source: "unknown" } };
};

/**
 * Parses identity JSON received as text (`--actor-json`, `--hub-actor-json`,
 * the HTTP `actorJson` field) under contract 1.2 rule 1: the text is checked
 * with the vendored `classifyText` scanner before JSON.parse can silently keep
 * the last of two repeated member names (`{"kind":"human","kind":"agent"}`) or
 * accept an unpaired surrogate. The text is scanned as the value of an `x-`
 * extension member of an empty block. Returns { ok, value } or { ok: false, error }.
 */
export const parseJsonText = (text, name) => {
  if (typeof text !== "string") return { ok: false, error: `${name} must be JSON text` };
  const parsed = (() => { try { return { ok: true, value: JSON.parse(text) }; } catch { return { ok: false }; } })();
  if (!parsed.ok) return { ok: false, error: `${name} must be a JSON object` };
  const scanned = classifyText(`{"schema":"${SCHEMA_TAG}","contributions":{},"x-text":${text}}`);
  const problems = scanned.problems
    .filter((problem) => /member name repeated|unpaired UTF-16 surrogate/.test(problem))
    .map((problem) => problem.replace(/^x-text\.?/, ""));
  return problems.length > 0 ? { ok: false, error: `${name} is malformed: ${problems.join("; ")}` } : parsed;
};

/**
 * The hub's own actor: automation `echelon/summa-hub`, or the operator
 * explicitly declared for this hub instance (a human using the UI, or another
 * automation). The hub is never an agent.
 */
export const resolveHubActor = (declared) => {
  if (declared === undefined) return { ok: true, actor: { ...HUB_ACTOR } };
  if (!isObject(declared)) return { ok: false, error: "hub actor must be a JSON object" };
  if (declared.kind !== "human" && declared.kind !== "automation") {
    return { ok: false, error: `hub actor kind '${declared.kind}' must be human or automation; the requester's identity is declared separately` };
  }
  const actor = completeActor(declared);
  const problems = [...actorProblems(actor, "hub actor"), ...credentialFindings(actor).map((path) => `hub actor ${path}: credential-like value`)];
  return problems.length > 0 ? { ok: false, error: problems.join("; ") } : { ok: true, actor };
};

/**
 * The environment for a spoke's ./ros, built with the reference
 * `identityEnvironment`: every identity variable the hub inherited is removed
 * (IDENTITY_ENVIRONMENT_VARIABLES), then only the requester's declared values
 * are set. An undeclared requester is set explicitly to ROS_ACTOR_KIND=unknown
 * and ROS_ACTOR=unknown, so nothing (the hub's own session, runtime, CI run,
 * or model server) can be inferred as the requester. Unknown
 * provider/model/runtime are omitted (the spoke records "unknown").
 */
export const spokeEnvironment = (hubEnv, requester) => {
  const { actor, execution } = requester;
  const nonHuman = actor.kind !== "human";
  return identityEnvironment(hubEnv, {
    ROS_ACTOR_KIND: actor.kind,
    ROS_ACTOR: actor.id,
    ROS_TELEMETRY_PROVIDER: nonHuman && known(actor.provider) ? actor.provider : undefined,
    ROS_TELEMETRY_MODEL: nonHuman && known(actor.model) ? actor.model : undefined,
    ROS_TELEMETRY_RUNTIME: nonHuman && known(actor.runtime) ? actor.runtime : undefined,
    ROS_EXECUTION_ID: execution,
  });
};

/**
 * The legacy `--actor` argument for spokes on older ROS versions, which read
 * only a string: the caller's own legacy string verbatim when it sent one,
 * otherwise the requester's id when known, otherwise nothing.
 */
export const legacyActorArguments = (requester, legacyActor) =>
  isNonEmptyString(legacyActor) ? ["--actor", legacyActor]
    : known(requester.actor.id) ? ["--actor", requester.actor.id]
      : [];

/**
 * The hub's own record of one dispatch: its own actor and the requester are
 * separate fields, and the embedded praxis.provenance/1 block credits the
 * requester with the request (`created`, keyed by its execution or
 * EXT-op.<operationId>) and the hub with carrying it (`transformed`, keyed
 * EXT-summa.<operationId>). Neither is substituted for the other.
 */
export const dispatchRecord = ({ operationId, at, repoId, command, hubActor, requester, legacyActor, spokeItemId }) => {
  // Contract 1.2 rule 4: both keys use the vendored per-code-point escapeKeySegment.
  const requesterKey = tryKey(() => requester.execution ?? keyFromEnvelopeV1({ operationId }));
  const hubKey = tryKey(() => `EXT-summa.${escapeKeySegment(operationId)}`);
  if (!requesterKey.ok || !hubKey.ok) return { ok: false, error: `operationId cannot form a contribution key: ${requesterKey.error ?? hubKey.error}` };
  const requested = appendContribution(emptyBlock(), requesterKey.key, {
    operations: ["created"], at, actor: clone(requester.actor), reason: `requested ${command} in ${repoId} through the hub`,
  });
  if (!requested.ok) return { ok: false, error: requested.error };
  const carried = appendContribution(requested.block, hubKey.key, {
    operations: ["transformed"], at, actor: clone(hubActor), reason: `hub dispatched ${command} to ${repoId}`,
  });
  if (!carried.ok) return { ok: false, error: carried.error };
  return {
    ok: true,
    record: {
      schema: "summa.hub-dispatch/1",
      operationId,
      at,
      repoId,
      command,
      hubActor: clone(hubActor),
      requester: { actor: clone(requester.actor), ...(requester.execution !== undefined ? { execution: requester.execution } : {}), source: requester.source },
      ...(isNonEmptyString(legacyActor) ? { legacyActor } : {}),
      ...(spokeItemId !== undefined ? { spokeItemId } : {}),
      provenance: { ...carried.block, schema: SCHEMA_TAG },
    },
  };
};
