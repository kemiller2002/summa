// Summa's own glue for the UI conformance suite (summa#90, WI-0053): the
// contract's viewports and the observations the Forma-mirrored helpers do not
// make. Nothing here guesses which elements are peers; every group is
// declared by the test that measures it.
import { expect } from "@playwright/test";
import { STATUS, edgeCoordinate, formatReport, measureAlignment, settleFontsAndFrames } from "./ui-conformance-helpers.js";

export const STANDARD = "UI-LAYOUT-INTERACTION@1.0.0";

// ULI-L09: the contract's widths, in CSS px.
export const WIDTHS = [320, 390, 768, 1280];

export const setWidth = (page, width) => page.setViewportSize({ width, height: 900 });

// ULI-L05: no horizontal page scrolling. Reports the overflowing amount and
// the widest offenders, so a failure names what escaped.
export async function expectNoHorizontalOverflow(page, label) {
  await settleFontsAndFrames(page);
  const result = await page.evaluate(() => {
    const root = document.documentElement;
    const limit = root.clientWidth;
    const offenders = Array.from(document.body.querySelectorAll("*"))
      .filter((element) => {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.right > limit + 1;
      })
      .slice(0, 5)
      .map((element) => `${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ""}${element.className && typeof element.className === "string" ? `.${element.className.trim().split(/\s+/).join(".")}` : ""}`);
    return { overflow: root.scrollWidth - limit, offenders };
  });
  expect(result.overflow, `ULI-L05 ${label}: page scrolls horizontally by ${result.overflow} CSS px (${result.offenders.join(", ")})`).toBeLessThanOrEqual(0);
}

// ULI-L01 for one declared group at the current width.
export async function expectGroup(page, group) {
  const report = await measureAlignment(page, group);
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
  return report;
}

// ULI-L09: where a declared row stacks on a narrow screen, its members keep
// their source order, one below the other, with no overlap.
export async function expectStackedInOrder(page, { id, peers, scope = page }) {
  await settleFontsAndFrames(page);
  const boxes = [];
  for (const peer of peers) {
    const locator = scope.getByRole(peer.role, { name: peer.name, exact: typeof peer.name === "string" });
    await expect(locator, `${id}: ${peer.role} "${peer.name}"`).toHaveCount(1);
    boxes.push({ peer, box: await locator.boundingBox() });
  }
  for (let index = 1; index < boxes.length; index += 1) {
    const above = boxes[index - 1];
    const below = boxes[index];
    expect(edgeCoordinate(below.box, "top"), `ULI-L09 ${id}: "${below.peer.name}" starts below the bottom of "${above.peer.name}"`)
      .toBeGreaterThanOrEqual(edgeCoordinate(above.box, "bottom") - 1);
  }
}

// ULI-L01 at the widths where the group shares a row, ULI-L09 where it stacks.
// `rows` maps a width to the declared same-row groups at that width.
export async function expectFieldRows(page, { id, peers, rows, scope }) {
  for (const width of WIDTHS) {
    await setWidth(page, width);
    const declared = rows[width];
    if (declared === undefined) {
      await expectStackedInOrder(page, { id: `${id} @${width}`, peers, scope });
      continue;
    }
    for (const [index, members] of declared.entries()) {
      await expectGroup(scope ?? page, { id: `${id} row ${index + 1} @${width}`, peers: members, edge: "top" });
    }
  }
}
