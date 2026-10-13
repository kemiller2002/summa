// UI-LAYOUT-INTERACTION@1.0.0 conformance for the work-backlog page (web/)
// and the project-administration hub (web-hub/), summa#90 / WI-0053. Peers
// are declared by role and name; failures are provoked by intercepting the
// real server's routes, never by stubbing the page.
import { test, expect, expectConsoleError, makeSpoke, refuse } from "./support.js";
import { expectActionOutcome, inventoryVisibleActions } from "./ui-conformance-helpers.js";
import { WIDTHS, expectFieldRows, expectNoHorizontalOverflow, setWidth } from "./ui-conformance-support.js";

const field = (role, name) => ({ role, name });

// ---------------------------------------------------------------------------
// Work backlog
// ---------------------------------------------------------------------------

const CAPTURE_ROW = [field("textbox", "Tags"), field("combobox", "Priority")];
const BACKLOG_FILTER_ROW = [field("textbox", "Tag"), field("combobox", "Status")];

test("@ui-conformance backlog: declared peer fields share a top edge, or stack in order (ULI-L01, ULI-L09)", async ({ backlog: { page } }) => {
  // Tags carries help text and Priority does not (the baseline defect: their
  // controls 27.9 CSS px apart at 768 and 1280, the select stretched).
  await expectFieldRows(page, { id: "capture", peers: CAPTURE_ROW, rows: { 768: [CAPTURE_ROW], 1280: [CAPTURE_ROW] } });
  await expectFieldRows(page, { id: "backlog filter", peers: BACKLOG_FILTER_ROW, rows: { 768: [BACKLOG_FILTER_ROW], 1280: [BACKLOG_FILTER_ROW] } });
});

test("@ui-conformance backlog: a refused capture claims nothing, keeps the input, and a retry adds exactly one item (ULI-A07, ULI-A10)", async ({ backlog: { page } }) => {
  expectConsoleError(page, /status of 400 \(Bad Request\)/);
  await page.getByRole("textbox", { name: "Title", exact: true }).fill("Retry me");
  await page.route(/\/api\/work$/, (route) => route.request().method() === "POST" ? refuse(route, "refused by the test") : route.fallback(), { times: 1 });

  await page.getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("refused by the test");
  // No false success: the queue is still empty and the input is kept.
  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();
  await expect(page.locator("#work-table-body tr")).toHaveCount(0);
  await expect(page.getByRole("textbox", { name: "Title", exact: true })).toHaveValue("Retry me");

  await expectActionOutcome(page, {
    ruleId: "ULI-A07",
    action: field("button", "Add"),
    outcome: { kind: "visible", target: field("cell", "Retry me"), expected: true }
  });
  await expect(page.locator("#work-table-body tr")).toHaveCount(1);
  await expect(page.getByRole("alert").filter({ hasText: "refused by the test" })).toHaveCount(0);
});

test("@ui-conformance backlog: every enabled action on a populated page is declared and its outcome is asserted (ULI-A01, ULI-A10)", async ({ backlog: { page } }) => {
  await page.getByRole("textbox", { name: "Title", exact: true }).fill("Inventory me");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.getByRole("cell", { name: "Inventory me" })).toBeVisible();

  // Each enabled, visible control, with where its outcome is asserted.
  const declared = {
    "+ file": "backlog.spec.js: capturing work, with a renamed attachment",
    Add: "this file: a refused capture ... retry adds exactly one item",
    Clear: "backlog.spec.js: filters narrow the queue and Clear resets them",
    Show: "backlog.spec.js: capturing work ... shows it in the queue and the detail",
    Edit: "backlog.spec.js: editing and attaching go through their dialogs",
    Attach: "backlog.spec.js: editing and attaching go through their dialogs",
    "Mark ready": "backlog.spec.js: a row offers exactly the actions the server allows",
    Block: "backlog.spec.js: a row offers exactly the actions the server allows",
    Abandon: "backlog.spec.js: a row offers exactly the actions the server allows",
    "Mark blocked": "backlog.spec.js: a row offers exactly the actions the server allows"
  };
  const found = (await inventoryVisibleActions(page)).map((action) => action.nameHint);
  const undeclared = found.filter((name) => !(name in declared));
  expect(undeclared, "ULI-A10: enabled actions with no declared outcome").toEqual([]);
  expect(found).toEqual(expect.arrayContaining(["Add", "Clear", "Show"]));

  // Show's outcome: the detail opens, and the button says it is pressed.
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: field("button", "Show"),
    outcome: { kind: "attribute", target: field("button", "Show"), attribute: "aria-pressed", expected: "true" }
  });
  await expect(page.getByRole("heading", { name: "Inventory me" })).toBeVisible();
});

test("@ui-conformance backlog: long titles and tags never scroll the page horizontally at any width (ULI-L05, ULI-L09)", async ({ backlog: { page } }) => {
  await page.getByRole("textbox", { name: "Title", exact: true }).fill("An-extraordinarily-long-work-item-title-with-no-break-opportunity-anywhere-in-it-at-all");
  await page.getByRole("textbox", { name: "Tags" }).fill("observability-and-telemetry-ingestion, accessibility-conformance-programme");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.locator("#work-table-body tr")).toHaveCount(1);
  for (const width of WIDTHS) {
    await setWidth(page, width);
    await expectNoHorizontalOverflow(page, `backlog @${width}`);
  }
});

test("@ui-conformance backlog: keyboard alone captures an item (ULI-A08)", async ({ backlog: { page } }) => {
  await page.getByRole("textbox", { name: "Title", exact: true }).focus();
  await page.keyboard.type("Keyed item");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("cell", { name: "Keyed item" })).toBeVisible();
});

// ---------------------------------------------------------------------------
// Hub
// ---------------------------------------------------------------------------

const REGISTER_ROW = [field("textbox", "Repository path"), field("textbox", "Display name")];
const CREATE_ROWS = [[field("combobox", "Repository"), field("textbox", "Title")], [field("textbox", "Tags"), field("combobox", "Priority")]];
const HUB_FILTER_ROW = [field("combobox", "Repository"), field("textbox", "Tag"), field("combobox", "Status")];

test("@ui-conformance hub: declared peer fields share a top edge, or stack in order (ULI-L01, ULI-L09)", async ({ hub: { page } }) => {
  await expectFieldRows(page, { id: "register", peers: REGISTER_ROW, rows: { 768: [REGISTER_ROW], 1280: [REGISTER_ROW] } });
  // "Repository" names a select in two sections, so each group is scoped to
  // its labelled region.
  const create = page.getByRole("region", { name: "Create work item" });
  for (const [index, row] of CREATE_ROWS.entries()) {
    await expectFieldRows(page, { id: `create ${index + 1}`, peers: row, scope: create, rows: { 768: [row], 1280: [row] } });
  }
  const filter = page.getByRole("region", { name: "Filter" });
  // At 768 the third field wraps under the first two; at 1280 all three share a row.
  await expectFieldRows(page, { id: "hub filter", peers: HUB_FILTER_ROW, scope: filter, rows: { 768: [HUB_FILTER_ROW.slice(0, 2)], 1280: [HUB_FILTER_ROW] } });
});

test("@ui-conformance hub: a refused registration lists nothing, and a retry registers the spoke once (ULI-A07, ULI-A10)", async ({ hub: { page } }) => {
  expectConsoleError(page, /status of 400 \(Bad Request\)/);
  const spoke = makeSpoke("spoke-retry");
  await page.getByRole("textbox", { name: "Repository path" }).fill(spoke);
  await page.getByRole("textbox", { name: "Display name" }).fill("Retry spoke");
  await page.route(/\/api\/repos$/, (route) => route.request().method() === "POST" ? refuse(route, "refused by the test") : route.fallback(), { times: 1 });

  await page.getByRole("button", { name: "Register" }).click();
  await expect(page.getByRole("alert")).toContainText("refused by the test");
  await expect(page.locator("#repos-table-body tr")).toHaveCount(0);
  await expect(page.getByRole("textbox", { name: "Repository path" })).toHaveValue(spoke);

  await expectActionOutcome(page, {
    ruleId: "ULI-A07",
    action: field("button", "Register"),
    outcome: { kind: "visible", target: field("cell", "Retry spoke"), expected: true }
  });
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);
  await expect(page.getByRole("alert").filter({ hasText: "refused by the test" })).toHaveCount(0);
});

test("@ui-conformance hub: no horizontal page scrolling at any width with a registered spoke (ULI-L05, ULI-L09)", async ({ hub: { page } }) => {
  const spoke = makeSpoke("spoke-with-a-rather-long-repository-name-for-the-overflow-check");
  await page.getByRole("textbox", { name: "Repository path" }).fill(spoke);
  await page.getByRole("button", { name: "Register" }).click();
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);
  for (const width of WIDTHS) {
    await setWidth(page, width);
    await expectNoHorizontalOverflow(page, `hub @${width}`);
  }
});
