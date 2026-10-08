// Assembles the static site GitHub Pages serves (WI-0039), from a checkout in
// which `npm ci` and `npm run build:wasm` have run.
//
//   node tools/pages/assemble-site.mjs [output directory, default dist-pages]
//
// Only the accounting application (app/) is published. The backlog and hub
// tools (web/, web-hub/) need the local server's /api/* and are never
// deployed. The page loads everything by relative address (../web-kernel,
// ../node_modules, ../build/wasm), so the site keeps the repository's layout
// and works at any base path. The site root sends the browser to app/.
//
// What the deployment is, is file-driven: deploy/pages/site.json names the
// deployment configuration that replaces app/summa.deployment.json and the
// Content-Security-Policy the page carries. The demo banner is the page's
// own environment banner (SUM0-040), projected from that configuration:
// a local environment named "demo (GitHub Pages)" whose books stay in the
// browser.
//
// Pure functions build the plan; the IO at the bottom carries it out.
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname } from "node:path";

const escapeHtml = (text) =>
  text.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

// The directories the page reaches, copied at the same relative paths.
export const copies = [
  "app",
  "web-kernel",
  "build/wasm/wwwroot",
  "node_modules/@echelon-foundry/limen/dist",
  "node_modules/@echelon-foundry/design-system/dist",
  "node_modules/@echelon-foundry/print-components/src"
];

// Never published: the tools that need the local server's API.
export const excluded = ["web", "web-hub", "tools"];

const cspMeta = (policy) => `<meta http-equiv="Content-Security-Policy" content="${escapeHtml(policy)}" />`;

const insertAfter = (html, pattern, addition) => {
  const match = html.match(pattern);
  if (match === null) throw new Error(`assemble-site: the page has no ${pattern}`);
  const at = match.index + match[0].length;
  return html.slice(0, at) + "\n" + addition + html.slice(at);
};

// The published page: the policy first in <head> (a meta policy covers only
// what follows it). A page that already carries a policy keeps it.
export const publishPage = (html, site) =>
  /http-equiv="Content-Security-Policy"/i.test(html)
    ? html
    : insertAfter(html, /<meta charset="[^"]*"\s*\/?>/i, cspMeta(site.contentSecurityPolicy));

export const rootPage = (site) => `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
${cspMeta(site.contentSecurityPolicy)}
<meta http-equiv="refresh" content="0; url=${site.page}/" />
<link rel="icon" href="data:," />
<title>${escapeHtml(site.title)}</title>
</head>
<body>
<p><a href="${site.page}/">Open ${escapeHtml(site.title)}</a></p>
</body>
</html>
`;

// Precompressed copies are for servers that negotiate them; Pages compresses
// on its own and would only publish them as dead weight.
const isPrecompressed = (path) => /\.(br|gz)$/.test(path);

const files = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });

const main = (root, out) => {
  const site = JSON.parse(readFileSync(join(root, "deploy/pages/site.json"), "utf8"));
  rmSync(out, { recursive: true, force: true });
  copies.forEach((relative) => {
    const from = join(root, relative);
    statSync(from); // fails loudly when the build or `npm ci` has not run
    mkdirSync(dirname(join(out, relative)), { recursive: true });
    cpSync(from, join(out, relative), { recursive: true, filter: (path) => !isPrecompressed(path) });
  });
  const page = join(out, site.page, "index.html");
  writeFileSync(page, publishPage(readFileSync(page, "utf8"), site));
  cpSync(join(root, site.deployment), join(out, site.page, "summa.deployment.json"));
  writeFileSync(join(out, "index.html"), rootPage(site));
  writeFileSync(join(out, ".nojekyll"), "");
  const published = files(out);
  console.log(`assemble-site: ${published.length} files in ${out}`);
};

if (import.meta.url === `file://${process.argv[1]}`) {
  main(process.cwd(), process.argv[2] ?? "dist-pages");
}
