import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const LOCK_RETRY_MS = 20;
const LOCK_TIMEOUT_MS = 10_000;
const LOCK_STALE_MS = 60_000;
const sleepState = new Int32Array(new SharedArrayBuffer(4));

export function readJson(file, fallback = null) {
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : fallback;
}

export function writeTextAtomic(file, text) {
  const directory = path.dirname(file);
  fs.mkdirSync(directory, { recursive: true });
  const temporary = path.join(directory, `.${path.basename(file)}.${process.pid}.${crypto.randomBytes(6).toString("hex")}.tmp`);
  try {
    fs.writeFileSync(temporary, text, { encoding: "utf8", flag: "wx" });
    fs.renameSync(temporary, file);
  } finally {
    try {
      fs.unlinkSync(temporary);
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
    }
  }
}

export function writeJson(file, value) {
  writeTextAtomic(file, `${JSON.stringify(value, null, 2)}\n`);
}

function lockFile(root, resource) {
  const directory = path.join(root, ".ros", "locks");
  fs.mkdirSync(directory, { recursive: true });
  const name = crypto.createHash("sha256").update(resource).digest("hex");
  return path.join(directory, `${name}.lock`);
}

function processIsAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return null;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    if (error.code === "EPERM") return true;
    return null;
  }
}

function removeAbandonedLock(file) {
  try {
    const stat = fs.statSync(file);
    const observed = fs.readFileSync(file, "utf8");
    let owner = null;
    try {
      owner = JSON.parse(observed);
    } catch {
      // A creator may still be writing the lock metadata; age remains the safe fallback.
    }
    const alive = processIsAlive(owner?.pid);
    if (alive === true) return false;
    if (alive === null && Date.now() - stat.mtimeMs <= LOCK_STALE_MS) return false;
    if (fs.readFileSync(file, "utf8") !== observed) return false;
    fs.unlinkSync(file);
    return true;
  } catch (error) {
    if (error.code === "ENOENT") return true;
    throw error;
  }
}

export function withFileLock(root, resource, operation) {
  const file = lockFile(root, resource);
  const started = Date.now();
  const ownerToken = crypto.randomBytes(16).toString("hex");
  let descriptor;
  while (descriptor === undefined) {
    try {
      const candidate = fs.openSync(file, "wx");
      try {
        fs.writeFileSync(candidate, `${JSON.stringify({ pid: process.pid, ownerToken, resource, acquiredAt: new Date().toISOString() })}\n`, "utf8");
        descriptor = candidate;
      } catch (error) {
        try {
          fs.closeSync(candidate);
        } finally {
          try {
            fs.unlinkSync(file);
          } catch (unlinkError) {
            if (unlinkError.code !== "ENOENT") throw unlinkError;
          }
        }
        throw error;
      }
    } catch (error) {
      if (error.code !== "EEXIST") throw error;
      if (removeAbandonedLock(file)) continue;
      if (Date.now() - started >= LOCK_TIMEOUT_MS) throw new Error(`timed out waiting for ROS lock '${resource}'`);
      Atomics.wait(sleepState, 0, 0, LOCK_RETRY_MS);
    }
  }
  try {
    return operation();
  } finally {
    try {
      fs.closeSync(descriptor);
    } finally {
      try {
        const owner = JSON.parse(fs.readFileSync(file, "utf8"));
        if (owner.ownerToken === ownerToken) fs.unlinkSync(file);
      } catch (error) {
        if (error.code !== "ENOENT") throw error;
      }
    }
  }
}
