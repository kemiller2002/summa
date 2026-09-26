// billing.record provenance (INV-PROV-001..004, INV-PROV-008, INV-PROV-012).
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  receiveBillingRecord, receiveBillingRecordText, deriveBillingRecord, sourceFromChronaEntry, appendBillingContribution,
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
      [EXE1]: { operations: ["created", "measured"], at: t(9), actor: AGENT, reason: "time.record: TimeObservation recorded" },
      "EXT-chrona.op-2": { operations: ["transformed"], at: t(9), actor: { kind: "automation", id: "echelon/chrona", provider: "echelon", model: "unknown", runtime: "chrona" } },
      "CTB-20260926-5f2e19aa": { operations: ["approved"], at: t(10), actor: HUMAN },
    },
    derivedFrom: ["vigila:item/IT-4", "praxis:observation/OBS-1"],
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
  assert.deepEqual(record.provenance.derivedFrom, ["vigila:item/IT-4", "praxis:observation/OBS-1", "chrona:entry/ACT-1", "praxis:FEAT-ECHELON-PROVENANCE"], "the source's lineage, then the source and work item");
  assert.deepEqual(Object.keys(record.provenance.contributions), ["EXT-summa.bill-1"], "billing.record is a create: the source's contributors are not authors of the billing record");
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

test("source blocks are kept verbatim and never appended to", () => {
  const entry = chronaEntry();
  const { record } = derive([sourceFromChronaEntry(entry)]);
  const approved = appendBillingContribution(record, "CTB-20260927-5f2e19aa", { operations: ["approved"], at: t(13), actor: HUMAN });
  assert.deepEqual(approved.record.sources[0].provenance, entry.provenance);
  assert.deepEqual(receiveBillingRecord(approved.record).record.sources[0].provenance, entry.provenance);
});

test("regression: a Bearer credential in a contribution reason is refused and nothing is stored", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const before = JSON.stringify(record);
  const result = appendBillingContribution(record, "CTB-20260927-5f2e19aa", { operations: ["approved"], at: t(13), actor: HUMAN, reason: "Bearer abcdefghijklmnopqrstuvwxyz0123456789" });
  assert.equal(result.ok, false);
  assert.match(result.error, /credential/);
  assert.equal(result.record, undefined);
  assert.equal(JSON.stringify(record), before);
});

test("regression: a contribution dated before the record's creation is refused", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const result = appendBillingContribution(record, "CTB-20260926-5f2e19aa", { operations: ["approved"], at: t(11), actor: HUMAN });
  assert.equal(result.ok, false);
  assert.equal(result.record, undefined);
});

test("regression: a late created and an unknown actor extending a known entry are refused", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())], { actor: OTHER, execution: EXE_BILLER });
  assert.equal(appendBillingContribution(record, "CTB-20260927-5f2e19aa", { operations: ["created"], at: t(13), actor: HUMAN }).ok, false);
  const unknown = { kind: "agent", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" };
  assert.equal(appendBillingContribution(record, EXE_BILLER, { operations: ["modified"], at: t(13), actor: unknown }).ok, false);
});

test("regression: a credential in the declared creator is refused", () => {
  const result = derive([sourceFromChronaEntry(chronaEntry())], { actor: { kind: "human", id: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } });
  assert.equal(result.ok, false);
  assert.match(result.error, /credential/);
});

test("contract 1.1: null is never absence; calendar-invalid times are refused; keys are escaped injectively", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  assert.equal(receiveBillingRecord({ ...record, provenance: null }).errors[0].code, "malformed-provenance");
  assert.equal(receiveBillingRecord({ ...record, sources: [{ ...record.sources[0], provenance: null }] }).errors[0].code, "malformed-provenance");
  assert.equal(receiveBillingRecord({ ...record, sources: [{ ...record.sources[0], workItemId: null }] }).errors[0].code, "invalid-billing-record");
  assert.equal(receiveBillingRecord({ ...record, sources: [{ ...record.sources[0], performer: null }] }).errors[0].code, "invalid-billing-record");
  assert.equal(deriveBillingRecord({ billingRecordId: "BR-2", sources: [], operationId: "b-1", at: "2026-02-30T00:00:00.000Z" }).ok, false);
  const spaced = deriveBillingRecord({ billingRecordId: "BR-3", sources: [], operationId: "bill 1", at: t(12) });
  assert.deepEqual(Object.keys(spaced.record.provenance.contributions), ["EXT-summa.bill_201"]);
  const agentOp = deriveBillingRecord({ billingRecordId: "BR-4", sources: [], creator: { actor: OTHER }, operationId: "op_1", at: t(12) });
  assert.deepEqual(Object.keys(agentOp.record.provenance.contributions), ["EXT-op.op_5f1"]);
});

// ---- contract revision 1.2 (INV-PROV-012) -----------------------------------

const TOKEN = "ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

test("contract 1.2 rule 3: lineage taken from the sources is checked; a refusal rejects the record", () => {
  // A surrogate in a source ref is refused by addLineage: nothing is returned to store.
  const lone = derive([{ ...sourceFromChronaEntry(chronaEntry()), ref: "chrona:entry/\ud800" }]);
  assert.equal(lone.ok, false);
  assert.equal(lone.record, undefined);
  assert.match(lone.error, /^derivedFrom: .*unpaired UTF-16 surrogate/);
  // A credential in a work item reference never reaches derivedFrom.
  const leak = derive([{ ...sourceFromChronaEntry(chronaEntry()), workItemId: TOKEN }]);
  assert.equal(leak.ok, false);
  assert.match(leak.error, /credential/);
  // A blank work item is refused, never dropped silently.
  const blank = derive([{ ...sourceFromChronaEntry(chronaEntry()), workItemId: " \t" }]);
  assert.equal(blank.ok, false);
  assert.match(blank.error, /^derivedFrom: /);
  // Duplicates across sources are dropped, first occurrence kept.
  const { record } = derive([sourceFromChronaEntry(chronaEntry()), sourceFromChronaEntry(chronaEntry({ entryId: "ACT-2" }))]);
  assert.deepEqual(record.provenance.derivedFrom, ["vigila:item/IT-4", "praxis:observation/OBS-1", "chrona:entry/ACT-1", "praxis:FEAT-ECHELON-PROVENANCE", "chrona:entry/ACT-2"]);
});

test("contract 1.2 finding 6: billing keys use the vendored per-code-point escaping", () => {
  const keyOf = (operationId, creator) => Object.keys(deriveBillingRecord({ billingRecordId: "BR-9", sources: [], creator, operationId, at: t(12) }).record.provenance.contributions)[0];
  assert.equal(keyOf("bill-\u{1F600}"), "EXT-summa.bill-_f0_9f_98_80");
  assert.notEqual(keyOf("bill-\u{1F600}"), keyOf("bill-\u{1F601}"));
  assert.equal(keyOf("bill.1"), "EXT-summa.bill_2e1");
  assert.equal(keyOf("op.1", { actor: OTHER }), "EXT-op.op_2e1");
  for (const operationId of ["", "bill-\udc00"]) {
    const result = deriveBillingRecord({ billingRecordId: "BR-9", sources: [], operationId, at: t(12) });
    assert.equal(result.ok, false);
    assert.match(result.error, /cannot form a contribution key/);
  }
});

test("contract 1.2 rule 1: a billing record received as JSON text is classified as text", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const received = receiveBillingRecordText(JSON.stringify(record));
  assert.ok(received.ok, JSON.stringify(received.errors));
  assert.deepEqual(received.record, record);
  // A duplicated contribution key inside a source block, which JSON.parse would resolve silently.
  const text = JSON.stringify(record).replace('"EXT-chrona.op-2":', `"${EXE1}":{"operations":["modified"],"at":"${t(9)}","actor":{"kind":"human","id":"mallory"}},"EXT-chrona.op-2":`);
  const duplicated = receiveBillingRecordText(text);
  assert.equal(duplicated.ok, false);
  assert.equal(duplicated.errors[0].code, "malformed-text");
  assert.match(duplicated.errors[0].message, /^sources\[0\]\.provenance\.contributions\.EXE-.*: member name repeated/);
  const surrogate = receiveBillingRecordText(JSON.stringify(record).replace('"BR-1"', '"BR-\\ud800"'));
  assert.equal(surrogate.errors[0].code, "malformed-text");
  assert.equal(receiveBillingRecordText("{").errors[0].code, "malformed-text");
  assert.equal(receiveBillingRecordText(JSON.stringify({ ...record, note: TOKEN })).errors[0].code, "credential");
  // Every text fixture as the record's own provenance member.
  const fixture = JSON.parse(readFileSync(new URL("../vendor/praxis-provenance/fixtures/text-cases.json", import.meta.url), "utf8"));
  for (const item of fixture.cases) {
    const { provenance, ...rest } = record;
    const embedded = JSON.stringify(rest).replace(/}$/, `,"provenance":${item.text}}`);
    let valid = true;
    try { JSON.parse(embedded); } catch { valid = false; }
    const result = receiveBillingRecordText(embedded);
    assert.equal(result.ok, valid && item.expect !== "malformed", `${item.name}: ${JSON.stringify(result.errors)}`);
  }
});

test("contract 1.2 rule 6: a stored null provenance is malformed, never read as absent", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  const result = appendBillingContribution({ ...record, provenance: null }, "CTB-20260927-00000001", { operations: ["approved"], at: t(13), actor: HUMAN });
  assert.equal(result.ok, false);
  assert.equal(result.record, undefined);
  assert.equal(provenanceStatus(null), "malformed");
});

test("contract 1.2 rule 2: only ASCII whitespace is blank", () => {
  const { record } = derive([sourceFromChronaEntry(chronaEntry())]);
  assert.equal(receiveBillingRecord({ ...record, billingRecordId: "\u0085" }).ok, true, "U+0085 is content");
  assert.equal(receiveBillingRecord({ ...record, billingRecordId: " \t\r\n" }).errors[0].code, "invalid-billing-record");
});
