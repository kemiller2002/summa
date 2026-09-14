import { execFileSync } from "node:child_process";

const DELTA_NAMES = new Map([
  [" ", "unmodified"],
  ["A", "added"],
  ["M", "modified"],
  ["D", "deleted"],
  ["R", "renamed"],
  ["C", "copied"],
  ["T", "type-changed"],
  ["U", "unmerged"]
]);

function deltaName(value) {
  return DELTA_NAMES.get(value) ?? `unknown:${value}`;
}

function compareText(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function failure(operation, reason, message, exitCode = null) {
  return { operation, reason, message, exitCode };
}

function errorText(error) {
  const stderr = Buffer.isBuffer(error?.stderr) ? error.stderr.toString("utf8") : error?.stderr;
  return String(stderr || error?.message || "git command failed").trim();
}

function commandFailure(operation, error) {
  const message = errorText(error);
  const reason = error?.code === "ENOENT"
    ? "tool-unavailable"
    : message.toLowerCase().includes("not a git repository")
      ? "not-repository"
      : "command-failed";
  return failure(operation, reason, message, Number.isInteger(error?.status) ? error.status : null);
}

export function parseGitStatus(output) {
  if (!output) return { outcome: "clean", changes: [], failure: null };
  if (!output.endsWith("\0")) {
    return {
      outcome: "unavailable",
      changes: [],
      failure: failure("parse git status", "malformed-output", "porcelain-v1 -z output did not end with a NUL delimiter")
    };
  }

  const fields = output.split("\0");
  fields.pop();
  const changes = [];
  for (let index = 0; index < fields.length; index += 1) {
    const entry = fields[index];
    if (entry.length < 3 || entry[2] !== " ") {
      return {
        outcome: "unavailable",
        changes: [],
        failure: failure("parse git status", "malformed-output", "porcelain-v1 entry did not contain a two-character status and path")
      };
    }
    const code = entry.slice(0, 2);
    const changedPath = entry.slice(3);
    if (!changedPath) {
      return {
        outcome: "unavailable",
        changes: [],
        failure: failure("parse git status", "malformed-output", "porcelain-v1 entry contained an empty path")
      };
    }
    const hasOrigin = /[RC]/.test(code);
    const originalPath = hasOrigin ? fields[++index] : null;
    if (hasOrigin && !originalPath) {
      return {
        outcome: "unavailable",
        changes: [],
        failure: failure("parse git status", "malformed-output", `rename/copy entry '${changedPath}' did not include its original path`)
      };
    }
    changes.push({
      code,
      kind: code === "??" ? "untracked" : code === "!!" ? "ignored" : "tracked",
      ...(code === "??" || code === "!!" ? {} : { index: deltaName(code[0]), workTree: deltaName(code[1]) }),
      path: changedPath,
      originalPath
    });
  }
  changes.sort((left, right) => compareText(left.path, right.path) || compareText(left.originalPath ?? "", right.originalPath ?? ""));
  return { outcome: changes.length ? "changed" : "clean", changes, failure: null };
}

export function runGitText(root, args, options = {}) {
  const operation = options.operation ?? `git ${args[0] ?? "command"}`;
  try {
    const value = execFileSync(options.executable ?? "git", ["-C", root, ...args], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
    return { outcome: "success", value: options.trim === false ? value : value.trim(), failure: null };
  } catch (error) {
    return { outcome: "unavailable", value: null, failure: commandFailure(operation, error) };
  }
}

export function observeGitStatus(root, options = {}) {
  const result = runGitText(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], {
    ...options,
    operation: "git status",
    trim: false
  });
  const observation = result.outcome === "success"
    ? parseGitStatus(result.value)
    : { outcome: "unavailable", changes: [], failure: result.failure };
  return {
    schemaVersion: "1.0.0",
    ...observation,
    source: { tool: "git", command: "status", format: "porcelain-v1-z" }
  };
}

export function requireGitText(result) {
  if (result.outcome === "success") return result.value;
  const error = new Error(`${result.failure.operation} unavailable: ${result.failure.message}`);
  error.gitFailure = result.failure;
  throw error;
}
