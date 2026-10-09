// The rest of the v0.4 interface (WI-0044) in a real browser: the editor's
// unsaved form survives a refresh, tax is entered on the screens and never
// worked out, and proposals have their own place.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });

async function addCustomer(page, name) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", name);
  await page.fill("#customer-email", "ap@example.com");
  await page.fill("#customer-address", "1 Main Street\nSpringfield");
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText(`Customer ${name} added.`);
}

async function typeLine(page, description, hours, rate) {
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill(description);
  await row.locator(".line-hours").fill(hours);
  await row.locator(".line-rate").fill(rate);
  return row;
}

test("an unsaved invoice survives a refresh, and can be discarded", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await typeLine(page, "Architecture assessment", "10", "150.00");
  await expect(page.locator("#draft-total")).toHaveText("1,500.00 USD");

  await page.reload();
  await expect(page.locator("#notice")).toHaveText("Your unsaved changes were restored.");
  const row = page.locator("#draft-lines tbody tr").first();
  await expect(row.locator(".line-description")).toHaveValue("Architecture assessment");
  await expect(page.locator("#draft-customer")).toHaveValue("CUST-0001");
  await expect(page.locator("#draft-total")).toHaveText("1,500.00 USD");

  await page.click("#discard-changes");
  await expect(page.locator("#notice")).toHaveText("Your unsaved changes were discarded.");
  await expect(row.locator(".line-description")).toHaveValue("");
  await page.reload();
  await expect(page.locator("#draft-lines tbody tr").first().locator(".line-description")).toHaveValue("");
});

test("a tax the person enters is added to the invoice and shown on the document", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  const row = await typeLine(page, "Assessment", "10", "100.00");
  await row.locator(".line-taxable").check();
  await page.fill("#draft-tax-code", "NY-8.875");
  await page.fill("#draft-tax-amount", "88.75");
  await page.fill("#draft-tax-rate", "8.875");
  await expect(page.locator("#draft-tax-account")).toHaveValue("2300");
  await expect(page.locator("#draft-total")).toHaveText("1,088.75 USD");
  await page.click("#review-draft");
  await expect(page.locator("#notice")).toHaveText("Ready to issue. Check the preview, then issue it.");
  await page.click("#issue-invoice");
  await expect(page.locator("#invoice-total")).toHaveText("1,088.75 USD");
  await expect(page.locator("#invoice-document .summa-adjustment dt")).toHaveText("Tax NY-8.875");
});

test("proposals have their own place, empty until something proposes an invoice", async ({ app: { page } }) => {
  await nav(page, "Proposals").click();
  await expect(page).toHaveURL(/#\/proposals$/);
  await expect(page.locator("#no-proposals")).toBeVisible();
  await page.goto(page.url().replace("#/proposals", "#/proposals/P-404"));
  await expect(page.locator("#not-found-title")).toHaveText("Nothing is at this link");
});
