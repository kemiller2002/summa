// Pure tests of the hub page engine (web-hub/engine/hub.ts): no browser, no
// DOM, no network. They pin the behaviour the page had before the engine was
// extracted from web-hub/app.ts.
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  buildWorkQuery,
  createFailed,
  createInput,
  created,
  filtersCleared,
  initialState,
  registered,
  registerInput,
  repoFilterChanged,
  reposFailed,
  reposLoaded,
  requests,
  responseError,
  retainedSelection,
  statusFilterChanged,
  tagFilterChanged,
  unregisterConfirmation,
  uploadName,
  workFailed,
  workLoaded,
  type RepoEntry
} from "../../web-hub/engine/hub.ts";

const repo: RepoEntry = { id: "r1", name: "Summa", path: "/src/summa", registeredAt: "2026-01-01" };

test("each error belongs to one panel and is cleared by its own success", () => {
  const failing = createFailed(workFailed(reposFailed(initialState, "a"), "b"), "c");
  assert.deepEqual([failing.reposError, failing.listError, failing.createError], ["a", "b", "c"]);
  assert.equal(reposLoaded(failing, [repo]).reposError, null);
  assert.equal(registered(failing).reposError, null);
  assert.equal(workLoaded(failing, []).listError, null);
  assert.equal(created(failing).createError, null);
  assert.equal(created(failing).listError, "b");
});

test("filters change independently and clear together", () => {
  const filtered = statusFilterChanged(tagFilterChanged(repoFilterChanged(initialState, "r1"), "x"), "ready");
  assert.deepEqual(filtered.filter, { repo: "r1", tag: "x", status: "ready" });
  assert.deepEqual(filtersCleared(filtered).filter, { repo: "", tag: "", status: "" });
});

test("the work query carries repo, each trimmed tag and status", () => {
  assert.equal(buildWorkQuery({ repo: "", tag: "", status: "" }), "");
  assert.equal(buildWorkQuery({ repo: "r1", tag: " a, ,b", status: "ready" }), "?repo=r1&tag=a&tag=b&status=ready");
  assert.equal(requests.work({ repo: "", tag: "", status: "active" }).path, "/api/work?status=active");
});

test("requests carry the same bodies the page sent", () => {
  assert.equal(JSON.stringify(requests.register("/p", "").json), JSON.stringify({ path: "/p" }));
  assert.equal(JSON.stringify(requests.register("/p", "n").json), JSON.stringify({ path: "/p", name: "n" }));
  assert.deepEqual(requests.unregister("r 1"), { method: "DELETE", path: "/api/repos/r%201" });
  const input = { repoId: "r1", title: "t", tags: ["a", "b"], priority: "low", description: "d" };
  const asJson = requests.createWork(input, false);
  assert.equal(asJson.path, "/api/repos/r1/work");
  assert.equal(JSON.stringify(asJson.json), JSON.stringify({ title: "t", tags: ["a", "b"], priority: "low", description: "d" }));
  assert.equal(asJson.formFields, undefined);
  assert.deepEqual(requests.createWork(input, true).formFields,
    [["title", "t"], ["tags", "a,b"], ["priority", "low"], ["description", "d"]]);
});

test("input decisions: trimmed, and refused when required fields are missing", () => {
  assert.equal(registerInput({ path: "  ", name: "x" }), null);
  assert.deepEqual(registerInput({ path: " /p ", name: " n " }), { path: "/p", name: "n" });
  assert.equal(createInput({ repoId: "", title: "t", tags: "", priority: "medium", description: "" }), null);
  assert.equal(createInput({ repoId: "r1", title: "  ", tags: "", priority: "medium", description: "" }), null);
  assert.deepEqual(createInput({ repoId: "r1", title: " t ", tags: "a, b", priority: "high", description: " d " }),
    { repoId: "r1", title: "t", tags: ["a", "b"], priority: "high", description: "d" });
  assert.equal(uploadName("", "x.txt"), "x.txt");
});

test("selection survives only while its repository is registered", () => {
  assert.equal(retainedSelection([repo], "r1"), "r1");
  assert.equal(retainedSelection([repo], "gone"), null);
  assert.equal(retainedSelection([], ""), null);
});

test("messages are the ones the page showed", () => {
  assert.equal(responseError(400, { error: "not a ROS repository" }), "not a ROS repository");
  assert.equal(responseError(502, "x"), "request failed (502)");
  assert.equal(unregisterConfirmation(repo),
    "Unregister Summa (/src/summa)? This only removes it from the hub -- the repository itself is unaffected.");
});
