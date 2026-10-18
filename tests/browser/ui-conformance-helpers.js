import { expect } from "@playwright/test";

export const CONFORMANCE_WIDTHS = [320, 390, 768, 1280];

async function settled(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  });
}

const coordinate = (box, relation) => {
  if (relation === "top-edge") return box.y;
  if (relation === "bottom-edge") return box.y + box.height;
  if (relation === "centerline") return box.y + box.height / 2;
  throw new Error(`Unsupported measured relation: ${relation}`);
};

export async function expectPeerAlignment(page, { id, relation = "top-edge", targets, toleranceCssPx = 1 }) {
  await settled(page);
  const measurements = [];
  for (const target of targets) {
    const locator = page.getByRole(target.role, { name: target.name, exact: true });
    await expect(locator, `${id}: ${target.role} "${target.name}" is present once`).toHaveCount(1);
    await expect(locator, `${id}: ${target.role} "${target.name}" is visible`).toBeVisible();
    const box = await locator.boundingBox();
    expect(box, `${id}: ${target.role} "${target.name}" has measurable geometry`).not.toBeNull();
    measurements.push({ target, box, value: coordinate(box, relation) });
  }

  const values = measurements.map(({ value }) => value);
  const spread = Math.max(...values) - Math.min(...values);
  expect(spread, `${id}: ${relation} spread in CSS px\n${JSON.stringify(measurements, null, 2)}`).toBeLessThanOrEqual(toleranceCssPx);
}

export async function expectEquivalentControlGeometry(page, { id, targets, toleranceCssPx = 1 }) {
  await settled(page);
  const heights = [];
  for (const target of targets) {
    const locator = page.getByRole(target.role, { name: target.name, exact: true });
    await expect(locator, `${id}: ${target.role} "${target.name}" is present once`).toHaveCount(1);
    const box = await locator.boundingBox();
    expect(box, `${id}: ${target.role} "${target.name}" has measurable geometry`).not.toBeNull();
    heights.push({ target, height: box.height });
  }
  const values = heights.map(({ height }) => height);
  const spread = Math.max(...values) - Math.min(...values);
  expect(spread, `${id}: control-height spread in CSS px\n${JSON.stringify(heights, null, 2)}`).toBeLessThanOrEqual(toleranceCssPx);
}

export async function expectNoPageOverflow(page, label) {
  await settled(page);
  const geometry = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth
  }));
  expect(geometry.scrollWidth - geometry.clientWidth, `${label}: horizontal page overflow in CSS px`).toBeLessThanOrEqual(0);
}

export async function expectNamedVisibleActions(page, label) {
  const actions = await page.locator("button:visible, a[href]:visible, input[type=button]:visible, input[type=submit]:visible, input[type=reset]:visible, [role=button]:visible")
    .evaluateAll((elements) => elements.map((element) => ({
      tag: element.tagName.toLowerCase(),
      id: element.id,
      name: element.getAttribute("aria-label")?.trim()
        || element.getAttribute("title")?.trim()
        || element.textContent?.trim()
        || element.value?.trim()
        || ""
    })));
  expect(actions.length, `${label}: action inventory is not empty`).toBeGreaterThan(0);
  expect(actions.filter(({ name }) => name === ""), `${label}: every visible action has an accessible-name source`).toEqual([]);
  return actions;
}

export async function expectBoundActionInventory(page, label) {
  const inventory = await page.evaluate(() => {
    const actions = [];
    const visit = (root) => {
      for (const element of root.querySelectorAll("button, a, input[type=button], input[type=submit], input[type=reset]")) {
        const tag = element.tagName.toLowerCase();
        const kind = tag === "a" ? "link" : "button";
        const name = element.getAttribute("aria-label")?.trim()
          || element.textContent?.replace(/\s+/g, " ").trim()
          || element.value?.trim()
          || element.id
          || "unnamed";
        const form = element.closest("form");
        const binding = tag === "a"
          ? element.hasAttribute("href") || element.hasAttribute("data-bind-href")
          : element.hasAttribute("data-event")
            || element.hasAttribute("commandfor")
            || (element.type === "submit" && (form?.hasAttribute("data-event") || form?.method === "dialog"))
            || element.type === "reset"
            || form?.method === "dialog";
        actions.push({ kind, name, binding });
      }
      for (const template of root.querySelectorAll("template")) visit(template.content);
    };
    visit(document);
    return actions;
  });

  expect(inventory.length, `${label}: source action inventory is not empty`).toBeGreaterThan(0);
  expect(inventory.filter(({ binding }) => !binding), `${label}: every source action has a navigation, command, form, or engine binding`).toEqual([]);
  return inventory;
}

export async function expectActionOutcome({ id, control, before, activate, after }) {
  const initial = await before();
  expect(await after(), `${id}: expected outcome must not already hold`).toBe(false);
  await activate(control);
  await expect.poll(after, { message: `${id}: action did not produce its declared outcome` }).toBe(true);
  expect(await before(), `${id}: pre-state must change after activation`).not.toEqual(initial);
}
