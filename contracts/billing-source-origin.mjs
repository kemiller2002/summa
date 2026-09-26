// Contract-verification harness for `summa.billing-source-origin` v1
// (docs/requirements/SUMMA-PROVENANCE-REQUIREMENTS.md, INV-PROV-001..008).
// Not product code: the F# implementation must pass the same fixtures.
//
// Pure functions: inputs are never mutated; results are plain data.
import { readFileSync } from "node:fs";
import Ajv2020 from "ajv/dist/2020.js";
import { jsonEqual, looksLikeCredential, validate as validateRecord, validateActor } from "./praxis-provenance/praxis-provenance-record.mjs";

export const CONTRACT_NAME = "summa.billing-source-origin";

export const schema = Object.freeze(JSON.parse(readFileSync(new URL("./billing-source-origin.v1.schema.json", import.meta.url), "utf8")));

const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false });
const schemaValid = ajv.compile(schema);
const lineSourcesValid = ajv.getSchema(`${schema.$id}#/$defs/invoiceLineSources`);

const problem = (field, message) => Object.freeze({ field, message });
const schemaProblems = (errors) => (errors ?? []).map((error) => problem(error.instancePath || "/", error.message ?? "schema violation"));

const ACTOR_FIELDS = ["kind", "id", "provider", "model", "runtime"];

const provenanceProblems = (field, origin) => {
  if (origin.provenance === undefined) return [];
  const reading = validateRecord(origin.provenance);
  if (reading.status === "invalid") return reading.problems.map((item) => problem(`${field}.provenance.${item.field}`, item.message));
  if (reading.status === "unsupported" || origin.originExecution === undefined) return []; // carried verbatim; never interpreted
  const contribution = reading.record.contributions.find((item) => item.key === origin.originExecution);
  if (!contribution) return [problem(`${field}.originExecution`, `'${origin.originExecution}' is not a contribution of the carried provenance record`)];
  const agrees = ACTOR_FIELDS.every((name) => contribution.actor[name] === (typeof origin.originActor[name] === "string" ? origin.originActor[name] : undefined));
  return agrees ? [] : [problem(`${field}.originActor`, `the carried provenance attributes ${origin.originExecution} to ${contribution.actor.kind}:${contribution.actor.id}`)];
};

const entryProblems = (entry, index) => {
  const field = `origin.lineage[${index}].origin`;
  return [
    ...validateActor(entry.origin.originActor).map((item) => problem(`${field}.originActor.${item.field}`, item.message)),
    ...(looksLikeCredential(entry.origin.originExecution) ? [problem(`${field}.originExecution`, "value looks like a credential")] : []),
    ...provenanceProblems(field, entry.origin),
  ];
};

/**
 * Every reason `source` is not an acceptable v1 billing source origin
 * (INV-PROV-001..003, 005, 008). Empty means acceptable; acceptable is not
 * authorized (INV-PROV-006). Nothing here contacts Praxis or Chrona.
 */
export const sourceOriginProblems = (source) => {
  if (!schemaValid(source)) return schemaProblems(schemaValid.errors);
  const lineage = source.origin === "unknown" ? [] : source.origin.lineage;
  return [
    ...["sourceId", "sourceVersion"].filter((name) => looksLikeCredential(source[name])).map((name) => problem(name, "value looks like a credential")),
    ...lineage.flatMap(entryProblems),
    ...lineage
      .filter((entry, index) => lineage.findIndex((item) => jsonEqual(item, entry)) !== index)
      .map((entry) => problem("origin.lineage", `origin lineage for '${entry.from}' is duplicated`)),
  ];
};

const sourceKey = (source) => `${source.sourceApplication}:${source.sourceId}@${source.sourceVersion}`;

/**
 * INV-PROV-004: a grouped invoice line's sources must be exactly the grouped
 * billing source origins, each verbatim and once. A dropped, rewritten,
 * collapsed, duplicated, or invented origin is reported.
 */
export const groupingProblems = (sources, lineSources) => {
  const shape = [
    ...sources.flatMap((source, index) => sourceOriginProblems(source).map((item) => problem(`sources[${index}].${item.field}`, item.message))),
    ...(lineSourcesValid(lineSources)
      ? lineSources.flatMap((source, index) => sourceOriginProblems(source).map((item) => problem(`line[${index}].${item.field}`, item.message)))
      : schemaProblems(lineSourcesValid.errors).map((item) => problem(`line${item.field}`, item.message))),
  ];
  if (shape.length > 0) return shape;
  return [
    ...sources.filter((source) => !lineSources.some((item) => jsonEqual(item, source))).map((source) => problem("line", `origin of ${sourceKey(source)} was dropped or rewritten`)),
    ...lineSources.filter((item) => !sources.some((source) => jsonEqual(item, source))).map((item) => problem("line", `origin of ${sourceKey(item)} is not one of the grouped sources (invented or rewritten)`)),
    ...lineSources.filter((item, index) => lineSources.findIndex((other) => jsonEqual(other, item)) !== index).map((item) => problem("line", `origin of ${sourceKey(item)} is duplicated`)),
  ];
};
