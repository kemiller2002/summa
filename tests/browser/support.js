// Shared harness for the browser suite: a throwaway ROS repository per test,
// the real Node server for the page under test (in-process, on a free port),
// and a guard that fails the test on any console error or page error, since
// Limen reports a broken engine to the console rather than throwing.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test as base, expect } from "@playwright/test";

import { createServer as createBacklogServer } from "../../tools/ros_server.mjs";
import { createServer as createHubServer } from "../../tools/ros_hub_server.mjs";
import { createServer as createAppServer } from "../../tools/app_server.mjs";

const REPOSITORY = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

// A minimal ROS repository: a git checkout with a ros.json naming it and the
// telemetry metric registry the work protocol requires to start work.
export function makeRepository(name) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `summa-${name}-`));
  execFileSync("git", ["init", "-q"], { cwd: root });
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify({ name }, null, 2));
  fs.mkdirSync(path.join(root, "telemetry"));
  fs.copyFileSync(path.join(REPOSITORY, "telemetry", "metrics.json"), path.join(root, "telemetry", "metrics.json"));
  return root;
}

// A spoke repository for the hub: its own `./ros` runs this checkout's Node
// ROS CLI, translating the one call shape that differs (the hub's
// `work attach --id ID --occurred-at T`, which an F#-backed spoke accepts).
export function makeSpoke(name, { broken = false } = {}) {
  const root = makeRepository(name);
  const cli = path.join(REPOSITORY, "tools", "ros_cli.mjs");
  const script = broken
    ? "#!/usr/bin/env node\nprocess.stderr.write('ERROR this spoke is broken');\nprocess.exit(1);\n"
    : `#!/usr/bin/env node
import { main } from ${JSON.stringify(cli)};
const args = process.argv.slice(2);
const attach = args[0] === "work" && args[1] === "attach" && args[2] === "--id";
const argv = attach
  ? ["work", "attach", args[3], ...args.slice(4).filter((arg, i, all) => arg !== "--occurred-at" && all[i - 1] !== "--occurred-at")]
  : args;
process.exitCode = main(argv);
`;
  fs.writeFileSync(path.join(root, "ros"), script, { mode: 0o755 });
  return root;
}

async function listen(server) {
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return `http://127.0.0.1:${server.address().port}`;
}

// Console errors a test expects, by page: the browser's own log line for a
// 4xx the test provoked, or the Aegis fault record a test provoked.
const expected = new WeakMap();
export function expectConsoleError(page, pattern) {
  expected.set(page, [...(expected.get(page) ?? []), pattern]);
}

export const test = base.extend({
  // Fails the test on any unexpected console error or uncaught page error.
  page: async ({ page }, use) => {
    const problems = [];
    page.on("console", (message) => {
      if (message.type() !== "error") return;
      const text = message.text();
      if (!(expected.get(page) ?? []).some((pattern) => pattern.test(text))) problems.push(`console.error: ${text}`);
    });
    page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));
    await use(page);
    expect(problems, "the page reported errors").toEqual([]);
  },

  backlog: async ({ page }, use) => {
    const root = makeRepository("fixture");
    const server = createBacklogServer(root);
    const origin = await listen(server);
    await page.goto(origin);
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use({ page, root, origin });
    server.close();
  },

  // The accounting application: a fresh browser profile, so its books start empty.
  app: async ({ page }, use) => {
    const server = createAppServer();
    const origin = await listen(server);
    await page.goto(origin);
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use({ page, origin });
    server.close();
  },

  hub: async ({ page }, use) => {
    const root = makeRepository("hub");
    const server = createHubServer(root);
    const origin = await listen(server);
    await page.goto(origin);
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use({ page, root, origin });
    server.close();
  }
});

export { expect };

// Holds each `method` request whose path matches `path` until the test answers
// it, so a test can look at the page while that request is in flight. Returns
// a function that resolves, in order, to the next held Playwright Route.
export async function holdRequests(page, method, path) {
  const held = [];
  const waiting = [];
  await page.route((url) => path.test(url.pathname), (route) => {
    if (route.request().method() !== method) return route.fallback();
    const waiter = waiting.shift();
    return waiter ? waiter(route) : held.push(route);
  });
  return () => held.length > 0 ? Promise.resolve(held.shift()) : new Promise((resolve) => waiting.push(resolve));
}

// Answers a held request as the server answers a refusal.
export const refuse = (route, error) =>
  route.fulfill({ status: 400, contentType: "application/json", body: JSON.stringify({ error }) });

// The rendered queue as plain rows: [id, title, status, tags, priority].
export async function queueRows(page) {
  return page.locator("#work-table-body tr").evaluateAll((rows) =>
    rows.map((row) => Array.from(row.querySelectorAll("td")).slice(0, 5).map((cell) => cell.textContent.trim())));
}

// The visible action buttons of one row, in order.
export async function rowActions(page, id) {
  return page.locator(`#work-table-body tr[data-id="${id}"] .row-actions button:visible`).allTextContents();
}
