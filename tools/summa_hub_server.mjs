#!/usr/bin/env node
// Summa-owned HTTP hub (DF-SUMMA-PROV-2026-0001): the tool-owned
// tools/ros_hub_server.mjs serves every route except work creation, which is
// handled here with explicit identity propagation (tools/summa_hub.mjs).
// The request body declares the requester (`actor` legacy string, or
// `actorJson` + `execution`); this server's own environment is never taken as
// the requester. The hub's own actor is the operator declared at startup
// (--hub-actor-json, e.g. the human using the UI) or automation
// `echelon/summa-hub`.

import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

import { createServer as createRosHubServer } from "./ros_hub_server.mjs";
import { parseRequestBody } from "./http_body.mjs";
import { createWorkWithIdentity } from "./summa_hub.mjs";
import { resolveHubActor } from "../lib/hub-identity.mjs";

const CREATE_WORK = /^\/api\/repos\/([^/]+)\/work$/;

function sendJson(res, status, value) {
  const body = JSON.stringify(value, null, 2);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": Buffer.byteLength(body) });
  res.end(body);
}

function jsonField(value, name) {
  if (value === undefined || value === "") return undefined;
  if (typeof value === "object") return value;
  try { return JSON.parse(value); } catch { throw new Error(`${name} must be a JSON object`); }
}

function withTempFiles(files, fn) {
  const written = files.map((file) => {
    const tempPath = path.join(os.tmpdir(), `summa-hub-upload-${randomBytes(8).toString("hex")}-${path.basename(file.filename)}`);
    fs.writeFileSync(tempPath, file.data);
    return { sourcePath: tempPath, name: file.filename };
  });
  try {
    return fn(written);
  } finally {
    for (const file of written) {
      try { fs.unlinkSync(file.sourcePath); } catch { /* best-effort cleanup */ }
    }
  }
}

async function createWork(req, res, root, repoId, hubActor, hubEnv) {
  try {
    const body = await parseRequestBody(req, { maxJsonBytes: 1_000_000, maxUploadBytes: 25_000_000 });
    const isMultipart = "fields" in body;
    const fields = isMultipart ? body.fields : body;
    const tags = isMultipart
      ? (fields.tags ? fields.tags.split(",").map((tag) => tag.trim()).filter(Boolean) : [])
      : (Array.isArray(fields.tags) ? fields.tags : []);
    const result = withTempFiles(isMultipart ? body.files : [], (files) => createWorkWithIdentity(root, repoId, {
      title: fields.title,
      tags,
      priority: fields.priority,
      description: fields.description,
      id: fields.id,
      actor: fields.actor,
      requesterActor: jsonField(fields.actorJson, "actorJson"),
      requesterExecution: fields.execution || undefined,
      files
    }, { hubActor, hubEnv }));
    sendJson(res, 200, result);
  } catch (error) {
    sendJson(res, 400, { error: error.message });
  }
}

export function createServer(root, { hubActor, hubEnv = process.env } = {}) {
  const hub = resolveHubActor(hubActor);
  if (!hub.ok) throw new Error(hub.error);
  const delegate = createRosHubServer(root).listeners("request")[0];
  return http.createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host ?? "localhost"}`);
    const match = req.method === "POST" ? CREATE_WORK.exec(url.pathname) : null;
    if (match) { createWork(req, res, root, decodeURIComponent(match[1]), hub.actor, hubEnv); return; }
    delegate(req, res);
  });
}

function parseArgs(argv) {
  const options = { port: 4320, host: "127.0.0.1", root: process.cwd() };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--port") options.port = Number(argv[++i]);
    else if (argv[i] === "--host") options.host = argv[++i];
    else if (argv[i] === "--root") options.root = path.resolve(argv[++i]);
    else if (argv[i] === "--hub-actor-json") options.hubActor = JSON.parse(argv[++i]);
  }
  return options;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseArgs(process.argv.slice(2));
    const server = createServer(options.root, { hubActor: options.hubActor });
    server.listen(options.port, options.host, () => {
      console.log(`Summa hub: http://${options.host}:${options.port} (hub root: ${options.root})`);
      console.log("Bound to localhost by default; this server has no authentication. Identity in requests is self-reported provenance, not authentication.");
    });
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    process.exitCode = 1;
  }
}
