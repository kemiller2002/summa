// Pure tests of the work-backlog page engine (web/engine/backlog.ts): no
// browser, no DOM, no network. They pin the behaviour the page had before the
// engine was extracted from web/app.ts.
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  addInput,
  buildQuery,
  completionInput,
  detailView,
  filtersCleared,
  formatSize,
  initialState,
  repositoryLabel,
  requestFailed,
  requests,
  responseError,
  rowActions,
  rowsLoaded,
  rowToggled,
  statusFilterChanged,
  tagFilterChanged,
  updateDefaults,
  updateInput,
  uploadName,
  type WorkRow
} from "../../web/engine/backlog.ts";

const row = (overrides: Partial<WorkRow> = {}): WorkRow => ({
  id: "WI-0001",
  title: "Write docs",
  description: null,
  tags: ["docs", "web"],
  priority: null,
  status: "captured",
  backlogActions: [],
  attachments: [],
  liveWorkItem: null,
  ...overrides
});

test("transitions replace only the fields they own", () => {
  const loaded = rowsLoaded(requestFailed(initialState, "boom"), [row()]);
  assert.equal(loaded.error, null);
  assert.equal(loaded.rows.length, 1);
  assert.equal(requestFailed(loaded, "nope").error, "nope");
  assert.deepEqual(requestFailed(loaded, "nope").rows, loaded.rows);
});

test("selecting a row toggles it", () => {
  const selected = rowToggled(initialState, "WI-0001");
  assert.equal(selected.selectedId, "WI-0001");
  assert.equal(rowToggled(selected, "WI-0001").selectedId, null);
  assert.equal(rowToggled(selected, "WI-0002").selectedId, "WI-0002");
});

test("filters change independently and clear together", () => {
  const filtered = statusFilterChanged(tagFilterChanged(initialState, "a, b"), "ready");
  assert.deepEqual(filtered.filter, { tag: "a, b", status: "ready" });
  assert.deepEqual(filtersCleared(filtered).filter, { tag: "", status: "" });
});

test("the list query carries each trimmed tag and the status", () => {
  assert.equal(buildQuery({ tag: "", status: "" }), "");
  assert.equal(buildQuery({ tag: "   ", status: "" }), "");
  assert.equal(buildQuery({ tag: " wasm, ,state ", status: "ready" }), "?tag=wasm&tag=state&status=ready");
  assert.equal(requests.list({ tag: "x", status: "" }).path, "/api/work?tag=x");
});

test("requests name the item path and carry the same JSON the page sent", () => {
  assert.deepEqual(requests.block("WI 1", "waiting"), { method: "POST", path: "/api/work/WI%201/block", json: { reason: "waiting" } });
  assert.deepEqual(requests.ready("WI-1"), { method: "POST", path: "/api/work/WI-1/ready" });
  assert.equal(
    JSON.stringify(requests.add({ title: "t", description: "d", tags: ["a"], priority: "high" }).json),
    JSON.stringify({ title: "t", tags: ["a"], priority: "high", description: "d" })
  );
  assert.equal(
    JSON.stringify(requests.complete("WI-1", { evidence: [{ type: "tests", path: "a.ts" }], conclusion: null }).json),
    JSON.stringify({ evidence: [{ type: "tests", path: "a.ts" }], conclusion: null })
  );
  assert.deepEqual(requests.uploadAttachments("WI-1"), { method: "POST", path: "/api/work/WI-1/attachments" });
});

test("a failed response reports the server's error, else the status", () => {
  assert.equal(responseError(400, { error: "illegal transition" }), "illegal transition");
  assert.equal(responseError(500, null), "request failed (500)");
  assert.equal(responseError(404, { message: "x" }), "request failed (404)");
});

test("a row offers live actions when in flight, backlog actions otherwise", () => {
  assert.deepEqual(rowActions(row({ backlogActions: ["ready", "block", "start", "abandon", "unknown"] })).map((a) => a.label),
    ["Show", "Mark ready", "Block", "Start", "Abandon", "Edit", "Attach"]);
  assert.deepEqual(
    rowActions(row({ backlogActions: ["ready"], liveWorkItem: { state: "active", semanticState: "active", allowedActions: ["complete", "block"] } }))
      .map((a) => a.kind),
    ["show", "complete", "block", "edit", "attach"]
  );
});

test("input becomes a submission: trimmed, tags parsed, empty title refused", () => {
  assert.equal(addInput({ title: "  ", description: "", tags: "", priority: "medium" }), null);
  assert.deepEqual(addInput({ title: " T ", description: " d ", tags: "a, ,b", priority: "low" }),
    { title: "T", description: "d", tags: ["a", "b"], priority: "low" });
  const existing = row({ title: "Original", description: "desc", priority: "high" });
  assert.deepEqual(updateDefaults(existing), { title: "Original", description: "desc", tags: "docs, web", priority: "high" });
  assert.equal(updateDefaults(row()).priority, "medium");
  assert.equal(updateInput({ title: null, description: "", tags: "", priority: "medium" }, existing).title, "Original");
  assert.equal(updateInput({ title: "  New ", description: "", tags: "", priority: "medium" }, existing).title, "New");
  assert.deepEqual(completionInput([{ type: " tests ", path: " a.ts " }, { type: "x", path: "" }, { type: "", path: "" }], "  "),
    { evidence: [{ type: "tests", path: "a.ts" }], conclusion: null });
  assert.equal(completionInput([], " done ").conclusion, "done");
  assert.equal(uploadName("  ", "a.pdf"), "a.pdf");
  assert.equal(uploadName(" spec ", "a.pdf"), "spec");
});

test("the detail panel projects the selected row", () => {
  assert.equal(detailView(initialState), null);
  const withAttachment = row({
    description: "",
    detail: "extra",
    attachments: [{ id: "f/1", name: "a.pdf", size: 2048, contentType: null, uploadedAt: "2026-01-01" }]
  });
  const view = detailView(rowToggled(rowsLoaded(initialState, [withAttachment]), "WI-0001"));
  assert.ok(view);
  assert.equal(view.description, "No description.");
  assert.deepEqual(view.attachments, [{ href: "/api/work/WI-0001/attachments/f%2F1", name: "a.pdf", sizeLabel: " (2.0 KB)" }]);
  assert.equal(view.raw, `${JSON.stringify(withAttachment, null, 2)}\n\nextra`);
  assert.equal(detailView(rowToggled(rowsLoaded(initialState, [row()]), "missing")), null);
});

test("sizes and the repository label format as before", () => {
  assert.equal(formatSize(512), "512 B");
  assert.equal(formatSize(1536), "1.5 KB");
  assert.equal(formatSize(3 * 1024 * 1024), "3.0 MB");
  assert.equal(repositoryLabel({ repository: "summa", protocolVersion: "1.0.0", validation: "passed" }),
    "summa · protocol 1.0.0 · validation passed");
});
