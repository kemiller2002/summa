// Summa billing.record: provenance portion.
//
// Requirements: docs/requirements/SUMMA-PROVENANCE.md (INV-PROV-001..004,
// INV-PROV-008), decision DF-SUMMA-PROV-2026-0001; Praxis
// RQ-ROS-2026-A008 (lineage), A015 (versioned interchange), A017 (no
// credentials), A018 (independence), A019 (not authority), DF-ROS-2026-A037.
//
// A billing record carries:
// - `sources[]`: every contributing source (for example a Chrona time entry)
//   with its performer and execution and the source's own praxis.provenance/1
//   block carried VERBATIM, so grouping never destroys source provenance
//   (INV-AUD-004, INV-CHR-010);
// - `provenance`: the billing record's own praxis.provenance/1 block: who
//   created it (keyed by execution), and `derivedFrom` lineage to the sources
//   and work items. Lineage is not authorship: the agent that performed the
//   work is not recorded as the author of the billing record, and the billing
//   record's creator is never recorded as the performer.
//
// Accounting never depends on provenance or on Praxis being available: a
// record with absent or unknown provenance is valid and billable, and
// nothing here grants, denies, or weights anything by actor.
// Every function is pure; inputs are never mutated.

import {
  SCHEMA_TAG, classify, appendContribution, addLineage, actorProblems, credentialFindings, emptyBlock,
  foreignExecutionKey, keyKind,
} from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

export const BILLING_SCHEMA = "summa.billing-record/1";
const BILLING_SCHEMA_PATTERN = /^summa\.billing-record\/([1-9][0-9]*)$/;
const EXECUTION = /^(EXE-[A-Za-z0-9._-]+|EXT-[a-z][a-z0-9-]*\.[A-Za-z0-9._-]+)$/;
const UNKNOWN = "unknown";

/** Summa's own identity when it generates a billing record without a declared requester. */
export const SUMMA_ACTOR = Object.freeze({ kind: "automation", id: "echelon/summa", provider: "echelon", model: UNKNOWN, runtime: "summa" });

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
const safeRunId = (text) => String(text).replace(/[^A-Za-z0-9._-]/g, "-");
const error = (code, message) => ({ code, message });

/**
 * Provenance status of a block: `attributed`, `unknown` (absent or empty --
 * for example Praxis was not available; never inferred), `unsupported`
 * (another major, carried verbatim), or `malformed`.
 */
export const provenanceStatus = (block) => {
  if (block === undefined || block === null) return "unknown";
  const verdict = classify(block).verdict;
  if (verdict !== "supported") return verdict;
  return Object.keys(block.contributions).length === 0 ? "unknown" : "attributed";
};

const sourceProblems = (source, index) => {
  const prefix = `sources[${index}]`;
  if (!isObject(source)) return [error("invalid-billing-record", `${prefix} must be an object`)];
  const performer = source.performer;
  const performerProblems = performer === undefined ? []
    : !isObject(performer) ? [`${prefix}.performer must be an object`]
      : [
        ...actorProblems(performer.actor, `${prefix}.performer.actor`),
        ...(performer.execution === undefined || (typeof performer.execution === "string" && EXECUTION.test(performer.execution))
          ? [] : [`${prefix}.performer.execution must be EXE-... or EXT-<system>.<run-id>`]),
      ];
  const structural = [
    ...(isNonEmptyString(source.ref) ? [] : [`${prefix}.ref must be a non-empty namespaced reference, e.g. chrona:entry/ID`]),
    ...performerProblems,
  ].map((message) => error("invalid-billing-record", message));
  const verdict = source.provenance === undefined || source.provenance === null ? undefined : classify(source.provenance);
  const provenance = verdict?.verdict === "malformed"
    ? verdict.problems.map((message) => error("malformed-provenance", `${prefix}.provenance: ${message}`)) : [];
  return [...structural, ...provenance];
};

/**
 * Receives a billing record at Summa's boundary. Malformed provenance (on the
 * record or any source) and credential-like values reject it with structured
 * errors; unsupported majors are carried verbatim; absent provenance is valid.
 * Returns { ok: true, record, billable: true, provenance, sources } or { ok: false, errors }.
 */
export const receiveBillingRecord = (record) => {
  if (!isObject(record)) return { ok: false, errors: [error("invalid-billing-record", "a billing record must be a JSON object")] };
  const secrets = credentialFindings(record);
  if (secrets.length > 0) return { ok: false, errors: secrets.map((path) => error("credential", `${path}: credential-like value; billing provenance must never carry authentication material`)) };
  if (record.schemaVersion !== BILLING_SCHEMA) {
    const other = typeof record.schemaVersion === "string" && BILLING_SCHEMA_PATTERN.test(record.schemaVersion);
    return { ok: false, errors: [error(other ? "unsupported-billing-version" : "invalid-billing-record", other ? `schemaVersion '${record.schemaVersion}' is not supported (expects ${BILLING_SCHEMA})` : `schemaVersion must be ${BILLING_SCHEMA}`)] };
  }
  const errors = [
    ...(isNonEmptyString(record.billingRecordId) ? [] : [error("invalid-billing-record", "billingRecordId must be a non-empty string")]),
    ...(Array.isArray(record.sources) ? record.sources.flatMap(sourceProblems) : [error("invalid-billing-record", "sources must be an array")]),
    ...(record.provenance === undefined || record.provenance === null || classify(record.provenance).verdict !== "malformed" ? []
      : classify(record.provenance).problems.map((message) => error("malformed-provenance", `provenance: ${message}`))),
  ];
  if (errors.length > 0) return { ok: false, errors };
  return {
    ok: true,
    record: clone(record),
    billable: true,
    provenance: provenanceStatus(record.provenance),
    sources: record.sources.map((source) => ({ ref: source.ref, provenance: provenanceStatus(source.provenance) })),
  };
};

/**
 * A billing source from a published Chrona time entry. The entry's performer,
 * execution, work item, and provenance block are carried unchanged (an
 * unsupported block verbatim); absent provenance stays absent (`unknown`).
 */
export const sourceFromChronaEntry = (entry) => ({
  kind: "chrona-time-entry",
  ref: `chrona:entry/${entry.entryId}`,
  ...(entry.workItemId !== undefined ? { workItemId: entry.workItemId } : {}),
  ...(entry.actor !== undefined ? { performer: { actor: clone(entry.actor), ...(entry.execution !== undefined ? { execution: entry.execution } : {}) } } : {}),
  ...(entry.exactDurationSeconds !== undefined ? { durationSeconds: entry.exactDurationSeconds } : {}),
  ...(entry.provenance !== undefined ? { provenance: clone(entry.provenance) } : {}),
});

/**
 * Derives a billing record from sources.
 * creator: the explicitly declared actor creating the record ({ actor, execution? });
 * absent means Summa itself generated it (automation `echelon/summa`, keyed
 * EXT-summa.<operationId>). An agent creator is recorded as an agent, keyed
 * by its execution or EXT-op.<operationId> -- never as a human.
 */
export const deriveBillingRecord = ({ billingRecordId, sources, creator, operationId, at }) => {
  const key = creator === undefined ? foreignExecutionKey("summa", safeRunId(operationId))
    : creator.execution ?? foreignExecutionKey("op", safeRunId(operationId));
  const actor = creator === undefined ? { ...SUMMA_ACTOR } : clone(creator.actor);
  const created = appendContribution(emptyBlock(), key, { operations: ["created"], at, actor, reason: "billing record derived from billable sources" });
  if (!created.ok) return { ok: false, error: created.error };
  const lineage = [...new Set(sources.flatMap((source) => [source.ref, ...(isNonEmptyString(source.workItemId) ? [source.workItemId] : [])]))];
  const record = {
    schemaVersion: BILLING_SCHEMA,
    billingRecordId,
    sources: clone(sources),
    provenance: { ...addLineage(created.block, lineage), schema: SCHEMA_TAG },
  };
  const received = receiveBillingRecord(record);
  return received.ok ? { ok: true, record: received.record } : { ok: false, error: received.errors.map((item) => item.message).join("; ") };
};

/**
 * Appends a contribution (for example a human's `approved` or `modified`) to
 * the billing record's own block under the Praxis append rules. Refuses to
 * touch an unsupported block and to re-attribute an existing key.
 */
export const appendBillingContribution = (record, key, contribution) => {
  if (record.provenance !== undefined && classify(record.provenance).verdict === "unsupported") {
    return { ok: false, error: "refusing to append to an unsupported provenance block; it is carried verbatim" };
  }
  const appended = appendContribution(record.provenance ?? emptyBlock(), key, contribution);
  return appended.ok ? { ok: true, record: { ...clone(record), provenance: appended.block } } : appended;
};

/**
 * The agent executions behind the billed work, traced through the sources:
 * each source performer's execution and every agent contribution's execution
 * key in each supported source block. Used to trace agent execution -> work
 * item -> billed amount; never used to decide billing.
 */
export const originatingExecutions = (record) => [...new Set(record.sources.flatMap((source) => [
  ...(source.performer?.execution !== undefined ? [source.performer.execution] : []),
  ...(source.provenance !== undefined && classify(source.provenance).verdict === "supported"
    ? Object.entries(source.provenance.contributions)
      .filter(([key, entry]) => entry.actor.kind === "agent" && keyKind(key) !== "contribution")
      .map(([key]) => key)
    : []),
]))];
