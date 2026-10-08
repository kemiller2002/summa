// node --test tools/pages/ : the page transforms of the Pages assembly (WI-0039).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { copies, excluded, publishPage, rootPage } from "./assemble-site.mjs";

const site = JSON.parse(readFileSync(new URL("../../deploy/pages/site.json", import.meta.url), "utf8"));
const page = readFileSync(new URL("../../app/index.html", import.meta.url), "utf8");
const deployment = JSON.parse(readFileSync(new URL(`../../${site.deployment}`, import.meta.url), "utf8"));

test("the policy comes first in <head>, before anything it governs", () => {
  const published = publishPage(page, site);
  const policy = published.indexOf('http-equiv="Content-Security-Policy"');
  assert.ok(policy > 0);
  assert.ok(policy < published.indexOf("<link"));
  assert.ok(policy < published.indexOf("<script"));
});

test("the policy allows WebAssembly and no inline script or style", () => {
  assert.match(site.contentSecurityPolicy, /script-src 'self' 'wasm-unsafe-eval'(;|$)/);
  assert.match(site.contentSecurityPolicy, /style-src 'self'(;|$)/);
  assert.doesNotMatch(site.contentSecurityPolicy, /'unsafe-inline'|'unsafe-eval'/);
});

test("the published page has no inline script and no inline style", () => {
  const published = publishPage(page, site);
  const scripts = [...published.matchAll(/<script\b[^>]*>/g)].map((m) => m[0]);
  assert.ok(scripts.length > 0);
  scripts.forEach((tag) => assert.match(tag, /\bsrc=/));
  assert.doesNotMatch(published, /\sstyle="/);
  assert.doesNotMatch(published, /<style\b/);
});

test("the demo banner is the page's own environment banner", () => {
  assert.match(page, /data-pages-banner data-text="environmentBanner"/);
  assert.equal(deployment.environmentName, "demo (GitHub Pages)");
});

test("a page that already carries a policy keeps its own", () => {
  const own = page.replace(/<meta charset="utf-8" \/>/, '$&\n<meta http-equiv="Content-Security-Policy" content="default-src \'none\'" />');
  const published = publishPage(own, site);
  assert.equal([...published.matchAll(/http-equiv="Content-Security-Policy"/g)].length, 1);
  assert.match(published, /default-src 'none'/);
});

test("the root page sends the browser to the application by a relative address, without script", () => {
  const root = rootPage(site);
  assert.match(root, /<meta http-equiv="refresh" content="0; url=app\/" \/>/);
  assert.doesNotMatch(root, /<script/);
});

test("only the accounting application is published, never the tools that need /api", () => {
  assert.equal(site.page, "app");
  excluded.forEach((tool) => assert.ok(!copies.some((copy) => copy === tool || copy.startsWith(`${tool}/`)), `${tool} is published`));
});

test("the Pages deployment keeps books in the browser: no sign-in, no data location", () => {
  assert.equal(deployment.environment, "local");
  assert.equal(deployment.identity, undefined);
  assert.equal(deployment.location, undefined);
});
