#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { observeGitStatus, requireGitText, runGitText } from "./ros_git.mjs";
import { readJson, withFileLock, writeJson, writeTextAtomic } from "./ros_persistence.mjs";
import {
  TELEMETRY_ADAPTERS,
  configuredTelemetry,
  finalizeExecution,
  finalizeWorkExecutions,
  ingestTelemetry,
  readTelemetryInput,
  recordTelemetryLifecycle,
  recordTelemetryMetric,
  showTelemetry,
  startExecution,
  summarizeTelemetry,
  telemetryFindings
} from "./ros_telemetry.mjs";

const ID_RE =
  /^(?:(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-(?:[0-9]{4}|[A-F0-9]{4})|RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-[A-Z0-9]+(?:-[A-Z0-9]+)*)$/;
const REFERENCE_FIELDS = new Set([
  "contradicts",
  "contradicting_evidence",
  "depends_on",
  "derived_from",
  "evidence_ids",
  "hypothesis_ids",
  "related_documents",
  "related_mission",
  "related_package",
  "related_theories",
  "supporting_evidence",
  "supports",
  "superseded_by",
  "supersedes",
  "tests_hypotheses",
  "theory_ids"
]);
const ALLOWED_STATUS = {
  DF: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EV: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EX: new Set(["proposed", "active", "blocked", "completed", "cancelled"]),
  HY: new Set(["proposed", "active", "supported", "rejected", "superseded", "withdrawn"]),
  MS: new Set(["proposed", "approved", "active", "blocked", "completed", "cancelled", "archived"]),
  RP: new Set([
    "draft",
    "review",
    "accepted",
    "canonical",
    "deprecated",
    "archived",
    "superseded",
    "withdrawn"
  ]),
  TH: new Set(["candidate", "supported", "established", "challenged", "superseded", "rejected"])
};
const CONFIDENCE = new Set(["very-low", "low", "medium", "medium-high", "high", "very-high"]);
const KIND_CONFIG = {
  decisions: ["research/decisions", "registries/decisions.json", "DF"],
  evidence: ["research/evidence", "registries/evidence.json", "EV"],
  experiments: ["research/experiments", "registries/experiments.json", "EX"],
  hypotheses: ["research/hypotheses", "registries/hypotheses.json", "HY"],
  journals: ["research/journals", "registries/journals.json", "JR"],
  missions: ["missions", "registries/missions.json", "MS"],
  "research-packages": ["research/packages", "registries/research-packages.json", "RP"],
  theories: ["research/theories", "registries/theories.json", "TH"]
};
const ARTIFACT_REGISTRY_RESOURCE = "artifact-registries";
const ARTIFACT_REGISTRY_TRANSACTION_VERSION = "1.0.0";
const WORK_STATE_RESOURCE = "work-state";
const WORK_STATE_TRANSACTION_VERSION = "1.0.0";
const WORK_STATE_PATHS = [".ros/events/events.jsonl", ".ros/context/current.json"];
const BACKLOG_STATE_RESOURCE = "backlog-state";
const BACKLOG_STATE_TRANSACTION_VERSION = "1.0.0";
const BACKLOG_STATE_PATHS = [".ros/work/queue.json", ".ros/work/queue.md"];

const SEMANTIC_STATES = new Set(["backlog", "ready", "active", "review", "blocked", "complete"]);
const TRANSITIONS = {
  ready: new Set(["begin", "block"]),
  active: new Set(["complete", "block"]),
  blocked: new Set(["resume"]),
  complete: new Set()
};

const WORK_ID_RE = /^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$/;
const PRIORITIES = new Set(["high", "medium", "low"]);

// The local backlog is a repository-owned capture/triage layer, not a second
// work-item authority. Once a backlog item starts (`ros work start`, which
// delegates to the existing `begin` transition), its live state in
// `.ros/context/current.json` always wins over the backlog's own `status` --
// see effectiveStatus(). This keeps exactly one authoritative record per ID.
// "complete" is the one status this triage lifecycle itself never transitions
// to (only ready/block/abandon do) -- it is written only when a promoted
// item finishes live (EV-ROS-2026-A052), kept here so validation recognizes
// it as legitimate rather than reporting a schema violation on the exact
// record the F# CLI (this repository's own ./ros) now correctly writes.
const BACKLOG_STATUS_VALUES = new Set(["captured", "ready", "blocked", "abandoned", "complete"]);
const BACKLOG_TRANSITIONS = {
  captured: new Set(["ready", "abandon"]),
  ready: new Set(["block", "start", "abandon"]),
  blocked: new Set(["ready", "abandon"]),
  abandoned: new Set()
};

function workConfig(root) {
  const config = readJson(path.join(root, "ros.json"), {});
  const result = {
    protocolVersion: config.workProtocol?.version ?? "1.0.0",
    repository: config.repository?.id ?? config.name ?? path.basename(root),
    stateMapping: config.workProtocol?.semanticMapping ?? {
      ready: "ready", active: "active", blocked: "blocked", complete: "complete"
    },
    evidence: config.workProtocol?.completionEvidence ?? { default: ["implementation", "tests"] },
    meaningful: config.workProtocol?.meaningfulPaths ?? ["**"],
    ignored: config.workProtocol?.ignoredPaths ?? [".git/**", ".ros/context/**", ".ros/events/**", ".ros/work/**", ".ros/telemetry/**", ".ros/locks/**"],
    enforce: config.workProtocol?.enforceAttribution === true
  };
  for (const [local, semantic] of Object.entries(result.stateMapping)) {
    if (!SEMANTIC_STATES.has(semantic)) throw new Error(`ros.json maps '${local}' to invalid semantic state '${semantic}'`);
  }
  return result;
}

function gitPaths(root) {
  const observation = observeGitStatus(root);
  if (observation.outcome === "unavailable") {
    // Greenfield installation intentionally supports a directory before `git init`.
    // Every other Git failure is indeterminate and must not look like a clean tree.
    if (observation.failure.reason === "not-repository") return [];
    const error = new Error(`${observation.failure.operation} unavailable: ${observation.failure.message}`);
    error.gitFailure = observation.failure;
    throw error;
  }
  const paths = observation.changes.map((change) => change.path);
  const comparison = process.env.ROS_BASE_REF;
  if (comparison) {
    const exists = runGitText(root, ["cat-file", "-e", `${comparison}^{commit}`]);
    if (exists.outcome === "success") {
      const committed = requireGitText(runGitText(root, ["diff", "--name-only", `${comparison}...HEAD`]));
      paths.push(...committed.split(/\r?\n/).filter(Boolean));
    }
    // A CI base ref can be absent in nested fixture repositories. Dirty paths
    // remain authoritative there; only the unavailable committed range is skipped.
  }
  return [...new Set(paths)].sort();
}

function globMatch(value, pattern) {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, "\\$&").replaceAll("**", "\u0000").replaceAll("*", "[^/]*").replaceAll("\u0000", ".*");
  return new RegExp(`^${escaped}$`).test(value);
}

function meaningfulPaths(root, paths) {
  const config = workConfig(root);
  return paths.filter((item) => config.meaningful.some((pattern) => globMatch(item, pattern)) && !config.ignored.some((pattern) => globMatch(item, pattern)));
}

function contextPath(root) { return path.join(root, ".ros", "context", "current.json"); }
function eventsPath(root) { return path.join(root, ".ros", "events", "events.jsonl"); }
function queuePath(root) { return path.join(root, ".ros", "work", "queue.json"); }
function detailPath(root, id) { return path.join(root, ".ros", "work", "items", `${id}.md`); }
function attachmentsDir(root, id) { return path.join(root, ".ros", "work", "attachments", id); }

function loadContext(root) {
  return readJson(contextPath(root), { schemaVersion: "1.0.0", repository: workConfig(root).repository, workItems: [] });
}

function allowedActions(item) {
  return [...(TRANSITIONS[item.semanticState] ?? [])].sort();
}

export function contextView(root, requestedId) {
  const config = workConfig(root);
  const context = loadContext(root);
  const items = requestedId ? context.workItems.filter((item) => item.id === requestedId) : context.workItems;
  if (requestedId && !items.length) throw new Error(`work item '${requestedId}' is not in repository context`);
  return {
    schemaVersion: context.schemaVersion,
    protocolVersion: config.protocolVersion,
    repository: config.repository,
    actor: context.actor ?? "unknown",
    workItems: items.map((item) => ({
      ...item,
      allowedActions: allowedActions(item),
      requiredEvidenceForCompletion: config.evidence[item.type] ?? config.evidence.default ?? []
    }))
  };
}

function loadQueue(root) {
  return readJson(queuePath(root), { schemaVersion: "1.0.0", repository: workConfig(root).repository, nextSeq: 1, items: [] });
}

function nextQueueId(queue) {
  let candidate;
  do {
    candidate = `WI-${String(queue.nextSeq).padStart(4, "0")}`;
    queue.nextSeq += 1;
  } while (queue.items.some((item) => item.id === candidate));
  return candidate;
}

function effectiveStatus(queueItem, contextItem) {
  if (contextItem && contextItem.semanticState !== "ready") return contextItem.semanticState;
  if (queueItem) return queueItem.status;
  if (contextItem) return contextItem.semanticState;
  return undefined;
}

function mergedRows(queue, contextItems) {
  const contextById = new Map(contextItems.map((item) => [item.id, item]));
  const ids = new Set([...queue.items.map((item) => item.id), ...contextItems.map((item) => item.id)]);
  return [...ids].sort().map((id) => {
    const queueItem = queue.items.find((item) => item.id === id);
    const contextItem = contextById.get(id);
    return {
      id,
      title: queueItem?.title ?? id,
      description: queueItem?.description ?? null,
      tags: queueItem?.tags ?? [],
      priority: queueItem?.priority ?? null,
      status: effectiveStatus(queueItem, contextItem),
      blockedReason: contextItem?.semanticState === "blocked" ? contextItem.blockReason : queueItem?.blockedReason,
      backlogActions: queueItem && !contextItem ? [...(BACKLOG_TRANSITIONS[queueItem.status] ?? [])].sort() : [],
      attachments: (queueItem?.attachments ?? []).map(({ id: attachmentId, name, size, contentType, uploadedAt }) => ({ id: attachmentId, name, size, contentType, uploadedAt })),
      liveWorkItem: contextItem
        ? { state: contextItem.state, semanticState: contextItem.semanticState, allowedActions: allowedActions(contextItem) }
        : null
    };
  });
}

function renderQueueMarkdown(rows) {
  const header = "# Work Queue\n\n| ID | Work | Status | Tags | Priority |\n|---|---|---|---|---|\n";
  const body = rows.map((row) => `| ${row.id} | ${row.title} | ${row.status} | ${row.tags.join(", ")} | ${row.priority ?? ""} |`).join("\n");
  return `${header}${body}${body ? "\n" : ""}`;
}

function saveQueueUnlocked(root, queue) {
  const context = loadContext(root);
  commitBacklogState(root, [
    { path: ".ros/work/queue.json", content: `${JSON.stringify(queue, null, 2)}\n` },
    { path: ".ros/work/queue.md", content: renderQueueMarkdown(mergedRows(queue, context.workItems)) }
  ]);
}

export function showWork(root, id) {
  if (!id) throw new Error("show requires an ID");
  const row = mergedWorkView(root, {}).find((candidate) => candidate.id === id);
  if (!row) throw new Error(`work item '${id}' was not found`);
  const detail = fs.existsSync(detailPath(root, id)) ? fs.readFileSync(detailPath(root, id), "utf8") : null;
  return { ...row, detail };
}

export function blockWork(root, ids, options = {}) {
  if (!ids.length) throw new Error("block requires at least one work-item ID");
  return withWorkProtocol(root, () => {
    const queue = loadQueue(root);
    const context = loadContext(root);
    const backlogIds = ids.filter((id) => queue.items.some((item) => item.id === id) && !context.workItems.some((item) => item.id === id));
    const contextIds = ids.filter((id) => !backlogIds.includes(id));
    const results = backlogIds.map((id) => backlogTransitionUnlocked(root, "block", id, options));
    if (contextIds.length) {
      const transitioned = transitionUnlocked(root, "block", contextIds, options);
      results.push(...transitioned.context.workItems.filter((item) => contextIds.includes(item.id)));
    }
    return results;
  });
}

export function mergedWorkView(root, { tags, status } = {}) {
  const queue = loadQueue(root);
  const context = loadContext(root);
  let rows = mergedRows(queue, context.workItems);
  if (tags?.length) rows = rows.filter((row) => tags.every((tag) => row.tags.includes(tag)));
  if (status) rows = rows.filter((row) => row.status === status);
  return rows;
}

function captureWorkUnlocked(root, title, options = {}) {
  if (!title || !title.trim()) throw new Error("add requires a non-empty title");
  if (options.priority && !PRIORITIES.has(options.priority)) throw new Error(`invalid priority '${options.priority}'; use high, medium, or low`);
  const queue = loadQueue(root);
  const context = loadContext(root);
  const now = new Date().toISOString();
  let id = options.id;
  if (id) {
    if (!WORK_ID_RE.test(id)) throw new Error(`invalid work-item ID '${id}'`);
    if (queue.items.some((item) => item.id === id)) throw new Error(`work item '${id}' already exists`);
    if (context.workItems.some((item) => item.id === id)) throw new Error(`work item '${id}' already exists in repository context`);
  } else {
    id = nextQueueId(queue);
  }
  const item = {
    id,
    title: title.trim(),
    description: options.description?.trim() || null,
    tags: options.tags ?? [],
    priority: options.priority ?? "medium",
    status: "captured",
    attachments: [],
    createdAt: now,
    updatedAt: now,
    createdBy: options.actor ?? process.env.ROS_ACTOR ?? "unknown",
    source: options.source ?? "manual",
    sourceReference: options.sourceReference ?? null
  };
  queue.items.push(item);
  saveQueueUnlocked(root, queue);
  for (const file of options.files ?? []) attachFileUnlocked(root, id, file);
  return options.files?.length ? showWork(root, id) : item;
}

export function captureWork(root, title, options = {}) {
  return withWorkProtocol(root, () => captureWorkUnlocked(root, title, options));
}

// Any known ID -- whether captured via `add` or begun directly on the
// external-authority protocol -- can carry description/tags/attachments.
// This upserts a minimal backlog record on first touch so `update`/`attach`
// work uniformly regardless of how the item originated, without ever
// inventing a lifecycle status for it (see effectiveStatus()).
function findOrCreateQueueEntry(root, queue, id) {
  let item = queue.items.find((entry) => entry.id === id);
  if (item) return item;
  if (!WORK_ID_RE.test(id)) throw new Error(`invalid work-item ID '${id}'`);
  const context = loadContext(root);
  if (!context.workItems.some((entry) => entry.id === id)) throw new Error(`work item '${id}' was not found`);
  const now = new Date().toISOString();
  item = {
    id,
    title: id,
    description: null,
    tags: [],
    priority: "medium",
    status: "captured",
    attachments: [],
    createdAt: now,
    updatedAt: now,
    createdBy: "unknown",
    source: "manual",
    sourceReference: null
  };
  queue.items.push(item);
  return item;
}

function updateWorkUnlocked(root, id, options = {}) {
  const queue = loadQueue(root);
  const item = findOrCreateQueueEntry(root, queue, id);
  if (options.title !== undefined) {
    if (!options.title.trim()) throw new Error("title cannot be empty");
    item.title = options.title.trim();
  }
  if (options.description !== undefined) item.description = options.description.trim() || null;
  if (options.tags !== undefined) item.tags = options.tags;
  if (options.priority !== undefined) {
    if (!PRIORITIES.has(options.priority)) throw new Error(`invalid priority '${options.priority}'; use high, medium, or low`);
    item.priority = options.priority;
  }
  item.updatedAt = new Date().toISOString();
  saveQueueUnlocked(root, queue);
  return item;
}

export function updateWork(root, id, options = {}) {
  return withWorkProtocol(root, () => updateWorkUnlocked(root, id, options));
}

function sanitizeFileComponent(value) {
  const cleaned = path.basename(String(value)).trim().replace(/[^A-Za-z0-9._-]+/g, "_");
  return cleaned || "file";
}

// A file's associated `name` is independent of its on-disk storage name --
// the caller may attach several files under the same display name, or
// rename one away from what was originally uploaded. Storage uniqueness is
// handled here, not by the caller.
function attachFileUnlocked(root, id, { sourcePath, buffer, name, contentType } = {}) {
  if (!sourcePath && !buffer) throw new Error("attach requires a source file or upload buffer");
  const queue = loadQueue(root);
  const item = findOrCreateQueueEntry(root, queue, id);
  const data = buffer ?? fs.readFileSync(path.resolve(root, sourcePath));
  const displayName = (name ?? (sourcePath ? path.basename(sourcePath) : "file")).trim() || "file";
  item.attachments ??= [];
  const sequence = item.attachments.reduce((max, entry) => Math.max(max, entry.seq ?? 0), 0) + 1;
  const storedFile = `${sequence}-${sanitizeFileComponent(displayName)}`;
  const dir = attachmentsDir(root, id);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, storedFile), data);
  const now = new Date().toISOString();
  const record = {
    id: `ATT-${sequence}`,
    seq: sequence,
    name: displayName,
    file: storedFile,
    size: data.length,
    contentType: contentType || null,
    uploadedAt: now
  };
  item.attachments.push(record);
  item.updatedAt = now;
  saveQueueUnlocked(root, queue);
  return record;
}

export function attachFile(root, id, options = {}) {
  return withWorkProtocol(root, () => attachFileUnlocked(root, id, options));
}

export function attachmentFilePath(root, id, attachmentId) {
  const queue = loadQueue(root);
  const item = queue.items.find((entry) => entry.id === id);
  const record = item?.attachments?.find((entry) => entry.id === attachmentId);
  if (!record) throw new Error(`attachment '${attachmentId}' was not found on '${id}'`);
  return { record, filePath: path.join(attachmentsDir(root, id), record.file) };
}

function backlogTransitionUnlocked(root, action, id, options = {}) {
  const queue = loadQueue(root);
  const item = queue.items.find((entry) => entry.id === id);
  if (!item) throw new Error(`'${id}' is not a captured local work item`);
  const legal = BACKLOG_TRANSITIONS[item.status] ?? new Set();
  if (!legal.has(action)) throw new Error(`cannot ${action} backlog item '${id}' from '${item.status}'`);
  const now = new Date().toISOString();
  if (action === "ready") { item.status = "ready"; delete item.blockedReason; }
  if (action === "block") {
    if (!options.reason) throw new Error("block requires --reason");
    item.status = "blocked";
    item.blockedReason = options.reason;
  }
  if (action === "abandon") {
    item.status = "abandoned";
    if (options.reason) item.abandonedReason = options.reason;
  }
  item.updatedAt = now;
  saveQueueUnlocked(root, queue);
  return item;
}

export function backlogTransition(root, action, id, options = {}) {
  return withWorkProtocol(root, () => backlogTransitionUnlocked(root, action, id, options));
}

export function startWork(root, ids, options = {}) {
  if (!ids.length) throw new Error("start requires at least one work-item ID");
  return withWorkProtocol(root, () => {
    const queue = loadQueue(root);
    for (const id of ids) {
      const item = queue.items.find((entry) => entry.id === id);
      if (item && item.status !== "ready") {
        throw new Error(
          item.status === "abandoned"
            ? `cannot start backlog item '${id}': it was abandoned`
            : `cannot start backlog item '${id}' from '${item.status}'; mark it ready first`
        );
      }
    }
    return transitionUnlocked(root, "begin", ids, options);
  });
}

function eventPayload(event) {
  const payload = { schemaVersion: "1.0.0", ...event };
  payload.eventId = crypto.createHash("sha256").update(JSON.stringify(payload)).digest("hex").slice(0, 24);
  return payload;
}

function sha256Text(text) {
  return crypto.createHash("sha256").update(text, "utf8").digest("hex");
}

function workStateTransactionPath(root) {
  return path.join(root, ".ros", "transactions", `${WORK_STATE_RESOURCE}.json`);
}

function currentText(root, relative) {
  const file = path.join(root, relative);
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
}

function workStateRecord(root, writes) {
  if (writes.length !== WORK_STATE_PATHS.length || writes.some((write, index) => write.path !== WORK_STATE_PATHS[index])) {
    throw new Error("work-state transaction must contain event then context");
  }
  return {
    schemaVersion: WORK_STATE_TRANSACTION_VERSION,
    resource: WORK_STATE_RESOURCE,
    writes: writes.map((write) => {
      const before = currentText(root, write.path);
      return {
        path: write.path,
        beforeSha256: before === null ? null : sha256Text(before),
        afterSha256: sha256Text(write.content),
        content: write.content
      };
    })
  };
}

function validWorkStateRecord(record) {
  if (record?.schemaVersion !== WORK_STATE_TRANSACTION_VERSION || record?.resource !== WORK_STATE_RESOURCE) return false;
  if (!Array.isArray(record.writes) || record.writes.length !== WORK_STATE_PATHS.length) return false;
  return record.writes.every((write, index) =>
    write?.path === WORK_STATE_PATHS[index]
    && (write.beforeSha256 === null || /^[a-f0-9]{64}$/.test(write.beforeSha256))
    && typeof write.afterSha256 === "string"
    && typeof write.content === "string"
    && sha256Text(write.content) === write.afterSha256
  );
}

function recoverWorkStateTransaction(root) {
  const transaction = workStateTransactionPath(root);
  if (!fs.existsSync(transaction)) return false;
  const record = readJson(transaction);
  if (!validWorkStateRecord(record)) throw new Error("work-state transaction is invalid");

  const observations = record.writes.map((write) => {
    const current = currentText(root, write.path);
    const hash = current === null ? null : sha256Text(current);
    if (hash !== write.beforeSha256 && hash !== write.afterSha256) {
      throw new Error(`work-state transaction target changed after preparation: ${write.path}`);
    }
    return { write, alreadyApplied: hash === write.afterSha256 };
  });

  for (const { write, alreadyApplied } of observations) {
    if (!alreadyApplied) writeTextAtomic(path.join(root, write.path), write.content);
  }
  fs.unlinkSync(transaction);
  return true;
}

function commitWorkState(root, writes) {
  const transaction = workStateTransactionPath(root);
  if (fs.existsSync(transaction)) throw new Error("pending work-state transaction must be recovered before preparing another");
  writeJson(transaction, workStateRecord(root, writes));
  recoverWorkStateTransaction(root);
}

function backlogStateTransactionPath(root) {
  return path.join(root, ".ros", "transactions", `${BACKLOG_STATE_RESOURCE}.json`);
}

function backlogStateRecord(root, writes) {
  if (writes.length !== BACKLOG_STATE_PATHS.length || writes.some((write, index) => write.path !== BACKLOG_STATE_PATHS[index])) {
    throw new Error("backlog-state transaction must contain queue then projection");
  }
  return {
    schemaVersion: BACKLOG_STATE_TRANSACTION_VERSION,
    resource: BACKLOG_STATE_RESOURCE,
    writes: writes.map((write) => {
      const before = currentText(root, write.path);
      return {
        path: write.path,
        beforeSha256: before === null ? null : sha256Text(before),
        afterSha256: sha256Text(write.content),
        content: write.content
      };
    })
  };
}

function validBacklogStateRecord(record) {
  if (record?.schemaVersion !== BACKLOG_STATE_TRANSACTION_VERSION || record?.resource !== BACKLOG_STATE_RESOURCE) return false;
  if (!Array.isArray(record.writes) || record.writes.length !== BACKLOG_STATE_PATHS.length) return false;
  return record.writes.every((write, index) =>
    write?.path === BACKLOG_STATE_PATHS[index]
    && (write.beforeSha256 === null || /^[a-f0-9]{64}$/.test(write.beforeSha256))
    && typeof write.afterSha256 === "string"
    && typeof write.content === "string"
    && sha256Text(write.content) === write.afterSha256
  );
}

function recoverBacklogStateTransaction(root) {
  const transaction = backlogStateTransactionPath(root);
  if (!fs.existsSync(transaction)) return false;
  const record = readJson(transaction);
  if (!validBacklogStateRecord(record)) throw new Error("backlog-state transaction is invalid");

  const observations = record.writes.map((write) => {
    const current = currentText(root, write.path);
    const hash = current === null ? null : sha256Text(current);
    if (hash !== write.beforeSha256 && hash !== write.afterSha256) {
      throw new Error(`backlog-state transaction target changed after preparation: ${write.path}`);
    }
    return { write, alreadyApplied: hash === write.afterSha256 };
  });

  for (const { write, alreadyApplied } of observations) {
    if (!alreadyApplied) writeTextAtomic(path.join(root, write.path), write.content);
  }
  fs.unlinkSync(transaction);
  return true;
}

function commitBacklogState(root, writes) {
  const transaction = backlogStateTransactionPath(root);
  if (fs.existsSync(transaction)) throw new Error("pending backlog-state transaction must be recovered before preparing another");
  writeJson(transaction, backlogStateRecord(root, writes));
  recoverBacklogStateTransaction(root);
}

function withWorkProtocol(root, operation) {
  return withFileLock(root, "work-protocol", () => {
    recoverWorkStateTransaction(root);
    recoverBacklogStateTransaction(root);
    return operation();
  });
}

function recoverOrStartExecution(root, item, statuses, options) {
  item.telemetryExecutionIds ??= [];
  const linked = new Set(item.telemetryExecutionIds);
  const candidates = showTelemetry(root, item.id)
    .filter((record) => statuses.includes(record.status) && !linked.has(record.executionId))
    .sort((left, right) => left.executionId.localeCompare(right.executionId));
  if (options.executionId) {
    const requested = candidates.find((record) => record.executionId === options.executionId);
    if (requested) return requested;
    if (candidates.length) {
      throw new Error(`detached telemetry execution must be linked before creating '${options.executionId}' for '${item.id}'; rerun with --execution-id ${candidates[0].executionId}`);
    }
  }
  if (candidates.length > 1) {
    throw new Error(`multiple detached telemetry executions require explicit selection for '${item.id}'; rerun with --execution-id one of: ${candidates.map((record) => record.executionId).join(", ")}`);
  }
  if (candidates.length === 1) return candidates[0];
  return startExecution(root, item.id, { ...options, attachToContext: false });
}

function renderedEventLog(root, planned) {
  const current = currentText(root, ".ros/events/events.jsonl") ?? "";
  const existing = current.split(/\r?\n/).filter(Boolean).map(JSON.parse);
  const known = new Set(existing.map((item) => item.eventId));
  const additions = planned.filter((item) => !known.has(item.eventId));
  if (!additions.length) return current;
  const separator = current.length > 0 && !current.endsWith("\n") ? "\n" : "";
  return `${current}${separator}${additions.map((item) => JSON.stringify(item)).join("\n")}\n`;
}

function adapterResult(request, outcome, fields = {}) {
  return {
    schemaVersion: "1.0.0",
    protocolVersion: request.protocolVersion,
    requestId: request.requestId,
    operation: request.operation,
    outcome,
    ...fields
  };
}

function validateAdapterRequest(request) {
  for (const field of ["protocolVersion", "requestId", "operation", "repository", "principal"]) {
    if (!request[field]) throw new Error(`adapter request is missing '${field}'`);
  }
  if (request.protocolVersion !== "1.0.0") return adapterResult(request, "failure", { error: { code: "protocol_mismatch", message: "adapter supports protocol 1.0.0" } });
  if (!["getWorkItem", "transitionWorkItem", "publishRepositoryEvent"].includes(request.operation)) {
    return adapterResult(request, "failure", { error: { code: "unsupported_operation", message: `unsupported operation '${request.operation}'` } });
  }
  return null;
}

function callFileAdapter(storeFile, request) {
  const invalid = validateAdapterRequest(request);
  if (invalid) return invalid;
  const store = readJson(storeFile, { schemaVersion: "1.0.0", protocolVersion: "1.0.0", repositories: [], workItems: {}, events: [], requests: {} });
  if (store.requests?.[request.requestId]) return store.requests[request.requestId];
  let result;
  if (!store.repositories.includes(request.repository)) {
    result = adapterResult(request, "failure", { error: { code: "repository_unknown", message: `repository '${request.repository}' is not authorized` } });
  } else if (request.simulateOutcome === "unknown") {
    result = adapterResult(request, "unknown", { error: { code: "remote_outcome_unknown", message: "the remote effect could not be confirmed" } });
  } else if (request.operation === "getWorkItem" && !(request.scopes ?? []).includes("work:read")) {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks work:read scope" } });
  } else if (request.operation === "getWorkItem") {
    const item = store.workItems[request.workItem];
    result = item
      ? adapterResult(request, "success", { data: { workItem: item } })
      : adapterResult(request, "failure", { error: { code: "work_item_not_found", message: `work item '${request.workItem}' was not found` } });
  } else if (!(request.scopes ?? []).includes("work:transition") && request.operation === "transitionWorkItem") {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks work:transition scope" } });
  } else if (request.operation === "transitionWorkItem") {
    const item = store.workItems[request.workItem];
    if (!item) result = adapterResult(request, "failure", { error: { code: "work_item_not_found", message: `work item '${request.workItem}' was not found` } });
    else if (request.expectedState && item.state !== request.expectedState) result = adapterResult(request, "failure", { error: { code: "state_conflict", message: `expected '${request.expectedState}', found '${item.state}'` } });
    else {
      item.state = request.targetState;
      item.updatedBy = request.principal;
      result = adapterResult(request, "success", { data: { workItem: item } });
    }
  } else if (!(request.scopes ?? []).includes("event:publish")) {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks event:publish scope" } });
  } else {
    const event = request.event;
    if (!event?.eventId) result = adapterResult(request, "failure", { error: { code: "invalid_event", message: "event.eventId is required" } });
    else {
      if (!store.events.some((item) => item.eventId === event.eventId)) store.events.push(event);
      result = adapterResult(request, "success", { data: { eventId: event.eventId } });
    }
  }
  store.requests ??= {};
  store.requests[request.requestId] = result;
  writeJson(storeFile, store);
  return result;
}

function transitionUnlocked(root, action, ids, options = {}) {
  if (!ids.length) throw new Error(`${action} requires at least one work-item ID`);
  const config = workConfig(root);
  const context = loadContext(root);
  const now = new Date().toISOString();
  const byId = new Map(context.workItems.map((item) => [item.id, item]));
  const events = [];
  const observedGitPaths = action === "complete" || (action === "begin" && !context.startedAt) ? gitPaths(root) : [];
  for (const id of ids) {
    if (!WORK_ID_RE.test(id)) throw new Error(`invalid work-item ID '${id}'`);
    let item = byId.get(id);
    if (!item) {
      if (action !== "begin") throw new Error(`work item '${id}' is not in repository context`);
      item = { id, type: options.type ?? "task", state: "ready", semanticState: "ready", evidence: [] };
      context.workItems.push(item);
      byId.set(id, item);
    }
    const current = item.semanticState;
    if (!TRANSITIONS[current]?.has(action)) {
      try {
        recordTelemetryMetric(root, id, {
          id: "quality.invalid_transitions_prevented",
          value: 1,
          quality: "derived",
          source: { type: "calculated", name: "ros-work-protocol", mechanism: "transition-guard" },
          collectedAt: now
        });
      } catch {
        // The transition guard remains authoritative when no active telemetry exists.
      }
      throw new Error(`cannot ${action} '${id}' from '${current}'`);
    }
    if (action === "begin" || action === "resume") item.state = options.localState ?? "active";
    if (action === "block") {
      if (!options.reason) throw new Error("block requires --reason");
      item.state = "blocked";
      item.blockReason = options.reason;
    }
    if (action === "complete") {
      const evidence = options.evidence ?? [];
      const required = config.evidence[item.type] ?? config.evidence.default ?? [];
      const types = new Set(evidence.map((entry) => entry.type));
      const missing = required.filter((kind) => !types.has(kind));
      if (missing.length) throw new Error(`completion evidence missing for '${id}': ${missing.join(", ")}`);
      for (const entry of evidence) if (!fs.existsSync(path.resolve(root, entry.path))) throw new Error(`evidence path does not exist: ${entry.path}`);
      item.evidence = evidence;
      item.state = options.localState ?? "complete";
      item.completedAt = now;
      if (item.type === "research") item.conclusion = options.conclusion ?? "inconclusive";
    }
    item.semanticState = config.stateMapping[item.state] ?? (action === "begin" || action === "resume" ? "active" : action === "block" ? "blocked" : "complete");
    if (!SEMANTIC_STATES.has(item.semanticState)) throw new Error(`invalid semantic state '${item.semanticState}'`);
    item.updatedAt = now;
    if (configuredTelemetry(root).enabled) {
      if (action === "begin") {
        const execution = recoverOrStartExecution(root, item, ["active"], {
          workType: item.type,
          classifications: options.classifications,
          classificationRationale: options.classificationRationale,
          rd: options.rd,
          identity: options.telemetryIdentity
        });
        item.telemetryExecutionIds ??= [];
        if (execution && !item.telemetryExecutionIds.includes(execution.executionId)) item.telemetryExecutionIds.push(execution.executionId);
      }
      if (action === "resume") {
        recordTelemetryLifecycle(root, id, "resumed", { occurredAt: now });
        const active = showTelemetry(root, id).filter((record) => record.status === "active");
        if (!active.length) {
          const prior = showTelemetry(root, id).at(-1);
          const execution = recoverOrStartExecution(root, item, ["active"], {
            workType: item.type,
            parentExecutionId: prior?.executionId ?? null,
            identity: options.telemetryIdentity
          });
          item.telemetryExecutionIds ??= [];
          if (execution) item.telemetryExecutionIds.push(execution.executionId);
        } else for (const execution of active) if (!item.telemetryExecutionIds.includes(execution.executionId)) item.telemetryExecutionIds.push(execution.executionId);
      }
      if (action === "block") recordTelemetryLifecycle(root, id, "blocked", { occurredAt: now, reason: options.reason });
      if (action === "complete") {
        if (!(item.telemetryExecutionIds ?? []).length) {
          const execution = recoverOrStartExecution(root, item, ["active", "finalized"], {
            workType: item.type,
            startedAt: now,
            identity: options.telemetryIdentity
          });
          item.telemetryExecutionIds ??= [];
          if (execution) item.telemetryExecutionIds.push(execution.executionId);
        }
        const finalized = finalizeWorkExecutions(root, id, { finalizedAt: now });
        for (const execution of finalized) if (!item.telemetryExecutionIds.includes(execution.executionId)) item.telemetryExecutionIds.push(execution.executionId);
      }
    }
    const event = eventPayload({
      type: `work.${action === "begin" ? "started" : action === "complete" ? "completed" : action === "block" ? "blocked" : "resumed"}`,
      workItem: id, repository: config.repository, protocolVersion: config.protocolVersion,
      occurredAt: now, reason: options.reason, evidence: item.evidence,
      paths: action === "complete" ? meaningfulPaths(root, observedGitPaths).filter((p) => !(context.baselineDirtyPaths ?? []).includes(p)) : [],
      telemetryExecutions: item.telemetryExecutionIds ?? [],
      publication: { status: "pending" }
    });
    events.push(event);
  }
  context.protocolVersion = config.protocolVersion;
  context.repository = config.repository;
  context.actor = options.actor ?? process.env.ROS_ACTOR ?? context.actor ?? "unknown";
  context.updatedAt = now;
  if (action === "begin" && !context.startedAt) {
    context.startedAt = now;
    context.baselineDirtyPaths = observedGitPaths.filter((item) => !context.workItems.some((work) => work.id === item));
  }
  commitWorkState(root, [
    { path: ".ros/events/events.jsonl", content: renderedEventLog(root, events) },
    { path: ".ros/context/current.json", content: `${JSON.stringify(context, null, 2)}\n` }
  ]);
  return { context, events };
}

export function transition(root, action, ids, options = {}) {
  return withWorkProtocol(root, () => transitionUnlocked(root, action, ids, options));
}

function workFindings(root) {
  const config = workConfig(root);
  if (!config.enforce) return [];
  const context = loadContext(root);
  let paths;
  try {
    paths = gitPaths(root);
  } catch (error) {
    if (!error.gitFailure) throw error;
    return [{
      path: ".git",
      field: "work_items",
      message: `cannot verify work attribution because ${error.gitFailure.operation} is unavailable: ${error.gitFailure.reason}`
    }];
  }
  const changed = meaningfulPaths(root, paths).filter((item) => !(context.baselineDirtyPaths ?? []).includes(item));
  if (!changed.length) return [];
  const events = fs.existsSync(eventsPath(root)) ? fs.readFileSync(eventsPath(root), "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
  const attributed = new Set(events.flatMap((event) => event.paths ?? []));
  const active = context.workItems.some((item) => item.semanticState === "active" || item.semanticState === "blocked");
  return changed.filter((item) => !attributed.has(item) && !active).map((item) => ({ path: item, field: "work_items", message: "meaningful change has no active or completed work-item attribution" }));
}

function queueFindings(root) {
  const queue = loadQueue(root);
  const findings = [];
  const seen = new Set();
  const relative = ".ros/work/queue.json";
  for (const item of queue.items) {
    if (seen.has(item.id)) findings.push({ path: relative, field: "id", message: `duplicate backlog id '${item.id}'` });
    seen.add(item.id);
    if (!WORK_ID_RE.test(item.id)) findings.push({ path: relative, field: "id", message: `invalid backlog id '${item.id}'` });
    if (!BACKLOG_STATUS_VALUES.has(item.status)) findings.push({ path: relative, field: "status", message: `invalid status '${item.status}' for '${item.id}'` });
    if (item.priority && !PRIORITIES.has(item.priority)) findings.push({ path: relative, field: "priority", message: `invalid priority '${item.priority}' for '${item.id}'` });
  }
  return findings;
}

function scalar(raw) {
  const value = raw.trim();
  if (!value) return "";
  if (value === "[]") return [];
  if (value === "{}") return {};
  if (value.startsWith("[") && value.endsWith("]")) {
    const inner = value.slice(1, -1).trim();
    if (!inner) return [];
    return inner.split(",").map((item) => item.trim().replace(/^['"]|['"]$/g, ""));
  }
  if (["true", "false"].includes(value.toLowerCase())) return value.toLowerCase() === "true";
  if (/^-?\d+(?:\.\d+)?$/.test(value)) return Number(value);
  return value.replace(/^['"]|['"]$/g, "");
}

export function parseFrontMatter(text) {
  const lines = text.split(/\r?\n/);
  if (lines[0]?.trim() !== "---") throw new Error("missing opening '---'");
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === "---");
  if (end < 0) throw new Error("missing closing '---'");
  const result = {};
  const stack = [{ indent: -1, value: result }];
  for (let index = 1; index < end; index += 1) {
    const raw = lines[index];
    if (!raw.trim() || raw.trimStart().startsWith("#")) continue;
    const indent = raw.length - raw.trimStart().length;
    const stripped = raw.trim();
    while (stack.at(-1).indent >= indent) stack.pop();
    const parent = stack.at(-1).value;
    if (stripped.startsWith("- ")) {
      if (!Array.isArray(parent)) throw new Error(`line ${index + 1}: list item has no list field`);
      parent.push(scalar(stripped.slice(2)));
      continue;
    }
    const separator = stripped.indexOf(":");
    if (separator < 1 || Array.isArray(parent)) {
      throw new Error(`line ${index + 1}: expected 'field: value'`);
    }
    const key = stripped.slice(0, separator).trim();
    const rawValue = stripped.slice(separator + 1);
    if (rawValue.trim()) {
      parent[key] = scalar(rawValue);
      continue;
    }
    const next = lines[index + 1];
    const nextIndent = next ? next.length - next.trimStart().length : -1;
    const child = next && nextIndent > indent && next.trim().startsWith("- ") ? [] : {};
    parent[key] = child;
    stack.push({ indent, value: child });
  }
  return result;
}

function walkMarkdown(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return walkMarkdown(target);
    return entry.isFile() && entry.name.endsWith(".md") && !entry.name.startsWith(".") ? [target] : [];
  });
}

function artifactFiles(root) {
  const files = new Set();
  for (const [directory] of Object.values(KIND_CONFIG)) {
    for (const file of walkMarkdown(path.join(root, directory))) files.add(file);
  }
  return [...files].sort();
}

function loadArtifacts(root) {
  const artifacts = [];
  const findings = [];
  for (const file of artifactFiles(root)) {
    const relative = path.relative(root, file).split(path.sep).join("/");
    try {
      const metadata = parseFrontMatter(fs.readFileSync(file, "utf8"));
      artifacts.push({
        file,
        relative,
        metadata,
        id: String(metadata.id ?? metadata.identifier ?? "")
      });
    } catch (error) {
      findings.push({ path: relative, field: "front_matter", message: error.message });
    }
  }
  return { artifacts, findings };
}

function prefix(identifier) {
  return identifier.includes("-") ? identifier.split("-", 1)[0] : "";
}

function references(value) {
  if (typeof value === "string") return ID_RE.test(value) ? [value] : [];
  if (Array.isArray(value)) return value.filter((item) => typeof item === "string" && ID_RE.test(item));
  return [];
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function renderedRegistries(root, artifacts) {
  const rendered = new Map();
  for (const [, registry, artifactPrefix] of Object.values(KIND_CONFIG)) {
    const entries = artifacts
      .filter((artifact) => prefix(artifact.id) === artifactPrefix)
      .map((artifact) => {
        const entry = { ...artifact.metadata, id: artifact.id, path: artifact.relative };
        delete entry.identifier;
        return stable(entry);
      })
      .sort((a, b) => a.id.localeCompare(b.id));
    rendered.set(path.join(root, registry), `${JSON.stringify(entries, null, 2)}\n`);
  }
  return rendered;
}

function registryFindings(root, artifacts) {
  const findings = [];
  for (const [file, expected] of renderedRegistries(root, artifacts)) {
    const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (actual !== expected) {
      findings.push({
        path: path.relative(root, file).split(path.sep).join("/"),
        field: "",
        message: "registry is stale; run 'ros registry build'"
      });
    }
  }
  return findings;
}

function artifactRegistryTransactionPath(root) {
  return path.join(root, ".ros", "transactions", `${ARTIFACT_REGISTRY_RESOURCE}.json`);
}

function artifactRegistryPaths() {
  return new Set(Object.values(KIND_CONFIG).map(([, registry]) => registry));
}

function assertArtifactRegistryTransaction(record) {
  const expected = artifactRegistryPaths();
  if (record?.schemaVersion !== ARTIFACT_REGISTRY_TRANSACTION_VERSION
    || record?.resource !== ARTIFACT_REGISTRY_RESOURCE
    || !Array.isArray(record.writes)
    || !record.writes.length) {
    throw new Error("artifact registry transaction is invalid");
  }
  const seen = new Set();
  for (const write of record.writes) {
    if (typeof write?.path !== "string" || typeof write?.content !== "string" || !expected.has(write.path) || seen.has(write.path)) {
      throw new Error("artifact registry transaction is invalid");
    }
    seen.add(write.path);
  }
}

function recoverArtifactRegistryTransaction(root) {
  const file = artifactRegistryTransactionPath(root);
  if (!fs.existsSync(file)) return;
  const record = readJson(file);
  assertArtifactRegistryTransaction(record);
  for (const write of record.writes) writeTextAtomic(path.join(root, write.path), write.content);
  fs.unlinkSync(file);
}

function prepareArtifactRegistryTransaction(root, writes) {
  const record = {
    schemaVersion: ARTIFACT_REGISTRY_TRANSACTION_VERSION,
    resource: ARTIFACT_REGISTRY_RESOURCE,
    writes: writes.map(({ path: writePath, content }) => ({ path: writePath, content }))
  };
  assertArtifactRegistryTransaction(record);
  writeJson(artifactRegistryTransactionPath(root), record);
}

export function validate(root, { checkRegistries = true } = {}) {
  const loaded = loadArtifacts(root);
  const findings = [...loaded.findings];
  const byId = new Map();
  for (const artifact of loaded.artifacts) {
    if (!artifact.id) {
      findings.push({ path: artifact.relative, field: "id", message: "required field is missing" });
      continue;
    }
    if (!ID_RE.test(artifact.id)) {
      findings.push({ path: artifact.relative, field: "id", message: `invalid identifier '${artifact.id}'` });
    }
    if (!byId.has(artifact.id)) byId.set(artifact.id, []);
    byId.get(artifact.id).push(artifact);
    if (!artifact.metadata.title) {
      findings.push({ path: artifact.relative, field: "title", message: "required field is missing" });
    }
    const basename = path.basename(artifact.file);
    const legacyRepFilename = /^RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-/.test(artifact.id) && basename === `${artifact.id}.md`;
    if (!basename.startsWith(`${artifact.id}--`) && !legacyRepFilename) {
      findings.push({
        path: artifact.relative,
        field: "id",
        message: `filename must start with '${artifact.id}--'`
      });
    }
    const kind = prefix(artifact.id);
    const status = artifact.metadata.status;
    if (status && ALLOWED_STATUS[kind] && !ALLOWED_STATUS[kind].has(status)) {
      findings.push({ path: artifact.relative, field: "status", message: `'${status}' is not allowed for ${kind}` });
    }
    const confidence = artifact.metadata.confidence;
    if (typeof confidence === "string" && !CONFIDENCE.has(confidence)) {
      findings.push({ path: artifact.relative, field: "confidence", message: `unknown label '${confidence}'` });
    }
  }
  for (const [identifier, records] of byId) {
    if (records.length > 1) {
      const paths = records.map((record) => record.relative).join(", ");
      for (const record of records) {
        findings.push({
          path: record.relative,
          field: "id",
          message: `duplicate '${identifier}' also in ${paths}`
        });
      }
    }
  }
  const known = new Set(byId.keys());
  for (const artifact of loaded.artifacts) {
    for (const field of REFERENCE_FIELDS) {
      for (const target of references(artifact.metadata[field])) {
        if (!known.has(target)) {
          findings.push({ path: artifact.relative, field, message: `broken reference '${target}'` });
        }
        if (target === artifact.id && ["supersedes", "superseded_by"].includes(field)) {
          findings.push({ path: artifact.relative, field, message: "artifact cannot supersede itself" });
        }
      }
    }
    for (const target of references(artifact.metadata.supersedes)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.superseded_by).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "supersedes", message: `'${target}' is not reciprocal` });
      }
    }
    for (const target of references(artifact.metadata.superseded_by)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.supersedes).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "superseded_by", message: `'${target}' is not reciprocal` });
      }
    }
  }
  if (checkRegistries) findings.push(...registryFindings(root, loaded.artifacts));
  findings.push(...workFindings(root));
  findings.push(...queueFindings(root));
  findings.push(...telemetryFindings(root));
  return findings.sort((a, b) =>
    [a.path, a.field, a.message].join("\0").localeCompare([b.path, b.field, b.message].join("\0"))
  );
}

export function buildRegistries(root, { dryRun = false } = {}) {
  // F# shadows this same lease protocol. Acquire before discovery so a writer
  // observes one coherent artifact snapshot from projection through writes.
  return withFileLock(root, ARTIFACT_REGISTRY_RESOURCE, () => {
    if (!dryRun) recoverArtifactRegistryTransaction(root);
    const loaded = loadArtifacts(root);
    if (loaded.findings.length) return { changed: 0, findings: loaded.findings };
    let changed = 0;
    for (const [file, content] of renderedRegistries(root, loaded.artifacts)) {
      const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
      if (actual === content) continue;
      changed += 1;
      console.log(`${dryRun ? "WOULD WRITE" : "WROTE"} ${path.relative(root, file).split(path.sep).join("/")}`);
      if (!dryRun) {
        // The journal is prepared below for the complete declared write set;
        // this loop only reports changes before the atomic replacements occur.
      }
    }
    if (!dryRun && changed) {
      const writes = [...renderedRegistries(root, loaded.artifacts)]
        .filter(([file, content]) => (fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null) !== content)
        .map(([file, content]) => ({ path: path.relative(root, file).split(path.sep).join("/"), content }));
      prepareArtifactRegistryTransaction(root, writes);
      for (const write of writes) writeTextAtomic(path.join(root, write.path), write.content);
      fs.unlinkSync(artifactRegistryTransactionPath(root));
    }
    return { changed, findings: [] };
  });
}

function renderFinding(finding) {
  const location = finding.field ? `${finding.path}:${finding.field}` : finding.path;
  return `${location}: ${finding.message}`;
}

function findingRecord(finding) {
  const repair = finding.message.includes("registry is stale")
    ? "Run './ros registry build'."
    : finding.field === "work_items"
      ? "Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence."
      : "Correct the named file and field, then run './ros validate' again.";
  return { severity: "error", path: finding.path, field: finding.field || null, message: finding.message, repair };
}

export function statusView(root) {
  const context = contextView(root);
  const findings = validate(root);
  return {
    repository: workConfig(root).repository,
    protocolVersion: workConfig(root).protocolVersion,
    validation: findings.length ? "failed" : "passed",
    findingCount: findings.length,
    workItems: context.workItems.map(({ id, type, state, semanticState, allowedActions, telemetryExecutionIds }) => ({ id, type, state, semanticState, allowedActions, telemetryExecutionIds: telemetryExecutionIds ?? [] })),
    telemetry: {
      executionCount: showTelemetry(root).length,
      activeExecutionCount: showTelemetry(root).filter((record) => record.status === "active").length
    },
    nextActions: findings.length ? [...new Set(findings.map((item) => findingRecord(item).repair))] : ["Select an allowed work transition or begin a new work item."]
  };
}

function parseCli(argv) {
  let root = process.cwd();
  const args = [...argv];
  const rootIndex = args.indexOf("--root");
  if (rootIndex >= 0) {
    if (!args[rootIndex + 1]) throw new Error("--root requires a value");
    root = path.resolve(args[rootIndex + 1]);
    args.splice(rootIndex, 2);
  }
  return { root: path.resolve(root), args };
}

function option(args, name) {
  const index = args.indexOf(name);
  if (index < 0) return undefined;
  if (!args[index + 1] || args[index + 1].startsWith("--")) throw new Error(`${name} requires a value`);
  return args[index + 1];
}

function options(args, name) {
  const values = [];
  for (let index = 0; index < args.length; index += 1) {
    if (args[index] !== name) continue;
    if (!args[index + 1] || args[index + 1].startsWith("--")) throw new Error(`${name} requires a value`);
    values.push(args[index + 1]);
    index += 1;
  }
  return values;
}

function telemetryIdentityOptions(args) {
  return {
    provider: option(args, "--provider"),
    model: option(args, "--model"),
    modelVersion: option(args, "--model-version"),
    runtime: option(args, "--runtime"),
    runtimeVersion: option(args, "--runtime-version"),
    sessionId: option(args, "--session"),
    conversationId: option(args, "--conversation"),
    runId: option(args, "--run"),
    agentId: option(args, "--agent"),
    subagentId: option(args, "--subagent"),
    parentExecutionId: option(args, "--parent-execution")
  };
}

function telemetryInput(root, args) {
  const input = option(args, "--input");
  if (!input) throw new Error("--input requires a JSON or JSON Lines file; use '-' for stdin");
  const file = input === "-" ? "-" : path.resolve(root, input);
  return readTelemetryInput(file, configuredTelemetry(root).maxRawPayloadBytes);
}

function telemetryTarget(args) {
  const value = args[2];
  return value && !value.startsWith("--") ? value : undefined;
}

function evidenceOptions(args) {
  const entries = [];
  for (let i = 0; i < args.length; i += 1) if (args[i] === "--evidence") {
    const value = args[++i];
    if (!value?.includes("=")) throw new Error("--evidence requires TYPE=PATH");
    const [type, ...rest] = value.split("="); entries.push({ type, path: rest.join("=") });
  }
  return entries;
}

function idArgs(args) {
  return args.filter((arg, index, all) => !arg.startsWith("-") && (index === 0 || !all[index - 1].startsWith("-")));
}

function tagOptions(args) {
  const tags = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i] === "--tag" || args[i] === "-t") {
      const value = args[i + 1];
      if (!value || value.startsWith("--")) throw new Error(`${args[i]} requires a value`);
      tags.push(...value.split(",").map((tag) => tag.trim()).filter(Boolean));
      i += 1;
    }
  }
  return [...new Set(tags)];
}

function fileOptions(args) {
  const files = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i] === "--file") {
      const value = args[i + 1];
      if (!value || value.startsWith("--")) throw new Error("--file requires PATH or PATH=NAME");
      const separator = value.indexOf("=");
      files.push(separator > 0
        ? { sourcePath: value.slice(0, separator), name: value.slice(separator + 1) }
        : { sourcePath: value, name: undefined });
      i += 1;
    }
  }
  return files;
}

const ACTION_ALIASES = { done: "complete" };

export function main(argv) {
  try {
    const { root, args } = parseCli(argv);
    if (args[0] === "add") {
      const title = args[1];
      if (!title || title.startsWith("--")) throw new Error('add requires a title, e.g. ros add "Title"');
      const item = captureWork(root, title, {
        tags: tagOptions(args.slice(2)),
        priority: option(args, "--priority"),
        id: option(args, "--id"),
        actor: option(args, "--actor"),
        source: option(args, "--source"),
        sourceReference: option(args, "--source-reference"),
        description: option(args, "--description"),
        files: fileOptions(args.slice(2))
      });
      console.log(JSON.stringify(item, null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "context") {
      console.log(JSON.stringify(contextView(root, args[2]), null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "update") {
      const id = args[2];
      if (!id) throw new Error("work update requires an ID");
      const rest = args.slice(3);
      const hasTagFlag = rest.includes("--tag") || rest.includes("-t");
      updateWork(root, id, {
        title: option(args, "--title"),
        description: option(args, "--description"),
        tags: hasTagFlag ? tagOptions(rest) : undefined,
        priority: option(args, "--priority")
      });
      console.log(JSON.stringify(showWork(root, id), null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "attach") {
      const id = args[2];
      if (!id) throw new Error("work attach requires an ID");
      const files = fileOptions(args.slice(3));
      if (!files.length) throw new Error("attach requires at least one --file PATH[=NAME]");
      for (const file of files) attachFile(root, id, file);
      console.log(JSON.stringify(showWork(root, id), null, 2)); return 0;
    }
    if (args[0] === "work" && (!args[1] || args[1] === "list")) {
      const rest = args.slice(2);
      const tags = tagOptions(rest);
      const status = option(rest, "--status");
      console.log(JSON.stringify(mergedWorkView(root, { tags: tags.length ? tags : undefined, status }), null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "ready") {
      const rest = args.slice(2);
      const ids = idArgs(rest);
      if (!ids.length) {
        const tags = tagOptions(rest);
        console.log(JSON.stringify(mergedWorkView(root, { tags: tags.length ? tags : undefined, status: "ready" }), null, 2)); return 0;
      }
      const results = ids.map((id) => backlogTransition(root, "ready", id));
      console.log(JSON.stringify(results, null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "show") {
      console.log(JSON.stringify(showWork(root, args[2]), null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "start") {
      const ids = idArgs(args.slice(2));
      const result = startWork(root, ids, {
        type: option(args, "--type"), actor: option(args, "--actor"),
        telemetryIdentity: telemetryIdentityOptions(args), classifications: options(args, "--classification")
      });
      console.log(JSON.stringify({ workItems: result.context.workItems, events: result.events.map((event) => event.eventId) }, null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "abandon") {
      const ids = idArgs(args.slice(2));
      const reason = option(args, "--reason");
      const results = ids.map((id) => backlogTransition(root, "abandon", id, { reason }));
      console.log(JSON.stringify(results, null, 2)); return 0;
    }
    if (args[0] === "work" && args[1] === "block") {
      const ids = idArgs(args.slice(2));
      console.log(JSON.stringify(blockWork(root, ids, { reason: option(args, "--reason") }), null, 2)); return 0;
    }
    if (args[0] === "work" && ["begin", "resume", "complete", "done"].includes(args[1])) {
      const action = ACTION_ALIASES[args[1]] ?? args[1];
      const ids = idArgs(args.slice(2));
      const result = transition(root, action, ids, {
        type: option(args, "--type"), actor: option(args, "--actor"), reason: option(args, "--reason"),
        localState: option(args, "--local-state"), conclusion: option(args, "--conclusion"), evidence: evidenceOptions(args),
        telemetryIdentity: telemetryIdentityOptions(args), classifications: options(args, "--classification")
      });
      console.log(JSON.stringify({ workItems: result.context.workItems, events: result.events.map((event) => event.eventId) }, null, 2)); return 0;
    }
    if (args[0] === "adapter" && args[1] === "publish") {
      const target = option(args, "--target");
      if (!target) throw new Error("adapter publish requires --target");
      const source = eventsPath(root);
      const events = fs.existsSync(source) ? fs.readFileSync(source, "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
      const destination = path.resolve(root, target);
      const published = fs.existsSync(destination) ? fs.readFileSync(destination, "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
      const known = new Set(published.map((event) => event.eventId));
      const additions = events.filter((event) => !known.has(event.eventId));
      fs.mkdirSync(path.dirname(destination), { recursive: true });
      for (const event of additions) fs.appendFileSync(destination, `${JSON.stringify(event)}\n`, "utf8");
      const receiptsFile = path.join(root, ".ros", "publications.json");
      const receipts = readJson(receiptsFile, {});
      const publishedAt = new Date().toISOString();
      for (const event of events) receipts[event.eventId] = { status: "success", target, publishedAt };
      writeJson(receiptsFile, receipts);
      console.log(`published ${additions.length} event(s); ${events.length - additions.length} duplicate(s) skipped`); return 0;
    }
    if (args[0] === "adapter" && args[1] === "call") {
      const store = option(args, "--store");
      const requestFile = option(args, "--request");
      if (!store || !requestFile) throw new Error("adapter call requires --store and --request");
      const request = readJson(path.resolve(root, requestFile));
      if (!request) throw new Error(`adapter request not found: ${requestFile}`);
      const result = callFileAdapter(path.resolve(root, store), request);
      console.log(JSON.stringify(result, null, 2));
      return result.outcome === "failure" ? 1 : result.outcome === "unknown" ? 2 : 0;
    }
    if (args[0] === "telemetry" && args[1] === "adapters") {
      console.log(JSON.stringify(TELEMETRY_ADAPTERS, null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "start") {
      const workItemId = telemetryTarget(args);
      if (!workItemId) throw new Error("telemetry start requires a work-item ID");
      const record = withWorkProtocol(root, () => {
        const context = loadContext(root);
        const contextItem = context.workItems.find((item) => item.id === workItemId);
        if (!contextItem || !["active", "blocked"].includes(contextItem.semanticState)) throw new Error(`work item '${workItemId}' must be active or blocked before starting telemetry`);
        const execution = recoverOrStartExecution(root, contextItem, ["active"], {
          executionId: option(args, "--execution-id"),
          workType: contextItem.type,
          classifications: options(args, "--classification"),
          classificationRationale: option(args, "--classification-rationale"),
          identity: telemetryIdentityOptions(args)
        });
        if (!execution) return null;
        contextItem.telemetryExecutionIds ??= [];
        if (!contextItem.telemetryExecutionIds.includes(execution.executionId)) contextItem.telemetryExecutionIds.push(execution.executionId);
        context.updatedAt = new Date().toISOString();
        commitWorkState(root, [
          { path: ".ros/events/events.jsonl", content: renderedEventLog(root, []) },
          { path: ".ros/context/current.json", content: `${JSON.stringify(context, null, 2)}\n` }
        ]);
        return execution;
      });
      if (!args.includes("--quiet")) console.log(JSON.stringify(record, null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "ingest") {
      const record = ingestTelemetry(root, telemetryTarget(args), telemetryInput(root, args), { adapter: option(args, "--adapter") ?? "generic" });
      if (!args.includes("--quiet")) console.log(JSON.stringify(record, null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "classify") {
      const classifications = options(args, "--classification");
      if (!classifications.length) throw new Error("telemetry classify requires at least one --classification");
      const rdFile = option(args, "--rd-context");
      const rd = rdFile ? readTelemetryInput(path.resolve(root, rdFile), configuredTelemetry(root).maxRawPayloadBytes) : null;
      const record = ingestTelemetry(root, telemetryTarget(args), {
        snapshotId: `classification-${Date.now()}`,
        classification: {
          types: classifications,
          rationale: option(args, "--rationale") ?? null,
          evidence: options(args, "--evidence-link"),
          rd
        },
        raw: {}
      });
      if (!args.includes("--quiet")) console.log(JSON.stringify(record.classification, null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "record") {
      const id = option(args, "--metric");
      const rawValue = option(args, "--value");
      if (!id || rawValue === undefined) throw new Error("telemetry record requires --metric and --value");
      const quality = option(args, "--quality") ?? "observed";
      const confidenceOption = option(args, "--confidence");
      const confidence = confidenceOption === undefined || confidenceOption === null
        ? null
        : Number.isFinite(Number(confidenceOption)) ? Number(confidenceOption) : confidenceOption;
      const pricingSource = option(args, "--pricing-source");
      const pricingVersion = option(args, "--pricing-version");
      const record = recordTelemetryMetric(root, telemetryTarget(args), {
        id,
        value: Number(rawValue),
        unit: option(args, "--unit"),
        currency: option(args, "--currency"),
        quality,
        confidence,
        scope: option(args, "--scope") ?? "execution",
        source: {
          type: option(args, "--source-type") ?? "agent-report",
          name: option(args, "--source-name") ?? "ros-telemetry-cli",
          mechanism: option(args, "--mechanism") ?? "explicit-metric-record"
        },
        pricing: pricingSource || pricingVersion ? { source: pricingSource ?? null, version: pricingVersion ?? null } : null,
        collectedAt: option(args, "--collected-at")
      });
      if (!args.includes("--quiet")) console.log(JSON.stringify(record.metrics.at(-1), null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "finalize") {
      const inputFile = option(args, "--input");
      const record = finalizeExecution(root, telemetryTarget(args), {
        input: inputFile ? telemetryInput(root, args) : undefined,
        adapter: option(args, "--adapter") ?? "generic"
      });
      if (!args.includes("--quiet")) console.log(JSON.stringify(record, null, 2)); return 0;
    }
    if (args[0] === "telemetry" && args[1] === "show") {
      console.log(JSON.stringify(showTelemetry(root, telemetryTarget(args)), null, 2)); return 0;
    }
    if (args[0] === "telemetry" && (args[1] === "summary" || args[1] === "summarize")) {
      console.log(JSON.stringify(summarizeTelemetry(root, telemetryTarget(args)), null, 2)); return 0;
    }
    if (args[0] === "validate") {
      const findings = validate(root);
      if (args.includes("--json")) {
        console.log(JSON.stringify({ valid: findings.length === 0, findings: findings.map(findingRecord) }, null, 2));
        return findings.length ? 1 : 0;
      }
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}\n  REPAIR ${findingRecord(finding).repair}`);
        console.error(`validation failed with ${findings.length} error(s)`);
        return 1;
      }
      console.log("validation passed");
      return 0;
    }
    if (args[0] === "status") {
      console.log(JSON.stringify(statusView(root), null, 2));
      return 0;
    }
    if (args[0] === "registry" && args[1] === "build") {
      const result = buildRegistries(root, { dryRun: args.includes("--dry-run") });
      if (result.findings.length) {
        for (const finding of result.findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log(`${result.changed} registry file(s) ${args.includes("--dry-run") ? "would change" : "changed"}`);
      return 0;
    }
    if (args[0] === "registry" && args[1] === "check") {
      const loaded = loadArtifacts(root);
      const findings = [...loaded.findings, ...registryFindings(root, loaded.artifacts)];
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log("registries are current");
      return 0;
    }
    console.error("Usage: ros [--root PATH] validate [--json] | status | registry build [--dry-run] | registry check | add TITLE ... | work [list|ready|show|start|block|abandon|update|attach|begin|resume|complete|done|context] ... | telemetry [start|ingest|record|classify|finalize|show|summary|adapters] ... | adapter call ... | adapter publish ...");
    return 2;
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
