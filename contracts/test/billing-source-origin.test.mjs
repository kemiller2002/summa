// INV-PROV-001..008: executable checks for the receiver-owned billing source origin contract.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { CONTRACT_NAME, groupingProblems, schema, sourceOriginProblems } from "../billing-source-origin.mjs";
import { foreignExecutionKey } from "../praxis-provenance/praxis-provenance-record.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "fixtures", "billing-source-origin", "v1");
const load = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));
const manifest = load("manifest.json");

test("schema and fixtures describe the same contract", () => {
  assert.equal(schema.properties.contract.const, CONTRACT_NAME);
  assert.equal(manifest.contract, CONTRACT_NAME);
});

for (const item of manifest.cases) {
  test(`source origin ${item.file} is ${item.expect} (${item.why})`, () => {
    const problems = sourceOriginProblems(load(item.file));
    assert.equal(problems.length === 0 ? "valid" : "invalid", item.expect, JSON.stringify(problems));
  });
}

for (const item of load(manifest.grouping).cases) {
  test(`invoice line grouping ${item.name} is ${item.expect} (${item.why})`, () => {
    const problems = groupingProblems(item.sources, item.line);
    assert.equal(problems.length === 0 ? "preserved" : "destructive", item.expect, JSON.stringify(problems));
  });
}

test("INV-PROV-002: execution IDs are stored as supplied, not normalized", () => {
  const source = load("valid/foreign-execution.json");
  const before = JSON.stringify(source);
  assert.deepEqual(sourceOriginProblems(Object.freeze(source)), []);
  assert.equal(JSON.stringify(source), before);
  assert.equal(source.origin.lineage[0].origin.originExecution, "EXE-vigila.run-7");
  assert.deepEqual(foreignExecutionKey("summa", "issue-7"), { ok: true, value: "EXE-summa.issue-7" });
});

test("INV-PROV-005: nothing here depends on Praxis or Chrona being installed or reachable", () => {
  const pkg = JSON.parse(readFileSync(new URL("../../package.json", import.meta.url), "utf8"));
  const deps = { ...pkg.dependencies, ...pkg.devDependencies };
  assert.ok(!Object.keys(deps).some((name) => /praxis|chrona|ros-/i.test(name)));
});
