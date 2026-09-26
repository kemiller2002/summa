// Summa billing.record: provenance portion.
//
// Requirements: docs/requirements/SUMMA-PROVENANCE.md (INV-PROV-001..004, INV-PROV-012,
// INV-PROV-008), decision DF-SUMMA-PROV-2026-0001; Praxis
// RQ-ROS-2026-A008 (lineage), A015 (versioned interchange), A017 (no
// credentials), A018 (independence), A019 (not authority), DF-ROS-2026-A037
// (contract revision 1.2).
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
  SCHEMA_TAG, classify, classifyText, appendContribution, addLineage, actorProblems, credentialFindings, emptyBlock,
  escapeKeySegment, keyFromEnvelopeV1, keyKind,
} from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

export const BILLING_SCHEMA = "summa.billing-record/1";
const BILLING_SCHEMA_PATTERN = /^summa\.billing-record\/([1-9][0-9]*)$/;
const EXECUTION = /^(EXE-[A-Za-z0-9._-]+|EXT-[a-z][a-z0-9-]*\.[A-Za-z0-9._-]+)$/;
const UNKNOWN = "unknown";

/** Summa's own identity when it generates a billing record without a declared requester. */
export const SUMMA_ACTOR = Object.freeze({ kind: "automation", id: "echelon/summa", provider: "echelon", model: UNKNOWN, runtime: "summa" });

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
// Contract 1.2 rule 2: "blank" means empty after trimming ASCII whitespace only
// (tab, LF, VT, FF, CR, space); every other character, U+0085 and U+FEFF included, is content.
const asciiTrim = (value) => value.replace(/^[\t\n\v\f\r ]+|[\t\n\v\f\r ]+$/g, "");
const isNonEmptyString = (value) => typeof value === "string" && asciiTrim(value).length > 0;
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
// A key derived from an id that cannot form a key (empty, or an unpaired surrogate)
// is an error, never an invented key (contract 1.2 rule 4).
const tryKey = (derive) => {
  try { return { ok: true, key: derive() }; } catch (failure) { return { ok: false, error: failure.message }; }
};
const optionalString = (value, field) => (value === undefined || isNonEmptyString(value) ? [] : [`${field} must be a non-empty string when present (null is not absence)`]);
const error = (code, message) => ({ code, message });

/**
 * Provenance status of a block: `attributed`, `unknown` (absent or empty --
 * for example Praxis was not available; never inferred), `unsupported`
 * (another major, carried verbatim), or `malformed`.
 */
export const provenanceStatus = (block) => {
  // JSON null is not absence (contract 1.1): classify reports it as malformed.
  if (block === undefined) return "unknown";
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
    ...optionalString(source.kind, `${prefix}.kind`),
    ...optionalString(source.workItemId, `${prefix}.workItemId`),
    ...performerProblems,
  ].map((message) => error("invalid-billing-record", message));
  const verdict = source.provenance === undefined ? undefined : classify(source.provenance);
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
    ...(record.provenance === undefined || classify(record.provenance).verdict !== "malformed" ? []
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
 * Receives a billing record that arrives as JSON text (a file, stdin, or a
 * request body). Contract 1.2 rule 1: the text is classified with the vendored
 * `classifyText` scanner before parsing, so a repeated member name (which
 * JSON.parse would silently resolve to the last) or an unpaired surrogate
 * anywhere in it (the record's block or any source block) rejects the record
 * as `malformed-text`. The parsed record then goes through receiveBillingRecord.
 */
export const receiveBillingRecordText = (text) => {
  if (typeof text !== "string") return { ok: false, errors: [error("invalid-billing-record", "the billing record text must be a string")] };
  const parsed = (() => { try { return { ok: true, value: JSON.parse(text) }; } catch { return { ok: false }; } })();
  if (!parsed.ok) return { ok: false, errors: [error("malformed-text", "the billing record is not valid JSON")] };
  const scanned = classifyText(`{"schema":"${SCHEMA_TAG}","contributions":{},"x-record":${text}}`);
  const problems = scanned.problems
    .filter((problem) => /member name repeated|unpaired UTF-16 surrogate/.test(problem))
    .map((problem) => problem.replace(/^x-record\.?/, ""));
  return problems.length > 0 ? { ok: false, errors: problems.map((problem) => error("malformed-text", problem)) } : receiveBillingRecord(parsed.value);
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
  // Contract 1.2 rule 4: operation-derived keys use the vendored escapeKeySegment.
  const key = tryKey(() => (creator === undefined ? `EXT-summa.${escapeKeySegment(operationId)}`
    : creator.execution ?? keyFromEnvelopeV1({ operationId })));
  if (!key.ok) return { ok: false, error: `operationId cannot form a contribution key: ${key.error}` };
  const actor = creator === undefined ? { ...SUMMA_ACTOR } : clone(creator.actor);
  const created = appendContribution(emptyBlock(), key.key, { operations: ["created"], at, actor, reason: "billing record derived from billable sources" });
  if (!created.ok) return { ok: false, error: created.error };
  // billing.record is a CREATE: the new record's own block has its creator
  // and lineage (each supported source's derivedFrom, then the source and
  // work-item references). Source blocks stay verbatim in `sources`.
  const upstream = (source) => (source.provenance !== undefined && classify(source.provenance).verdict === "supported" && Array.isArray(source.provenance.derivedFrom)
    ? source.provenance.derivedFrom : []);
  // Contract 1.2 rule 3: lineage taken from the sources (request payload) goes
  // through the checked addLineage (which also de-duplicates, first occurrence
  // kept); a refusal rejects the request -- never stored, never dropped.
  const lineage = sources.flatMap((source) => [...upstream(source), source.ref, ...(source.workItemId !== undefined ? [source.workItemId] : [])]);
  const added = addLineage(created.block, lineage);
  if (!added.ok) return { ok: false, error: `derivedFrom: ${added.error}` };
  const record = {
    schemaVersion: BILLING_SCHEMA,
    billingRecordId,
    sources: clone(sources),
    provenance: { ...added.block, schema: SCHEMA_TAG },
  };
  const received = receiveBillingRecord(record);
  return received.ok ? { ok: true, record: received.record } : { ok: false, error: received.errors.map((item) => item.message).join("; ") };
};

/**
 * Appends a contribution (for example a human's `approved` or `modified`) to
 * the billing record's OWN block under the Praxis append rules; source blocks
 * are never touched. Only the reference library's result is stored: it
 * refuses credentials, back-dated contributions, re-attribution, a late
 * `created`, and an unknown actor extending a known entry, and it never
 * returns a block that would not classify as supported.
 */
export const appendBillingContribution = (record, key, contribution) => {
  if (record.provenance !== undefined && classify(record.provenance).verdict === "unsupported") {
    return { ok: false, error: "refusing to append to an unsupported provenance block; it is carried verbatim" };
  }
  // A stored `"provenance": null` is malformed, not absent (contract 1.2 rule 6):
  // only an absent block starts empty; null reaches the library and is refused.
  const appended = appendContribution(record.provenance === undefined ? emptyBlock() : record.provenance, key, contribution);
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
