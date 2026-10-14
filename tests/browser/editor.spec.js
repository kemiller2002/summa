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
  const typed = await typeLine(page, "Architecture assessment", "10", "150.00");
  // The line's fields are on screen at a usable size, not only present (their labels are for screen readers).
  for (const field of [".line-description", ".line-hours", ".line-rate"]) {
    const onScreen = await typed.locator(field).evaluate((input) => {
      const box = input.getBoundingClientRect();
      return box.width > 40 && document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2) === input;
    });
    expect(onScreen, `${field} is not visible where it is`).toBe(true);
  }
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
  await page.click("#confirm-issue");
  await expect(page.locator("#invoice-total")).toHaveText("1,088.75 USD");
  await expect(page.locator("#invoice-document .summa-adjustment dt")).toHaveText("Tax NY-8.875");
});

test("line details and client references are entered in the editor and shown on the invoice", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  const row = await typeLine(page, "Assessment", "10", "150.00");
  await row.locator(".line-details").click();
  await expect(page.locator("#line-details")).toBeVisible();
  await expect(page.locator("#line-details-title")).toHaveText("Line 1: Assessment");
  await page.fill("#line-detailed-description", "Security assessment of the billing service");
  await page.fill("#line-unit", "hours");
  await page.fill("#line-service-from", "2026-09-01");
  await page.fill("#line-service-to", "2026-09-30");
  await page.fill("#line-customer-code", "ABC-4410");
  await expect(row.locator(".line-details")).toContainText("Details (given)");
  await page.click("#line-details-done");
  await expect(page.locator("#line-details")).toBeHidden();

  await page.click("#add-reference");
  const reference = page.locator("#draft-reference-rows tbody tr").first();
  await reference.locator(".reference-kind").selectOption("cost-center");
  await reference.locator(".reference-value").fill("CC-7");

  await page.click("#review-draft");
  await expect(page.locator("#notice")).toHaveText("Ready to issue. Check the preview, then issue it.");
  // Saved and shown again as stored.
  await expect(page.locator("#draft-reference-rows .reference-kind")).toHaveValue("cost-center");
  await row.locator(".line-details").click();
  await expect(page.locator("#line-unit")).toHaveValue("hours");
  await page.keyboard.press("Escape");
  await expect(page.locator("#line-details")).toBeHidden();

  await page.click("#issue-invoice");

  await page.click("#confirm-issue");
  const document = page.locator("#invoice-document");
  await expect(document).toContainText("Security assessment of the billing service");
  await expect(document).toContainText("Unit: hours");
  await expect(document).toContainText("Your code: ABC-4410");
  await expect(document.locator("dt", { hasText: "Cost center" })).toHaveCount(1);
  await expect(document).toContainText("CC-7");
});

test("a customer that requires a purchase order: the editor says so and issuing waits for it", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await page.goto(page.url().replace(/#.*$/, "#/customers/CUST-0001"));
  await page.check("#customer-po-required");
  await expect(page.locator("#notice")).toHaveText("Invoices for this customer now need a purchase order.");
  await typeLine(page, "Assessment", "10", "150.00");
  await expect(page.locator("#draft-po-note")).toHaveText("Required: this customer's accounts payable needs a purchase order on every invoice.");
  await page.click("#review-draft");
  await expect(page.locator("#blockers")).toContainText("requires a purchase order on every invoice");
  await page.fill("#draft-po", "PO-55");
  await page.click("#review-draft");
  await expect(page.locator("#notice")).toHaveText("Ready to issue. Check the preview, then issue it.");
});

test("issuing asks first and says what will happen; not yet issues nothing", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await typeLine(page, "Assessment", "10", "150.00");
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await expect(page.locator("#issue-dialog")).toBeVisible();
  await expect(page.locator("#issue-dialog-text")).toHaveText("Issue this invoice to ABC Corp for 1,500.00 USD? It gets the next invoice number, which never changes, and is recorded in the books. To correct it afterwards, issue a credit memo.");
  await page.click("#issue-dialog-cancel");
  await expect(page.locator("#issue-dialog")).toBeHidden();
  await expect(page).toHaveURL(/#\/drafts\//);
  await page.click("#issue-invoice");
  await page.click("#confirm-issue");
  await expect(page).toHaveURL(/#\/invoices\/INV-\d{4}$/);
});

test("after issuing, the accounting is one link away and says why it was posted (SUM4-021, SUM4-034)", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await typeLine(page, "Assessment", "10", "150.00");
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await page.click("#confirm-issue");
  await expect(page.locator("#notice")).toHaveText(/^Invoice \S+ issued\.$/);
  const number = (await page.locator("#notice").textContent()).split(" ")[1];
  await expect(page.locator("#notice-entry")).toHaveText("View accounting entry");
  await page.click("#notice-entry");
  await expect(page).toHaveURL(/#\/ledger\/entries\/[^/]+$/);
  await expect(page.locator("#entry-explained-title")).toHaveText("Why was this posted?");
  await expect(page.locator("#entry-explanation li").first()).toHaveText(`Invoice ${number} was issued to ABC Corp for 1,500.00 USD.`);
  await expect(page.locator("#entry-explanation li").nth(1)).toHaveText(/^Debit .*because/);
});

test("proposals have their own place, empty until something proposes an invoice", async ({ app: { page } }) => {
  await nav(page, "Proposals").click();
  await expect(page).toHaveURL(/#\/proposals$/);
  await expect(page.locator("#no-proposals")).toBeVisible();
  await page.goto(page.url().replace("#/proposals", "#/proposals/P-404"));
  await expect(page.locator("#not-found-title")).toHaveText("Nothing is at this link");
});

test("choosing a customer shows what it proposes, and an engagement can be chosen", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  for (const name of ["Retainer", "Audit"]) {
    await nav(page, "Engagements").click();
    await page.selectOption("#engagement-customer", "CUST-0001");
    await page.fill("#engagement-name", name);
    await page.click("#add-engagement");
    await expect(page).toHaveURL(/#\/engagements\/ENG-000\d$/);
  }

  await page.click("#new-invoice");
  await expect(page.locator("#draft-engagement")).toHaveCount(0);
  await page.selectOption("#draft-customer", "CUST-0001");
  await expect(page.locator("#draft-bill-to")).toHaveText("ABC Corp · 1 Main Street, Springfield · ap@example.com");
  // No rate is set anywhere, so none is invented.
  await expect(page.locator("#draft-rate-note")).toHaveText("No rate is set for this customer: enter each line's rate.");
  await expect(page.locator("#draft-lines tbody tr").first().locator(".line-rate")).toHaveValue("");

  // Two engagements: none is chosen until the person chooses one.
  await expect(page.locator("#draft-engagement")).toHaveValue("");
  await page.selectOption("#draft-engagement", "ENG-0002");
  await expect(page.locator("#draft-engagement")).toHaveValue("ENG-0002");
  await page.reload();
  await expect(page.locator("#draft-engagement")).toHaveValue("ENG-0002");
});
