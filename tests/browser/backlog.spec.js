// The work-backlog page (web/) end to end: DOM event -> Limen kernel -> WASM
// shim -> F# engine -> effect -> Node server -> result -> engine -> view.
import fs from "node:fs";
import path from "node:path";
import { test, expect, expectConsoleError, holdRequests, queueRows, refuse, rowActions } from "./support.js";

const action = (page, id, label) =>
  page.locator(`#work-table-body tr[data-id="${id}"] .row-actions`).getByRole("button", { name: label, exact: true });

async function capture(page, { title, tags = "", priority = "medium", description = "" }) {
  await page.fill("#add-title", title);
  await page.fill("#add-tags", tags);
  await page.selectOption("#add-priority", priority);
  await page.fill("#add-description", description);
  await page.click("#add-submit");
}

test("the page runs on Limen, Forma and Folio from the pinned packages", async ({ backlog }) => {
  const { page } = backlog;
  await expect(page.locator("#repository-label")).toHaveText(/^fixture · protocol 1\.0\.0 · validation (passed|failed)$/);

  // Forma: the stylesheet is the installed package's, and its tokens and
  // component rules are what style the page.
  const sheets = await page.evaluate(() =>
    Array.from(document.styleSheets).flatMap((sheet) =>
      Array.from(sheet.cssRules).filter((rule) => rule instanceof CSSImportRule).map((rule) => rule.href)));
  expect(sheets).toEqual(expect.arrayContaining([
    "../node_modules/@echelon-foundry/design-system/dist/all.css",
    "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
  ]));
  const token = await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue("--ef-color-accent-primary").trim());
  expect(token).not.toBe("");
  // A plain <span> is inline; Forma's status lozenge rule lays it out.
  await expect(page.locator("#row-count")).not.toHaveCSS("display", "inline");

  // Folio: its print elements are registered (upgraded), not unknown tags.
  expect(await page.evaluate(() => ["ef-print-document", "ef-print-table", "ef-print-page-number"]
    .every((name) => customElements.get(name) !== undefined))).toBe(true);

  // Forma's authoring wrappers stay inert: never registered as elements.
  expect(await page.evaluate(() => customElements.get("ef-data-grid"))).toBeUndefined();

  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();
});

test("capturing work, with a renamed attachment, shows it in the queue and the detail", async ({ backlog }) => {
  const { page, origin } = backlog;
  await page.setInputFiles('#add-files input[type="file"]', { name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("hello summa") });
  await page.fill('#add-files input[type="text"]', "spec.txt");
  await capture(page, { title: "  Write the ledger  ", tags: "ledger, ,web", priority: "high", description: " first slice " });

  await expect.poll(() => queueRows(page)).toEqual([["WI-0001", "Write the ledger", "captured", "ledger, web", "high"]]);
  await expect(page.locator("#row-count")).toHaveText("1 item");
  // The form is cleared after the item is created.
  await expect(page.locator("#add-title")).toHaveValue("");
  await expect(page.locator('#add-files input[type="text"]')).toHaveValue("");

  await action(page, "WI-0001", "Show").click();
  await expect(action(page, "WI-0001", "Show")).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("#detail-title")).toHaveText("WI-0001 · Write the ledger");
  await expect(page.locator("#detail-description")).toHaveText("first slice");
  const link = page.locator(".attachment-list a");
  await expect(link).toHaveText("spec.txt");
  const href = await link.getAttribute("href");
  expect(href).toMatch(/^\/api\/work\/WI-0001\/attachments\//);
  const download = await page.request.get(origin + href);
  expect(await download.text()).toBe("hello summa");
  await expect(page.locator("#detail-body")).toContainText('"id": "WI-0001"');

  // Show again closes the detail.
  await action(page, "WI-0001", "Show").click();
  await expect(page.locator("#detail")).toHaveCount(0);
});

test("a row offers exactly the actions the server allows, and its lifecycle runs through the dialogs", async ({ backlog }) => {
  const { page, root } = backlog;
  await capture(page, { title: "Ship it" });
  await expect.poll(() => rowActions(page, "WI-0001")).toEqual(["Show", "Abandon", "Mark ready", "Edit", "Attach"]);

  await action(page, "WI-0001", "Mark ready").click();
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("ready");

  await action(page, "WI-0001", "Start").click();
  await expect(page.locator("#start-dialog")).toBeVisible();
  await page.selectOption("#start-dialog-type", "maintenance");
  await page.click("#start-dialog-confirm");
  await expect(page.locator("#start-dialog")).toBeHidden();
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("active");
  await expect(page.locator('#work-table-body tr[data-id="WI-0001"] .ef-status-lozenge')).toHaveAttribute("data-state", "attention");

  await action(page, "WI-0001", "Block").click();
  await expect(page.locator("#reason-dialog-title")).toHaveText("Reason for blocking");
  await page.fill("#reason-dialog-input", "waiting on review");
  await page.click("#reason-dialog-confirm");
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("blocked");
  await expect(page.locator('#work-table-body tr[data-id="WI-0001"] .ef-status-lozenge')).toHaveAttribute("data-state", "blocked");

  await action(page, "WI-0001", "Resume").click();
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("active");

  fs.writeFileSync(path.join(root, "impl.txt"), "x");
  fs.writeFileSync(path.join(root, "tests.txt"), "x");
  await action(page, "WI-0001", "Complete").click();
  const evidence = page.locator("#complete-evidence-rows .evidence-row");
  await expect(evidence).toHaveCount(2);
  await page.click("#complete-add-row");
  await expect(evidence).toHaveCount(3);
  await evidence.nth(0).locator("input").nth(0).fill("implementation");
  await evidence.nth(0).locator("input").nth(1).fill("impl.txt");
  await evidence.nth(1).locator("input").nth(0).fill("tests");
  await evidence.nth(1).locator("input").nth(1).fill("tests.txt");
  await page.click("#complete-dialog-confirm");
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("complete");
  await expect(page.locator('#work-table-body tr[data-id="WI-0001"] .ef-status-lozenge')).toHaveAttribute("data-state", "ok");
});

test("a server refusal is an ordinary error, not an operational fault", async ({ backlog }) => {
  const { page } = backlog;
  await capture(page, { title: "Refuse me" });
  await expect.poll(() => queueRows(page)).toHaveLength(1);
  await action(page, "WI-0001", "Mark ready").click();
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("ready");

  await action(page, "WI-0001", "Start").click();
  await page.click("#start-dialog-confirm");
  await expect.poll(async () => (await queueRows(page))[0][2]).toBe("active");

  // Completing without the configured evidence is refused by the server.
  expectConsoleError(page, /status of 400 \(Bad Request\)/);
  await action(page, "WI-0001", "Complete").click();
  await page.click("#complete-dialog-confirm");
  await expect(page.locator("#error")).toHaveText(/completion evidence missing for 'WI-0001'/);
  await expect(page.locator("#operational-fault")).toHaveCount(0);
});

test("editing and attaching go through their dialogs", async ({ backlog }) => {
  const { page } = backlog;
  await capture(page, { title: "Original", tags: "a, b", priority: "low", description: "desc" });
  await expect.poll(() => queueRows(page)).toHaveLength(1);

  await action(page, "WI-0001", "Edit").click();
  await expect(page.locator("#update-dialog-title")).toHaveValue("Original");
  await expect(page.locator("#update-dialog-tags")).toHaveValue("a, b");
  await expect(page.locator("#update-dialog-priority")).toHaveValue("low");
  await expect(page.locator("#update-dialog-description")).toHaveValue("desc");
  await page.fill("#update-dialog-title", "  Renamed ");
  await page.fill("#update-dialog-tags", "c");
  await page.click("#update-dialog-confirm");
  await expect.poll(() => queueRows(page)).toEqual([["WI-0001", "Renamed", "captured", "c", "low"]]);

  await action(page, "WI-0001", "Attach").click();
  await page.click("#attach-add-row");
  const rows = page.locator("#attach-file-rows .file-row");
  await expect(rows).toHaveCount(2);
  await rows.nth(0).locator('input[type="file"]').setInputFiles({ name: "a.txt", mimeType: "text/plain", buffer: Buffer.from("aaa") });
  await rows.nth(1).locator('input[type="file"]').setInputFiles({ name: "b.txt", mimeType: "text/plain", buffer: Buffer.from("bbbb") });
  await rows.nth(1).locator('input[type="text"]').fill("second.txt");
  await page.click("#attach-dialog-confirm");

  await action(page, "WI-0001", "Show").click();
  await expect(page.locator(".attachment-list li")).toHaveText(["a.txt (3 B)", "second.txt (4 B)"]);
});

// Limen 0.7.0 re-sent every control of a submitted form, buttons included, so
// each Add first re-fired "+ file" and appended a blank attachment row
// (limen#80, fixed in 0.7.1). The create is held in flight to see the rows
// before the reply resets the form.
test("a single submit of the capture form adds no file row, even after a refused create", async ({ backlog }) => {
  const { page } = backlog;
  const rows = page.locator("#add-files .file-row");
  const nextCreate = await holdRequests(page, "POST", /^\/api\/work$/);
  await expect(rows).toHaveCount(1);

  expectConsoleError(page, /status of 400 \(Bad Request\)/);
  await page.fill("#add-title", "Refused");
  await page.click("#add-submit");
  const refused = await nextCreate();
  await expect(rows).toHaveCount(1);
  await refuse(refused, "refused by the test");
  await expect(page.locator("#error")).toContainText("refused by the test");
  await expect(rows).toHaveCount(1);

  await page.fill("#add-title", "Accepted");
  await page.click("#add-submit");
  const accepted = await nextCreate();
  await expect(rows).toHaveCount(1);
  await accepted.continue();
  await expect.poll(() => queueRows(page)).toEqual([["WI-0001", "Accepted", "captured", "", "medium"]]);
  await expect(rows).toHaveCount(1);
});

test("filters narrow the queue and Clear resets them", async ({ backlog }) => {
  const { page } = backlog;
  await capture(page, { title: "One", tags: "wasm" });
  await expect.poll(() => queueRows(page)).toHaveLength(1);
  await capture(page, { title: "Two", tags: "docs" });
  await expect.poll(() => queueRows(page)).toHaveLength(2);

  await page.fill("#filter-tag", "wasm");
  await expect.poll(async () => (await queueRows(page)).map((row) => row[1])).toEqual(["One"]);
  await page.selectOption("#filter-status", "ready");
  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();

  await page.click("#filter-clear");
  await expect(page.locator("#filter-tag")).toHaveValue("");
  await expect(page.locator("#filter-status")).toHaveValue("");
  await expect.poll(() => queueRows(page)).toHaveLength(2);
});

test("an unreadable server response is an Aegis fault, shown through Forma's fault component", async ({ backlog }) => {
  const { page } = backlog;
  // Aegis's standard-error sink: the fault record, in the console.
  expectConsoleError(page, /"code":"SUMMA\.BOUNDARY\.RESPONSE_INVALID"/);
  await page.route("**/api/work?tag=broken", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ rows: "not a list" }) }));
  await page.fill("#filter-tag", "broken");

  const fault = page.locator("#operational-fault");
  await expect(fault).toBeVisible();
  await expect(fault).toHaveAttribute("data-ef-severity", "error");
  await expect(fault.locator(".ef-fault__message")).toHaveText(/could not read/);
  await expect(fault.locator(".ef-fault-reference code")).not.toHaveText("");
  // The fault is not an ordinary error, and it does not leak the exception.
  await expect(page.locator("#error")).toHaveCount(0);
  await expect(fault).not.toContainText("MalformedInput");

  // The next message clears it.
  await page.fill("#filter-tag", "");
  await expect(fault).toHaveCount(0);
});

test("printing shows Folio's document of the same rows and hides the controls", async ({ backlog }) => {
  const { page } = backlog;
  await capture(page, { title: "Print me", tags: "report" });
  await expect.poll(() => queueRows(page)).toHaveLength(1);

  await expect(page.locator(".print-surface")).toBeHidden();
  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".print-surface")).toBeVisible();
  await expect(page.locator("#capture")).toBeHidden();
  await expect(page.locator("ef-print-table tbody tr td")).toHaveText(["WI-0001", "Print me", "captured", "report", "medium"]);
  await expect(page.locator("ef-print-footer")).toContainText("1 item");
});

test("the page reflows at 320 CSS px without horizontal scrolling", async ({ backlog }) => {
  const { page } = backlog;
  await capture(page, { title: "A long enough title to need wrapping on a narrow screen", tags: "one, two, three" });
  await expect.poll(() => queueRows(page)).toHaveLength(1);
  await page.setViewportSize({ width: 320, height: 800 });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
