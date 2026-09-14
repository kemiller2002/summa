#!/usr/bin/env node
// Thin HTTP adapter over the ROS work backlog/protocol kernel in ros_cli.mjs.
// It owns no domain logic of its own: every handler below is a direct call
// into the same functions the CLI uses, so the CLI and the web UI can never
// drift into two different sources of truth for legality/state.

import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

import {
  attachFile,
  attachmentFilePath,
  backlogTransition,
  blockWork,
  captureWork,
  mergedWorkView,
  showWork,
  startWork,
  statusView,
  transition,
  updateWork,
  validate
} from "./ros_cli.mjs";
import { parseRequestBody as parseBody } from "./http_body.mjs";

const WEB_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "web");
const MAX_BODY_BYTES = 1_000_000;
const MAX_UPLOAD_BYTES = 25_000_000;

const CONTENT_TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
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

// Route table: each entry is a pure (root, params, query, body) -> result
// function. The HTTP layer below only marshals request/response; it makes
// no decisions about work-item legality.
const ROUTES = [
  {
    method: "GET", pattern: /^\/api\/work$/,
    handler: (root, params, query) => mergedWorkView(root, { tags: tagsParam(query), status: query.get("status") ?? undefined })
  },
  {
    method: "GET", pattern: /^\/api\/work\/ready$/,
    handler: (root, params, query) => mergedWorkView(root, { tags: tagsParam(query), status: "ready" })
  },
  {
    method: "GET", pattern: /^\/api\/work\/([^/]+)$/,
    handler: (root, [id]) => showWork(root, id)
  },
  {
    method: "POST", pattern: /^\/api\/work$/,
    handler: (root, params, query, body) => captureWork(root, body.title, {
      tags: Array.isArray(body.tags) ? body.tags : [],
      priority: body.priority,
      id: body.id,
      actor: body.actor,
      source: body.source,
      sourceReference: body.sourceReference,
      description: body.description
    })
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/ready$/,
    handler: (root, [id]) => { backlogTransition(root, "ready", id); return showWork(root, id); }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/block$/,
    handler: (root, [id], query, body) => { blockWork(root, [id], { reason: body.reason }); return showWork(root, id); }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/abandon$/,
    handler: (root, [id], query, body) => { backlogTransition(root, "abandon", id, { reason: body.reason }); return showWork(root, id); }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/update$/,
    handler: (root, [id], query, body) => {
      updateWork(root, id, {
        title: body.title,
        description: body.description,
        tags: Array.isArray(body.tags) ? body.tags : undefined,
        priority: body.priority
      });
      return showWork(root, id);
    }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/attachments$/,
    handler: (root, [id], query, body) => {
      const files = Array.isArray(body.files) ? body.files : [];
      if (!files.length) throw new Error("attachments requires at least one uploaded file");
      for (const file of files) attachFile(root, id, { buffer: file.data, name: file.filename, contentType: file.contentType });
      return showWork(root, id);
    }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/start$/,
    handler: (root, [id], query, body) => {
      startWork(root, [id], { type: body.type, actor: body.actor });
      return showWork(root, id);
    }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/resume$/,
    handler: (root, [id], query, body) => {
      transition(root, "resume", [id], { actor: body.actor });
      return showWork(root, id);
    }
  },
  {
    method: "POST", pattern: /^\/api\/work\/([^/]+)\/complete$/,
    handler: (root, [id], query, body) => {
      transition(root, "complete", [id], {
        actor: body.actor,
        conclusion: body.conclusion,
        evidence: Array.isArray(body.evidence) ? body.evidence : []
      });
      return showWork(root, id);
    }
  },
  {
    method: "GET", pattern: /^\/api\/validate$/,
    handler: (root) => { const findings = validate(root); return { valid: findings.length === 0, findings }; }
  },
  {
    method: "GET", pattern: /^\/api\/status$/,
    handler: (root) => statusView(root)
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
    const body = req.method === "POST" ? await parseRequestBody(req) : {};
    const result = matched.route.handler(root, matched.params, url.searchParams, body);
    sendJson(res, 200, result);
  } catch (error) {
    sendJson(res, 400, { error: error.message });
  }
}

const ATTACHMENT_DOWNLOAD_PATTERN = /^\/api\/work\/([^/]+)\/attachments\/([^/]+)$/;

function handleAttachmentDownload(res, root, id, attachmentId) {
  try {
    const { record, filePath } = attachmentFilePath(root, id, attachmentId);
    const data = fs.readFileSync(filePath);
    res.writeHead(200, {
      "Content-Type": record.contentType || "application/octet-stream",
      "Content-Length": data.length,
      "Content-Disposition": `attachment; filename="${record.name.replace(/[\r\n"]/g, "_")}"`
    });
    res.end(data);
  } catch (error) {
    sendJson(res, 404, { error: error.message });
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
    const downloadMatch = req.method === "GET" && ATTACHMENT_DOWNLOAD_PATTERN.exec(url.pathname);
    if (downloadMatch) { handleAttachmentDownload(res, root, downloadMatch[1], downloadMatch[2]); return; }
    if (url.pathname.startsWith("/api/")) { handleApi(req, res, root, url); return; }
    if (req.method !== "GET") { res.writeHead(405); res.end("method not allowed"); return; }
    serveStatic(req, res, url);
  });
}

function parseArgs(argv) {
  const options = { port: 4310, host: "127.0.0.1", root: process.cwd() };
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
    console.log(`ROS web interface: http://${options.host}:${options.port} (repository root: ${options.root})`);
    console.log("Bound to localhost by default; this server has no authentication -- do not expose it beyond your own machine without adding one.");
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2));
}
