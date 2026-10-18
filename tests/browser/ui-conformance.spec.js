import { test, expect, makeSpoke, queueRows } from "./support.js";
import {
  CONFORMANCE_WIDTHS,
  expectActionOutcome,
  expectBoundActionInventory,
  expectEquivalentControlGeometry,
  expectNamedVisibleActions,
  expectNoPageOverflow,
  expectPeerAlignment
} from "./ui-conformance-helpers.js";

const semantic = (role, name) => ({ role, name });

async function exerciseWidths(page, surface) {
  for (const width of CONFORMANCE_WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await expectNoPageOverflow(page, `${surface} at ${width}px`);
  }

  // 1280 physical pixels at 200% zoom has a 640 CSS-pixel layout viewport.
  await page.setViewportSize({ width: 640, height: 900 });
  await expectNoPageOverflow(page, `${surface} at 1280px/200% zoom equivalent`);
}

async function stressLongRtlLabels(page, selectors, surface) {
  await page.evaluate((labelSelectors) => {
    document.documentElement.dir = "rtl";
    for (const selector of labelSelectors) {
      const label = document.querySelector(selector);
      if (label) label.textContent = `${label.textContent} — detailed internationalized accounting workflow label`;
    }
  }, selectors);
  await page.setViewportSize({ width: 320, height: 900 });
  await expectNoPageOverflow(page, `${surface} with long RTL labels`);
}

test("backlog declares aligned fields and remains operable at universal widths", async ({ backlog }) => {
  const { page } = backlog;
  await expectBoundActionInventory(page, "backlog");
  await page.setViewportSize({ width: 1280, height: 900 });
  await expectPeerAlignment(page, {
    id: "backlog-capture-tags-priority",
    targets: [semantic("textbox", "Tags"), semantic("combobox", "Priority")]
  });
  await expectEquivalentControlGeometry(page, {
    id: "backlog-capture-tags-priority-controls",
    targets: [semantic("textbox", "Tags"), semantic("combobox", "Priority")]
  });
  await expectPeerAlignment(page, {
    id: "backlog-filter-tag-status",
    targets: [semantic("textbox", "Tag"), semantic("combobox", "Status")]
  });
  await page.getByRole("textbox", { name: "Title", exact: true }).fill("A long backlog title that must wrap without moving actions outside the viewport");
  await page.getByRole("textbox", { name: "Tags", exact: true }).fill("internationalization, responsive-layout, accessibility");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.locator("#work-table-body tr")).toHaveCount(1);
  await expectNamedVisibleActions(page, "backlog initial state");
  await exerciseWidths(page, "backlog");
  await page.getByRole("button", { name: "Edit", exact: true }).click();
  await expect(page.locator("#update-dialog")).toBeVisible();
  await expectNoPageOverflow(page, "backlog edit dialog at 200% zoom equivalent");
  await page.locator("#update-dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await stressLongRtlLabels(page, ["label[for=add-tags]", "label[for=add-priority]", "label[for=filter-status]"], "backlog");
});

test("backlog critical actions expose observable state and keyboard outcomes", async ({ backlog }) => {
  const { page } = backlog;
  await expectActionOutcome({
    id: "backlog-add-file-row",
    control: page.getByRole("button", { name: "+ file", exact: true }),
    before: () => page.locator("#add-files .file-row").count(),
    activate: (control) => control.press("Enter"),
    after: async () => await page.locator("#add-files .file-row").count() === 2
  });

  await page.getByRole("textbox", { name: "Title", exact: true }).fill("Conformance outcome");
  await expectActionOutcome({
    id: "backlog-add-work",
    control: page.getByRole("button", { name: "Add", exact: true }),
    before: () => queueRows(page),
    activate: (control) => control.press("Enter"),
    after: async () => (await queueRows(page)).some((row) => row[1] === "Conformance outcome")
  });

  await page.getByRole("textbox", { name: "Tag", exact: true }).fill("absent");
  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();
  await expectActionOutcome({
    id: "backlog-clear-filters",
    control: page.getByRole("button", { name: "Clear", exact: true }),
    before: () => page.getByRole("textbox", { name: "Tag", exact: true }).inputValue(),
    activate: (control) => control.press("Enter"),
    after: async () => await page.getByRole("textbox", { name: "Tag", exact: true }).inputValue() === ""
  });
});

test("hub declares aligned fields and remains operable at universal widths", async ({ hub }) => {
  const { page } = hub;
  await expectBoundActionInventory(page, "hub");
  await page.setViewportSize({ width: 1280, height: 900 });
  await expectPeerAlignment(page, {
    id: "hub-registration-path-name",
    targets: [semantic("textbox", "Repository path"), semantic("textbox", "Display name")]
  });
  await expectEquivalentControlGeometry(page, {
    id: "hub-registration-path-name-controls",
    targets: [semantic("textbox", "Repository path"), semantic("textbox", "Display name")]
  });
  const spoke = makeSpoke("responsive-conformance-spoke");
  await page.getByRole("textbox", { name: "Repository path", exact: true }).fill(spoke);
  await page.getByRole("textbox", { name: "Display name", exact: true }).fill("A long internationalized repository display name for responsive verification");
  await page.getByRole("button", { name: "Register", exact: true }).click();
  await expect(page.locator("#repos-table-body tr")).toHaveCount(1);
  await page.getByRole("textbox", { name: "Title", exact: true }).fill("A long cross-repository work item title that must remain readable");
  await page.getByRole("button", { name: "Create", exact: true }).click();
  await expect(page.locator("#work-table-body tr")).toHaveCount(1);
  await expectNamedVisibleActions(page, "hub initial state");
  await exerciseWidths(page, "hub");
  await page.getByRole("button", { name: "Unregister", exact: true }).click();
  await expect(page.locator("#unregister-dialog")).toBeVisible();
  await expectNoPageOverflow(page, "hub unregister dialog at 200% zoom equivalent");
  await page.locator("#unregister-dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await stressLongRtlLabels(page, ["label[for=register-path]", "label[for=register-name]", "label[for=filter-status]"], "hub");
});

test("hub critical actions expose observable state and recovery", async ({ hub }) => {
  const { page } = hub;
  const spoke = makeSpoke("conformance-spoke");
  await page.getByRole("textbox", { name: "Repository path", exact: true }).fill(spoke);
  await page.getByRole("textbox", { name: "Display name", exact: true }).fill("Conformance spoke");
  await expectActionOutcome({
    id: "hub-register-repository",
    control: page.getByRole("button", { name: "Register", exact: true }),
    before: () => page.locator("#repos-table-body tr").count(),
    activate: (control) => control.press("Enter"),
    after: async () => await page.locator("#repos-table-body tr").count() === 1
  });

  await page.getByRole("textbox", { name: "Title", exact: true }).fill("Created through the hub");
  await expectActionOutcome({
    id: "hub-create-work",
    control: page.getByRole("button", { name: "Create", exact: true }),
    before: () => page.locator("#work-table-body tr").count(),
    activate: (control) => control.press("Enter"),
    after: async () => await page.locator("#work-table-body tr").count() === 1
  });

  await page.getByRole("textbox", { name: "Tag", exact: true }).fill("absent");
  await expect(page.getByRole("heading", { name: "No work items" })).toBeVisible();
  await expectActionOutcome({
    id: "hub-clear-filters",
    control: page.getByRole("button", { name: "Clear", exact: true }),
    before: () => page.getByRole("textbox", { name: "Tag", exact: true }).inputValue(),
    activate: (control) => control.press("Enter"),
    after: async () => await page.getByRole("textbox", { name: "Tag", exact: true }).inputValue() === ""
  });
});

test("accounting app shell and customer form remain aligned and responsive", async ({ app: { page } }) => {
  await expectBoundActionInventory(page, "accounting app");
  await page.getByRole("link", { name: "Customers", exact: true }).click();
  await page.setViewportSize({ width: 1280, height: 900 });
  await expectPeerAlignment(page, {
    id: "app-customer-identity-fields",
    targets: [semantic("textbox", "Name"), semantic("textbox", "Billing name")]
  });
  await expectEquivalentControlGeometry(page, {
    id: "app-customer-identity-controls",
    targets: [semantic("textbox", "Name"), semantic("textbox", "Billing name")]
  });
  await expectNamedVisibleActions(page, "accounting app customer route");
  await exerciseWidths(page, "accounting app");
  await stressLongRtlLabels(page, ["label[for=customer-name]", "label[for=customer-billing-name]", "label[for=customer-terms]"], "accounting app");
});
