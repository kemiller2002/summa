#!/usr/bin/env node
// Thin HTTP adapter over the hub kernel in ros_hub_cli.mjs. Like
// ros_server.mjs, this owns no domain logic -- every route calls straight
// into the same functions the hub CLI uses, and every mutation the hub
// performs on a spoke repository goes through that repository's own `./ros`
// (never a direct file write), so a spoke repo behaves identically whether
// its work item was created by a human at its own command line or through
// this hub.

import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

import {
  createWorkInRepo,
  listRepos,
  listWorkAcrossRepos,
  registerRepo,
  unregisterRepo
} from "./ros_hub_cli.mjs";
import { parseRequestBody as parseBody } from "./http_body.mjs";

const WEB_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "web-hub");
const MAX_BODY_BYTES = 1_000_000;
const MAX_UPLOAD_BYTES = 25_000_000;

const CONTENT_TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8"
};

function parseRequestBody(req) {
  return parseBody(req, { maxJsonBytes: MAX_BODY_BYTES, maxUploadBytes: MAX_UPLOAD_BYTES });
}

function sendJson(res, status, value) {
  const body = JSON.stringify(value, null, 2);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": Buffer.byteLength(body) });
  res.end(body);
}

function tagsParam(query) {
  const tags = query.getAll("tag").flatMap((value) => value.split(",")).map((tag) => tag.trim()).filter(Boolean);
  return tags.length ? tags : undefined;
}

function tagsFromField(value) {
  if (!value) return [];
  return value.split(",").map((tag) => tag.trim()).filter(Boolean);
}

// Uploaded bytes only ever touch disk briefly, as a named temp file passed
// to the spoke's own `ros add --file`; the spoke's attachFile() reads and
// copies it into its own .ros/work/attachments/, then this is deleted.
function withTempFiles(files, fn) {
  const written = files.map((file) => {
    const tempPath = path.join(os.tmpdir(), `ros-hub-upload-${randomBytes(8).toString("hex")}-${file.filename}`);
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

function createWorkHandler(root, [repoId], query, body) {
  const isMultipart = "fields" in body;
  const fields = isMultipart ? body.fields : body;
  const files = isMultipart ? body.files : [];
  return withTempFiles(files, (tempFiles) => createWorkInRepo(root, repoId, {
    title: fields.title,
    tags: isMultipart ? tagsFromField(fields.tags) : (Array.isArray(fields.tags) ? fields.tags : []),
    priority: fields.priority,
    description: fields.description,
    id: fields.id,
    actor: fields.actor,
    files: tempFiles
  }));
}

const ROUTES = [
  { method: "GET", pattern: /^\/api\/repos$/, handler: (root) => listRepos(root) },
  {
    method: "POST", pattern: /^\/api\/repos$/,
    handler: (root, params, query, body) => registerRepo(root, body.path, { name: body.name })
  },
  {
    method: "DELETE", pattern: /^\/api\/repos\/([^/]+)$/,
    handler: (root, [id]) => unregisterRepo(root, id)
  },
  {
    method: "GET", pattern: /^\/api\/work$/,
    handler: (root, params, query) => listWorkAcrossRepos(root, { repoId: query.get("repo") ?? undefined, tags: tagsParam(query), status: query.get("status") ?? undefined })
  },
  {
    method: "POST", pattern: /^\/api\/repos\/([^/]+)\/work$/,
    handler: createWorkHandler
  }
];

function matchRoute(method, pathname) {
  for (const route of ROUTES) {
    if (route.method !== method) continue;
    const match = route.pattern.exec(pathname);
    if (match) return { route, params: match.slice(1) };
  }
  return null;
}

async function handleApi(req, res, root, url) {
  const matched = matchRoute(req.method, url.pathname);
  if (!matched) { sendJson(res, 404, { error: `no route for ${req.method} ${url.pathname}` }); return; }
  try {
    const body = (req.method === "POST") ? await parseRequestBody(req) : {};
    const result = matched.route.handler(root, matched.params, url.searchParams, body);
    sendJson(res, 200, result);
  } catch (error) {
    sendJson(res, 400, { error: error.message });
  }
}

function serveStatic(req, res, url) {
  const relative = url.pathname === "/" ? "/index.html" : url.pathname;
  const resolved = path.resolve(WEB_ROOT, `.${relative}`);
  if (!resolved.startsWith(WEB_ROOT + path.sep) && resolved !== WEB_ROOT) {
    res.writeHead(403); res.end("forbidden"); return;
  }
  fs.readFile(resolved, (error, content) => {
    if (error) { res.writeHead(404); res.end("not found"); return; }
    const type = CONTENT_TYPES[path.extname(resolved)] ?? "application/octet-stream";
    res.writeHead(200, { "Content-Type": type });
    res.end(content);
  });
}

export function createServer(root) {
  return http.createServer((req, res) => {
    const url = new URL(req.url, `http://${req.headers.host ?? "localhost"}`);
    if (url.pathname.startsWith("/api/")) { handleApi(req, res, root, url); return; }
    if (req.method !== "GET") { res.writeHead(405); res.end("method not allowed"); return; }
    serveStatic(req, res, url);
  });
}

function parseArgs(argv) {
  const options = { port: 4320, host: "127.0.0.1", root: process.cwd() };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--port") options.port = Number(argv[++i]);
    else if (argv[i] === "--host") options.host = argv[++i];
    else if (argv[i] === "--root") options.root = path.resolve(argv[++i]);
  }
  return options;
}

function main(argv) {
  const options = parseArgs(argv);
  const server = createServer(options.root);
  server.listen(options.port, options.host, () => {
    console.log(`ROS hub: http://${options.host}:${options.port} (hub root: ${options.root})`);
    console.log("Bound to localhost by default; this server has no authentication and can create work items and run commands in every registered repository -- do not expose it beyond your own machine without adding one.");
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}
