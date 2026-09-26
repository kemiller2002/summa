// Summa project-administration hub: identity propagation to spoke repositories.
//
// Requirements: docs/requirements/SUMMA-PROVENANCE.md (INV-PROV-005..007),
// decision DF-SUMMA-PROV-2026-0001; Praxis RQ-ROS-2026-A016 (identity
// propagation) and DF-ROS-2026-A037.
//
// The hub keeps two identities apart:
// - the REQUESTER: who asked for the spoke operation, taken only from an
//   explicit declaration (structured actor, the legacy `--actor` string, or,
//   for a command-line invocation, the invoking process's own
//   ROS_ACTOR_KIND/ROS_ACTOR/ROS_TELEMETRY_*/ROS_EXECUTION_ID); otherwise
//   unknown. This is what the spoke is told.
// - the HUB ACTOR: the hub itself (automation `echelon/summa-hub`), or the
//   human operating it when explicitly declared. This is recorded by the hub
//   in its own dispatch record and is never forwarded as the requester.
//
// Every function is pure: no clock, environment, or filesystem access; the
// caller passes those in. Inputs are never mutated.

import {
  actorProblems, credentialFindings, appendContribution, emptyBlock, keyFromEnvelopeV1, SCHEMA_TAG,
  IDENTITY_ENVIRONMENT_VARIABLES, identityEnvironment,
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
const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const known = (value) => isNonEmptyString(value) && value !== UNKNOWN;
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
// Injective run-id escaping, identical to the reference keyFromEnvelopeV1 (contract 1.1 rule 6).
const escapeRunId = (text) => String(text).replace(/[^A-Za-z0-9.-]/g, (char) =>
  [...new TextEncoder().encode(char)].map((byte) => `_${byte.toString(16).padStart(2, "0")}`).join(""));

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
  const match = LEGACY_ACTOR.exec(text.trim());
  if (match && KINDS.has(match[1])) return completeActor({ kind: match[1], id: match[2].trim() });
  return completeActor({ kind: UNKNOWN, id: text.trim() });
};

/** The actor a process declared through the ROS_* identity variables, or undefined when it declared none. */
export const actorFromEnvironment = (env) => {
  const kind = env.ROS_ACTOR_KIND?.trim();
  const id = env.ROS_ACTOR?.trim();
  if (!isNonEmptyString(kind) && !isNonEmptyString(id)) return undefined;
  const actorKind = isNonEmptyString(kind) ? kind : UNKNOWN;
  const base = { kind: actorKind, id: isNonEmptyString(id) ? id : UNKNOWN };
  if (actorKind === "human") return base;
  return {
    ...base,
    provider: env.ROS_TELEMETRY_PROVIDER?.trim() || UNKNOWN,
    model: env.ROS_TELEMETRY_MODEL?.trim() || UNKNOWN,
    runtime: env.ROS_TELEMETRY_RUNTIME?.trim() || UNKNOWN,
  };
};

/**
 * Resolves the requester from explicit declarations only, in precedence order:
 * 1. `declared.actor` (a structured Praxis actor) with optional `declared.execution`;
 * 2. `declared.legacyActor` (the `--actor` string older clients send);
 * 3. `invokingEnv` -- the environment of a command-line invocation, where the
 *    invoking process IS the requester. The HTTP server passes no environment:
 *    the server's own identity is not the requester's.
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
  const fromEnv = invokingEnv === undefined ? undefined : actorFromEnvironment(invokingEnv);
  if (fromEnv !== undefined) {
    const execution = invokingEnv.ROS_EXECUTION_ID?.trim() || undefined;
    return validated(fromEnv, execution, "environment", "requester");
  }
  return { ok: true, requester: { actor: { ...UNKNOWN_ACTOR }, source: "unknown" } };
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
  const requesterKey = requester.execution ?? keyFromEnvelopeV1({ operationId });
  const hubKey = `EXT-summa.${escapeRunId(operationId)}`;
  const requested = appendContribution(emptyBlock(), requesterKey, {
    operations: ["created"], at, actor: clone(requester.actor), reason: `requested ${command} in ${repoId} through the hub`,
  });
  if (!requested.ok) return { ok: false, error: requested.error };
  const carried = appendContribution(requested.block, hubKey, {
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
