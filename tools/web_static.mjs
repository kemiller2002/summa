// Static assets for Summa's two web pages (web/ and web-hub/), shared by
// ros_server.mjs and ros_hub_server.mjs. Owns no domain logic.
//
// A page is served at the server's root. Everything else it loads is
// referenced by its repository-relative location ("../node_modules/...",
// "../web-kernel/...", "../build/wasm/..."), and served from that same
// location here, so a reference means the same file in the checkout and over
// HTTP. Only the directories below are reachable; nothing else in the
// repository (or above it) is served.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const REPOSITORY_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

// The pinned Echelon foundations the pages consume (package.json), the shared
// Limen kernel module, and the published F# engine (`npm run build:wasm`).
export const SHARED_DIRECTORIES = Object.freeze([
  "node_modules/@echelon-foundry/limen/dist",
  "node_modules/@echelon-foundry/design-system/dist",
  "node_modules/@echelon-foundry/print-components/src",
  "web-kernel",
  "build/wasm/wwwroot/_framework"
]);

const CONTENT_TYPES = Object.freeze({
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".wasm": "application/wasm",
  ".dat": "application/octet-stream",
  ".svg": "image/svg+xml"
});

const within = (directory, candidate) => candidate === directory || candidate.startsWith(directory + path.sep);

// The file a request path names, or null when it is outside every served
// directory. Pure: no file-system access.
export function resolveAsset(pageRoot, pathname) {
  const relative = decodeURIComponent(pathname === "/" ? "/index.html" : pathname);
  const fromRepository = path.resolve(REPOSITORY_ROOT, `.${relative}`);
  const shared = SHARED_DIRECTORIES
    .map((directory) => path.join(REPOSITORY_ROOT, directory))
    .find((directory) => within(directory, fromRepository));
  if (shared) return fromRepository === shared ? null : fromRepository;
  const fromPage = path.resolve(pageRoot, `.${relative}`);
  return within(pageRoot, fromPage) && fromPage !== pageRoot ? fromPage : null;
}

export function contentType(file) {
  return CONTENT_TYPES[path.extname(file)] ?? "application/octet-stream";
}

// A request handler serving `pageRoot` plus the shared directories.
export function staticHandler(pageRoot) {
  return (req, res, url) => {
    let file;
    try {
      file = resolveAsset(pageRoot, url.pathname);
    } catch {
      res.writeHead(400); res.end("bad request"); return;
    }
    if (!file) { res.writeHead(403); res.end("forbidden"); return; }
    fs.readFile(file, (error, content) => {
      if (error) { res.writeHead(404); res.end("not found"); return; }
      res.writeHead(200, { "Content-Type": contentType(file) });
      res.end(content);
    });
  };
}
