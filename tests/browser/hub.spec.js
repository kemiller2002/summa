// The project-administration hub page (web-hub/) end to end, against spoke
// repositories with their own `./ros`.
import { test, expect, expectConsoleError, makeSpoke } from "./support.js";

const workRows = (page) =>
  page.locator("#work-table-body tr").evaluateAll((rows) =>
    rows.map((row) => Array.from(row.querySelectorAll("td")).map((cell) => cell.textContent.trim())));

async function register(page, path, name = "") {
  await page.fill("#register-path", path);
  await page.fill("#register-name", name);
  await page.click("#register-submit");
}

test("the hub runs on Limen, Forma and Folio from the pinned packages", async ({ hub }) => {
  const { page } = hub;
  const sheets = await page.evaluate(() =>
    Array.from(document.styleSheets).flatMap((sheet) =>
      Array.from(sheet.cssRules).filter((rule) => rule instanceof CSSImportRule).map((rule) => rule.href)));
  expect(sheets).toEqual(expect.arrayContaining([
    "../node_modules/@echelon-foundry/design-system/dist/all.css",
    "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
  ]));
  expect(await page.evaluate(() => customElements.get("ef-print-document") !== undefined)).toBe(true);
  await expect(page.getByText("No repositories are registered yet.")).toBeVisible();
  await expect(page.locator("#row-count")).toHaveText("0 items");
});

test("registering a spoke lists it, offers it in both selects and aggregates its work", async ({ hub }) => {
  const { page } = hub;
  const spoke = makeSpoke("spoke-a");
  await register(page, spoke, "Spoke A");

  await expect(page.locator("#repos-table-body tr td")).toHaveText(["spoke-a", "Spoke A", spoke, "Unregister"]);
  await expect(page.locator("#register-path")).toHaveValue("");
  await expect(page.locator("#create-repo option")).toHaveText(["Spoke A"]);
  await expect(page.locator("#create-repo")).toHaveValue("spoke-a");
  await expect(page.locator("#filter-repo option")).toHaveText(["all", "Spoke A"]);

  await page.fill("#create-title-input", "  From the hub ");
  await page.fill("#create-tags", "hub, web");
  await page.selectOption("#create-priority", "high");
  await page.click("#create-submit");
  await expect.poll(() => workRows(page)).toEqual([["Spoke A", "WI-0001", "From the hub", "captured", "hub, web", "high"]]);
  await expect(page.locator("#create-title-input")).toHaveValue("");
  // The repository choice survives the reset, so several items can follow.
  await expect(page.locator("#create-repo")).toHaveValue("spoke-a");
});

test("creating with files sends them to the spoke as a multipart upload", async ({ hub }) => {
  const { page } = hub;
  const spoke = makeSpoke("spoke-files");
  await register(page, spoke);
  await expect(page.locator("#create-repo option")).toHaveText(["spoke-files"]);

  await page.fill("#create-title-input", "With evidence");
  await page.click("#create-add-file");
  const rows = page.locator("#create-files .file-row");
  await expect(rows).toHaveCount(2);
  await rows.nth(0).locator('input[type="file"]').setInputFiles({ name: "a.txt", mimeType: "text/plain", buffer: Buffer.from("a") });
  await rows.nth(0).locator('input[type="text"]').fill("renamed.txt");
  await page.click("#create-submit");

  await expect.poll(() => workRows(page)).toEqual([["spoke-files", "WI-0001", "With evidence", "captured", "", "medium"]]);
  await expect(rows).toHaveCount(1);
  const { readdirSync, readFileSync } = await import("node:fs");
  const queue = JSON.parse(readFileSync(`${spoke}/.ros/work/queue.json`, "utf8"));
  expect(queue.items[0].attachments.map((attachment) => attachment.name)).toEqual(["renamed.txt"]);
  expect(readdirSync(`${spoke}/.ros/work/attachments`).length).toBeGreaterThan(0);
});

test("unregistering asks first, and Cancel keeps the repository", async ({ hub }) => {
  const { page } = hub;
  const spoke = makeSpoke("spoke-gone");
  await register(page, spoke, "Gone soon");
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);

  await page.locator("#repos-table-body").getByRole("button", { name: "Unregister", exact: true }).click();
  await expect(page.locator("#unregister-dialog")).toBeVisible();
  await expect(page.locator("#unregister-dialog-text")).toHaveText(
    `Unregister Gone soon (${spoke})? This only removes it from the hub -- the repository itself is unaffected.`);
  await page.locator("#unregister-dialog").getByRole("button", { name: "Cancel" }).click();
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);

  await page.locator("#repos-table-body").getByRole("button", { name: "Unregister", exact: true }).click();
  await page.click("#unregister-dialog-confirm");
  await expect(page.locator("#repos-table-body tr")).toHaveCount(0);
  await expect(page.locator("#create-repo option")).toHaveCount(0);
});

test("a refused registration and a broken spoke are ordinary errors", async ({ hub }) => {
  const { page } = hub;
  expectConsoleError(page, /status of 400 \(Bad Request\)/);
  await register(page, "/no/such/path");
  await expect(page.locator("#repos-error")).toHaveText(/not a directory/);

  const broken = makeSpoke("spoke-broken", { broken: true });
  await register(page, broken);
  await expect(page.locator("#repos-error")).toHaveCount(0);
  await expect.poll(() => workRows(page)).toEqual([["spoke-broken", "spoke-broken: this spoke is broken"]]);
  await expect(page.locator("#work-table-body tr")).toHaveAttribute("data-error", "true");
  await expect(page.locator("#operational-fault")).toHaveCount(0);
});

test("filters narrow the aggregated queue by repository, tag and status", async ({ hub }) => {
  const { page } = hub;
  await register(page, makeSpoke("one"));
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);
  await register(page, makeSpoke("two"));
  await expect(page.locator("#repos-table-body tr")).toHaveCount(2);

  for (const [repo, title, tags] of [["one", "First", "x"], ["two", "Second", "y"]]) {
    await page.selectOption("#create-repo", repo);
    await page.fill("#create-title-input", title);
    await page.fill("#create-tags", tags);
    await page.click("#create-submit");
    await expect(page.locator("#create-title-input")).toHaveValue("");
  }
  await expect.poll(async () => (await workRows(page)).map((row) => row[2])).toEqual(["First", "Second"]);

  await page.selectOption("#filter-repo", "two");
  await expect.poll(async () => (await workRows(page)).map((row) => row[2])).toEqual(["Second"]);
  await page.selectOption("#filter-repo", "");
  await page.fill("#filter-tag", "x");
  await expect.poll(async () => (await workRows(page)).map((row) => row[2])).toEqual(["First"]);
  await page.selectOption("#filter-status", "ready");
  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();
  await page.click("#filter-clear");
  await expect(page.locator("#filter-repo")).toHaveValue("");
  await expect.poll(async () => (await workRows(page)).length).toBe(2);
});

test("printing shows Folio's document of the aggregated queue", async ({ hub }) => {
  const { page } = hub;
  await register(page, makeSpoke("printed"));
  await page.fill("#create-title-input", "On paper");
  await page.click("#create-submit");
  await expect.poll(async () => (await workRows(page)).length).toBe(1);

  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".print-surface")).toBeVisible();
  await expect(page.locator("#repos")).toBeHidden();
  await expect(page.locator("ef-print-table tbody tr td")).toHaveText(["printed", "WI-0001", "On paper", "captured", "", "medium"]);
});
