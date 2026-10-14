// Local mirror of Forma's reference UI conformance helpers, for Summa's
// browser suite (summa#90, WI-0053).
//
// Source: kemiller2002/forma tests/site-browser/ui-conformance-helpers.mjs on
// branch feat/universal-ui-conformance-2026-10 at d327d30 (not yet released
// in a Forma package). The exported API is kept identical so this file can be
// replaced by an import from the Forma package once it ships the helpers;
// change nothing here except to follow that reference.
//
// Contract: visual-engineering framework/standards/UI-LAYOUT-INTERACTION.md,
// UI-LAYOUT-INTERACTION@1.0.0 (rule IDs ULI-L01..L13, ULI-A01..A10).
import { expect } from "@playwright/test";

export const STATUS = Object.freeze({
  PASS: "PASS",
  FAIL: "FAIL",
  UNKNOWN: "UNKNOWN"
});

export const DEFAULT_TOLERANCE_CSS_PX = 1;
export const DEFAULT_TIMEOUT_MS = 5000;

const EDGES = Object.freeze({
  top: box => box.y,
  bottom: box => box.y + box.height,
  center: box => box.y + box.height / 2,
  left: box => box.x,
  right: box => box.x + box.width
});

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

const describeTarget = ({ role, name }) => `${role} "${name instanceof RegExp ? name.source : name}"`;

const requireSemanticTarget = (target, label) => {
  if (!target?.role || target.name === undefined || target.name === "") {
    throw new Error(`${label} must specify both accessible role and name.`);
  }
  return target;
};

export const edgeCoordinate = (box, edge) => {
  const read = EDGES[edge];
  if (!read) throw new Error(`Unsupported alignment edge "${edge}". Use one of: ${Object.keys(EDGES).join(", ")}.`);
  return read(box);
};

// Classify one raw observation of a declared peer. Only a single, rendered,
// non-zero box can contribute a coordinate; everything else is UNKNOWN.
export const classifyPeerObservation = ({ peer, count, box }) => {
  const element = describeTarget(peer);
  if (count === 0) return { element, status: STATUS.UNKNOWN, reason: "element not found in the accessibility tree (missing or hidden)" };
  if (count > 1) return { element, status: STATUS.UNKNOWN, reason: `accessible name is ambiguous (${count} matches)` };
  if (!box) return { element, status: STATUS.UNKNOWN, reason: "no bounding box (not rendered)" };
  if (!(box.width > 0) || !(box.height > 0)) {
    return { element, status: STATUS.UNKNOWN, reason: `zero-size box (${box.width}x${box.height})`, box };
  }
  return { element, status: STATUS.PASS, box };
};

export const validateAlignmentGroup = group => {
  const { ruleId = "ULI-L01", id, peers, edge = "top", toleranceCssPx = DEFAULT_TOLERANCE_CSS_PX } = group ?? {};
  if (!id) throw new Error("A declared alignment group requires an id.");
  if (!Array.isArray(peers) || peers.length < 2) throw new Error(`Alignment group ${id} requires at least two declared peers.`);
  if (!/^ULI-L\d{2}$/.test(ruleId)) throw new Error(`Alignment group ${id} must cite a ULI-L rule ID.`);
  if (!(toleranceCssPx >= 0)) throw new Error(`Alignment group ${id} tolerance must be a non-negative number.`);
  peers.forEach((peer, index) => requireSemanticTarget(peer, `Peer ${index + 1} of ${id}`));
  edgeCoordinate({ x: 0, y: 0, width: 0, height: 0 }, edge);
  return Object.freeze({ ruleId, id, peers, edge, toleranceCssPx });
};

// Decide a declared group's status from observations. Missing evidence is
// UNKNOWN, never PASS; a measured delta above tolerance is FAIL.
export const evaluateAlignment = (group, observations) => {
  const { ruleId, id, edge, toleranceCssPx } = validateAlignmentGroup(group);
  const classified = observations.map(classifyPeerObservation);
  const measurements = classified.map(item =>
    item.status === STATUS.PASS ? { ...item, coordinate: edgeCoordinate(item.box, edge) } : item);
  const base = { ruleId, group: id, edge, toleranceCssPx, measurements };
  const unknown = measurements.filter(item => item.status !== STATUS.PASS);
  if (unknown.length > 0) {
    return { ...base, status: STATUS.UNKNOWN, deltaCssPx: null, reason: unknown.map(item => `${item.element}: ${item.reason}`).join("; ") };
  }
  const coordinates = measurements.map(item => item.coordinate);
  const deltaCssPx = Math.max(...coordinates) - Math.min(...coordinates);
  const status = deltaCssPx <= toleranceCssPx ? STATUS.PASS : STATUS.FAIL;
  return {
    ...base,
    status,
    deltaCssPx,
    reason: `${edge}-edge delta ${deltaCssPx.toFixed(2)} CSS px ${status === STATUS.PASS ? "<=" : ">"} tolerance ${toleranceCssPx} CSS px`
  };
};

export const formatReport = report => {
  const details = (report.measurements ?? [])
    .map(item => `  - ${item.element}: ${item.status}${item.coordinate !== undefined ? ` ${report.edge}=${item.coordinate.toFixed(2)}px` : ""}${item.reason ? ` (${item.reason})` : ""}`)
    .join("\n");
  const subject = report.group ? `group ${report.group}` : report.action;
  return `${report.ruleId} ${report.status}: ${subject}: ${report.reason}${details ? `\n${details}` : ""}`;
};

const sameObservations = (a, b) =>
  a.length === b.length && a.every((item, index) => {
    const other = b[index];
    if (item.count !== other.count) return false;
    if (!item.box || !other.box) return item.box === other.box;
    return ["x", "y", "width", "height"].every(key => Math.abs(item.box[key] - other.box[key]) < 0.01);
  });

const normalizeText = value => (value ?? "").replace(/\s+/g, " ").trim();

export const matchesExpected = (observed, expected) => {
  if (observed === null || observed === undefined) return false;
  if (expected instanceof RegExp) return expected.test(String(observed));
  return observed === expected;
};

const OUTCOME_KINDS = Object.freeze({
  text: { needsTarget: true, expectedType: "string-or-regexp" },
  visible: { needsTarget: true, expectedType: "boolean" },
  checked: { needsTarget: true, expectedType: "boolean" },
  attribute: { needsTarget: true, expectedType: "string-or-regexp" },
  url: { needsTarget: false, expectedType: "string-or-regexp" }
});

export const validateActionContract = contract => {
  const { ruleId = "ULI-A02", action, outcome, timeoutMs = DEFAULT_TIMEOUT_MS } = contract ?? {};
  requireSemanticTarget(action, "Action control");
  if (!/^ULI-A\d{2}$/.test(ruleId)) throw new Error("An action assertion must cite a ULI-A rule ID.");
  const kind = OUTCOME_KINDS[outcome?.kind];
  if (!kind) throw new Error(`An action requires an observable outcome kind: ${Object.keys(OUTCOME_KINDS).join(", ")}.`);
  if (kind.needsTarget) requireSemanticTarget(outcome.target, `Outcome target for ${outcome.kind}`);
  if (outcome.kind === "attribute" && !outcome.attribute) throw new Error("Attribute outcome requires an attribute name.");
  const typeOk = kind.expectedType === "boolean"
    ? typeof outcome.expected === "boolean"
    : typeof outcome.expected === "string" || outcome.expected instanceof RegExp;
  if (!typeOk) throw new Error(`Outcome ${outcome.kind} requires an explicit expected ${kind.expectedType} post-state.`);
  if (!(timeoutMs > 0 && Number.isFinite(timeoutMs))) throw new Error("Action assertions require a bounded positive timeoutMs.");
  return Object.freeze({ ruleId, action, outcome, timeoutMs });
};

// Decide whether an action can demonstrate a transition at all. Returns a
// report when it cannot (UNKNOWN for missing evidence, FAIL when the expected
// post-state is already present before activation), otherwise null.
export const evaluateActionPremise = ({ ruleId, action, outcome, pre, controlIssue }) => {
  const base = { ruleId, action: describeTarget(action), outcome: outcome.kind, expected: String(outcome.expected), pre, post: null };
  if (controlIssue) return { ...base, status: STATUS.UNKNOWN, reason: controlIssue };
  if (pre.missing && outcome.kind !== "visible") return { ...base, status: STATUS.UNKNOWN, reason: "outcome target not found before activation" };
  if (pre.count > 1) return { ...base, status: STATUS.UNKNOWN, reason: `outcome target name is ambiguous (${pre.count} matches)` };
  if (matchesExpected(pre.value, outcome.expected)) {
    return { ...base, status: STATUS.FAIL, reason: "pre-state already satisfies the expected post-state; no transition can be demonstrated" };
  }
  return null;
};

// Decide an action's status from pre/post observations. An action passes only
// when the pre-state did NOT already satisfy the expectation, the expected
// post-state was observed, and the observable value actually changed.
export const evaluateActionOutcome = ({ ruleId, action, outcome, pre, post, controlIssue }) => {
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;
  const base = { ruleId, action: describeTarget(action), outcome: outcome.kind, expected: String(outcome.expected), pre, post };
  if (!post) return { ...base, status: STATUS.FAIL, reason: "no post-state observed" };
  if (!matchesExpected(post.value, outcome.expected)) {
    return { ...base, status: STATUS.FAIL, reason: "expected post-state never appeared within the timeout" };
  }
  if (post.value === pre.value) return { ...base, status: STATUS.FAIL, reason: "post-state equals pre-state" };
  return { ...base, status: STATUS.PASS, reason: "expected post-state observed after activation" };
};

// ---------------------------------------------------------------------------
// Browser adapters (observation only)
// ---------------------------------------------------------------------------

const semantic = (page, { role, name }) => page.getByRole(role, { name, exact: typeof name === "string" });

// Wait until web fonts have loaded and two animation frames have elapsed.
export const settleFontsAndFrames = page =>
  page.evaluate(() => document.fonts.ready.then(() =>
    new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))));

const observePeer = async (page, peer, timeoutMs) => {
  const locator = semantic(page, peer);
  const count = await locator.count();
  const box = count === 1 ? await locator.boundingBox({ timeout: timeoutMs }) : null;
  return { peer, count, box };
};

const observePeers = (page, peers, timeoutMs) => Promise.all(peers.map(peer => observePeer(page, peer, timeoutMs)));

// Re-read until two consecutive frame-separated observations agree, bounded
// by a deadline. Each retry waits for real animation frames, not a fixed sleep.
const readStableObservations = async (page, peers, timeoutMs, deadline, previous) => {
  await settleFontsAndFrames(page);
  const current = await observePeers(page, peers, timeoutMs);
  if (previous && sameObservations(previous, current)) return current;
  if (Date.now() >= deadline) return null;
  return readStableObservations(page, peers, timeoutMs, deadline, current);
};

// Measure a declared group once fonts are ready and layout is stable. Returns
// a report; never throws on a geometry failure. A group whose layout never
// stabilizes is UNKNOWN.
export const measureAlignment = async (page, group, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) => {
  const declared = validateAlignmentGroup(group);
  const stable = await readStableObservations(page, declared.peers, timeoutMs, Date.now() + timeoutMs, null);
  if (!stable) {
    return { ruleId: declared.ruleId, group: declared.id, edge: declared.edge, toleranceCssPx: declared.toleranceCssPx,
      measurements: [], status: STATUS.UNKNOWN, deltaCssPx: null, reason: "layout did not stabilize within the timeout" };
  }
  return evaluateAlignment(declared, stable);
};

export const expectPeerAlignment = async (page, group, options) => {
  const report = await measureAlignment(page, group, options);
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
  return report;
};

const observeOutcome = async (page, outcome, timeoutMs) => {
  if (outcome.kind === "url") return { value: page.url(), missing: false };
  const target = semantic(page, outcome.target);
  const count = await target.count();
  if (outcome.kind === "visible") {
    return { value: count === 1 ? await target.isVisible() : false, missing: count === 0, count };
  }
  if (count !== 1) return { value: null, missing: count === 0, count };
  switch (outcome.kind) {
    case "text": return { value: normalizeText(await target.textContent({ timeout: timeoutMs })), missing: false, count };
    case "checked": return { value: await target.isChecked({ timeout: timeoutMs }), missing: false, count };
    case "attribute": return { value: await target.getAttribute(outcome.attribute, { timeout: timeoutMs }), missing: false, count };
    default: throw new Error(`Unsupported observable outcome: ${outcome.kind}`);
  }
};

const inspectControl = async control => {
  const count = await control.count();
  if (count === 0) return "action control not found";
  if (count > 1) return `action control name is ambiguous (${count} matches)`;
  if (!(await control.isVisible())) return "action control is not visible";
  if (!(await control.isEnabled())) return "action control is disabled";
  return null;
};

// Capture pre-state, activate the control through its role/name, wait for
// the explicitly expected post-state with Playwright auto-waiting bounded by
// timeoutMs, and return a report. No coordinates and no fixed sleeps.
export const runActionAssertion = async (page, contract) => {
  const { ruleId, action, outcome, timeoutMs } = validateActionContract(contract);
  const control = semantic(page, action);
  const controlIssue = await inspectControl(control);
  const pre = await observeOutcome(page, outcome, timeoutMs);
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;

  await control.click({ timeout: timeoutMs });

  const appeared = await expect(async () => {
    const observed = await observeOutcome(page, outcome, timeoutMs);
    expect(matchesExpected(observed.value, outcome.expected), `observed ${JSON.stringify(observed.value)}`).toBe(true);
  }).toPass({ timeout: timeoutMs }).then(() => true, () => false);
  const post = await observeOutcome(page, outcome, timeoutMs);
  return { ...evaluateActionOutcome({ ruleId, action, outcome, pre, post }), waitedForExpected: appeared };
};

export const expectActionOutcome = async (page, contract) => {
  const report = await runActionAssertion(page, contract);
  expect(report.status, `${formatReport(report)} (pre=${JSON.stringify(report.pre?.value)}, post=${JSON.stringify(report.post?.value)})`).toBe(STATUS.PASS);
  return report;
};

// Diagnostic inventory, NOT a no-op detector. It finds candidate enabled
// controls requiring explicit scenario contracts; it does not claim that
// matching an element proves its implementation.
export const inventoryVisibleActions = page =>
  page.locator('button:not([disabled]), input[type="button"]:not([disabled]), input[type="submit"]:not([disabled]), a[role="button"]:not([aria-disabled="true"])')
    .evaluateAll(elements => elements.filter(el => {
      const rect = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    }).map(el => ({
      tag: el.tagName.toLowerCase(),
      nameHint: el.getAttribute("aria-label") || el.textContent?.trim() || el.getAttribute("value") || "",
      type: el.getAttribute("type") || "",
      url: el.getAttribute("href") || null
    })));
