// billing.record provenance (INV-PROV-001..004, INV-PROV-008).
import test from "node:test";
import assert from "node:assert/strict";
import {
  receiveBillingRecord, deriveBillingRecord, sourceFromChronaEntry, appendBillingContribution,
  originatingExecutions, provenanceStatus, SUMMA_ACTOR,
} from "../lib/billing-record-provenance.mjs";
import { classify, originator, modifiers, preservationViolations, withRole } from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const AGENT = { kind: "agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" };
const OTHER = { kind: "agent", id: "anthropic/claude-code", provider: "anthropic", model: "unknown", runtime: "claude-code" };
const HUMAN = { kind: "human", id: "kevin" };
const EXE1 = "EXE-20260926T080000000Z-a1a1a1a1";
const EXE2 = "EXE-20260926T100000000Z-a2a2a2a2";
const EXE_BILLER = "EXE-20260927T090000000Z-b1b1b1b1";
const t = (hour) => `2026-09-26T${String(hour).padStart(2, "0")}:00:00.000Z`;

// A Chrona time entry as published (Chrona CHR-PROV-007): performer, execution, provenance.
const chronaEntry = (overrides = {}) => ({
  entryId: "ACT-1",
  observationId: "OBS-1",
  sourceSystem: "praxis",
  workItemId: "praxis:FEAT-ECHELON-PROVENANCE",
  actorId: AGENT.id,
  actor: AGENT,
  execution: EXE1,
  exactDurationSeconds: 2700,
  disposition: "Accepted",
  provenance: {
    schema: "praxis.provenance/1",
    contributions: {
      [EXE1]: { operations: ["created"], at: t(9), actor: AGENT, reason: "time.record: TimeObservation received" },
      "EXT-chrona.op-2": { operations: ["transformed"], at: t(9), actor: { kind: "automation", id: "echelon/chrona", provider: "echelon", model: "unknown", runtime: "chrona" } },
      "CTB-20260926-5f2e19aa": { operations: ["approved"], at: t(10), actor: HUMAN },
    },
    derivedFrom: ["chrona:observation/OBS-1", "chrona:candidate/OBS-1"],
  },
  ...overrides,
});

const derive = (sources, creator) => deriveBillingRecord({ billingRecordId: "BR-1", sources, creator, operationId: "bill-1", at: t(12) });

test("a billing record derived from an agent time entry keeps the originating execution", () => {
  const entry = chronaEntry();
  const { record } = derive([sourceFromChronaEntry(entry)]);
  const [source] = record.sources;
  assert.equal(source.performer.execution, EXE1);
  assert.deepEqual(source.performer.actor, AGENT);
  assert.deepEqual(source.provenance, entry.provenance, "the source's provenance is carried verbatim");
  assert.deepEqual(originatingExecutions(record), [EXE1]);
  assert.ok(record.provenance.derivedFrom.includes("chrona:entry/ACT-1"));
  assert.ok(record.provenance.derivedFrom.includes("praxis:FEAT-ECHELON-PROVENANCE"));
  assert.equal(classify(record.provenance).verdict, "supported");
  assert.deepEqual(originator(record.provenance).actor, SUMMA_ACTOR, "Summa generated the record; the agent is lineage, not its author");
  assert.equal(originator(record.provenance).key, "EXT-summa.bill-1");
});

test("grouping several executions keeps each one distinct (two executions of one agent)", () => {
  const one = sourceFromChronaEntry(chronaEntry());
  const two = sourceFromChronaEntry(chronaEntry({ entryId: "ACT-2", execution: EXE2, provenance: { schema: "praxis.provenance/1", contributions: { [EXE2]: { operations: ["created"], at: t(11), actor: AGENT } } } }));
  const { record } = derive([one, two]);
  assert.deepEqual(originatingExecutions(record), [EXE1, EXE2]);
  assert.equal(record.sources.length, 2);
});

test("missing Praxis provenance: still valid and billable, provenance unknown", () => {
  const { actor, execution, provenance, ...plain } = chronaEntry();
  const { record } = derive([sourceFromChronaEntry(plain)]);
  const received = receiveBillingRecord(record);
  assert.equal(received.ok, true);
  assert.equal(received.billable, true);
  assert.deepEqual(received.sources, [{ ref: "chrona:entry/ACT-1", provenance: "unknown" }]);
  assert.deepEqual(originatingExecutions(record), []);

  const legacy = receiveBillingRecord({ schemaVersion: "summa.billing-record/1", billingRecordId: "BR-0", sources: [{ ref: "manual:line/1" }] });
  assert.equal(legacy.ok, true);
  assert.equal(legacy.billable, true);
  assert.equal(legacy.provenance, "unknown");
  assert.equal(provenanceStatus(undefined), "unknown");
});

test("malformed provenance is rejected at the boundary with a clear error", () => {
  const broken = { contributions: { "CTB-1": { operations: ["created"], at: t(9), actor: AGENT } } };
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const onSource = receiveBillingRecord({ ...record, sources: [{ ...record.sources[0], provenance: broken }] });
  assert.equal(onSource.ok, false);
  assert.equal(onSource.errors[0].code, "malformed-provenance");
  assert.match(onSource.errors[0].message, /^sources\[0\]\.provenance: /);
  const onRecord = receiveBillingRecord({ ...record, provenance: { schema: "praxis.provenance/1.1", contributions: {} } });
  assert.equal(onRecord.ok, false);
  assert.equal(onRecord.errors[0].code, "malformed-provenance");
  const leaked = receiveBillingRecord({ ...record, note: "sk-abcdefghijklmnopqrstuvwxyz0123" });
  assert.equal(leaked.errors[0].code, "credential");
  assert.equal(receiveBillingRecord({ ...record, sources: [{ ref: "" }] }).errors[0].code, "invalid-billing-record");
  assert.equal(receiveBillingRecord({ ...record, schemaVersion: "summa.billing-record/2" }).errors[0].code, "unsupported-billing-version");
});

test("an unsupported provenance major is carried verbatim and never appended to", () => {
  const future = { schema: "praxis.provenance/2", history: [{ who: "x" }] };
  const { record } = derive([sourceFromChronaEntry(chronaEntry({ provenance: future }))]);
  assert.deepEqual(record.sources[0].provenance, future);
  const received = receiveBillingRecord(record);
  assert.equal(received.ok, true);
  assert.deepEqual(received.sources[0].provenance, "unsupported");
  const wholeRecord = { ...record, provenance: future };
  assert.equal(receiveBillingRecord(wholeRecord).provenance, "unsupported");
  assert.equal(appendBillingContribution(wholeRecord, "CTB-1", { operations: ["approved"], at: t(13), actor: HUMAN }).ok, false);
});

test("agent actions are never recorded as human: an agent-created record stays agent-originated after human approval", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())], { actor: OTHER, execution: EXE_BILLER });
  assert.equal(originator(record.provenance).actor.kind, "agent");
  assert.equal(originator(record.provenance).key, EXE_BILLER);
  const approved = appendBillingContribution(record, "CTB-20260927-5f2e19aa", { operations: ["approved"], at: t(13), actor: HUMAN });
  assert.ok(approved.ok);
  assert.equal(originator(approved.record.provenance).actor.kind, "agent");
  assert.deepEqual(withRole(approved.record.provenance, "approved").map((item) => item.actor.id), ["kevin"]);
  assert.deepEqual(preservationViolations(record.provenance, approved.record.provenance), []);
  const forged = appendBillingContribution(record, EXE_BILLER, { operations: ["modified"], at: t(14), actor: HUMAN });
  assert.equal(forged.ok, false, "an agent's execution can never be re-attributed to a human");
  assert.match(forged.error, /refusing to re-attribute/);
});

test("an agent creator without a known execution is keyed by the operation, not invented", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())], { actor: OTHER });
  assert.deepEqual(Object.keys(record.provenance.contributions), ["EXT-op.bill-1"]);
});

test("a human correction of an agent-derived line is a separate modification", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())], { actor: OTHER, execution: EXE_BILLER });
  const corrected = appendBillingContribution(record, "CTB-20260927-5f2e19aa", { operations: ["modified"], at: t(15), actor: HUMAN, reason: "rate corrected" });
  assert.deepEqual(modifiers(corrected.record.provenance).map((item) => item.actor.id), ["kevin"]);
});

test("round trip through JSON preserves everything", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const wire = JSON.parse(JSON.stringify(record));
  const received = receiveBillingRecord(wire);
  assert.deepEqual(received.record, record);
  assert.deepEqual(preservationViolations(record.provenance, received.record.provenance), []);
});

test("billing validity never depends on who the actor is", () => {
  for (const actor of [AGENT, HUMAN, SUMMA_ACTOR, { kind: "unknown", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" }]) {
    const source = sourceFromChronaEntry(chronaEntry({ actor, execution: actor.kind === "human" ? undefined : EXE1, provenance: undefined }));
    assert.equal(receiveBillingRecord(derive([source]).record).billable, true, actor.kind);
  }
});
