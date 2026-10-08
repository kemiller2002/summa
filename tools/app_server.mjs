// Serves the accounting application (app/) for local use and the browser
// suite: static files only. The application keeps its books in the browser
// (Limen's Storage effect) when the deployment names no data location, so
// there is no API here. Owns no domain logic.
//
//   npm run app            # http://127.0.0.1:4330
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { REPOSITORY_ROOT, staticHandler } from "./web_static.mjs";

const serveStatic = staticHandler(path.join(REPOSITORY_ROOT, "app"));

export function createServer() {
  return http.createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (req.method !== "GET" && req.method !== "HEAD") {
      res.writeHead(405, { Allow: "GET, HEAD" });
      res.end("method not allowed");
      return;
    }
    serveStatic(req, res, url);
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT ?? 4330);
  createServer().listen(port, "127.0.0.1", () => {
    console.log(`Summa accounting: http://127.0.0.1:${port}`);
  });
}
