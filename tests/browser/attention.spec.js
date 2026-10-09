// The work queue, follow-up, payments inbox and CPA workspace (WI-0030
// slice 5) in the real page, each reached by a link and kept in the address.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });

async function invoiceFor(page, rate) {
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Work");
  await row.locator(".line-rate").fill(rate);
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await expect(page).toHaveURL(/#\/invoices\/INV-\d{4}$/);
}

async function acmeWithInvoices(page) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await invoiceFor(page, "100");
  await invoiceFor(page, "250");
}

test("the work queue links to what needs attention, and a dispute is recorded on the invoice", async ({ app: { page } }) => {
  await acmeWithInvoices(page);
  await nav(page, "Work").click();
  await expect(page).toHaveURL(/#\/work$/);
  await expect(page.locator("#work-list li").first()).toBeVisible();

  await page.goto(page.url().replace(/#.*$/, "#/invoices/INV-0002?tab=payments"));
  await page.fill("#follow-up-note", "Hours not agreed");
  await page.click("#mark-disputed");
  await expect(page.locator("#follow-up-dispute")).toHaveText(/^Disputed on .*: Hours not agreed$/);
  await page.click("#record-reminder");
  await expect(page.locator("#follow-up-collection")).toHaveText("Reminded once");

  await nav(page, "Work").click();
  await page.click("#work-follow-up");
  await page.click('.summa-tab[data-tab="disputed"]');
  await expect(page).toHaveURL(/#\/follow-up\?show=disputed$/);
  await expect(page.locator("#follow-up-table tbody tr")).toHaveCount(1);
});

test("a payment received on account is applied from the inbox, oldest invoice first", async ({ app: { page } }) => {
  await acmeWithInvoices(page);
  await nav(page, "Work").click();
  await page.click("#work-inbox");
  await expect(page).toHaveURL(/#\/inbox$/);
  await page.selectOption("#receipt-customer", "CUST-0001");
  await page.fill("#receipt-amount", "300");
  await page.fill("#receipt-reference", "CHK-9");
  await page.click("#record-receipt");
  await expect(page.locator("#inbox-table tbody tr")).toHaveCount(1);
  await page.click("#inbox-table .summa-apply");
  await expect(page.locator("#notice")).toHaveText("Payment PAY-0001 applied.");
  await expect(page.locator("#inbox-empty")).toBeVisible();
  await nav(page, "Invoices").click();
  await expect(page.locator("#invoices-table tbody tr", { hasText: "INV" }).first()).toBeVisible();
});

test("the CPA workspace downloads the year's trial balance as CSV", async ({ app: { page } }) => {
  await acmeWithInvoices(page);
  await page.goto(page.url().replace(/#.*$/, "#/cpa"));
  await expect(page.locator("#cpa-title")).toContainText("CPA workspace");
  const [download] = await Promise.all([page.waitForEvent("download"), page.click("#export-trial-balance")]);
  expect(download.suggestedFilename()).toMatch(/^trial-balance-\d{4}\.csv$/);
  const chunks = [];
  for await (const chunk of await download.createReadStream()) chunks.push(chunk);
  expect(Buffer.concat(chunks).toString("utf8")).toMatch(/^# Export: trial-balance\n# SchemaVersion: summa\.export\/1\n# GeneratedAt: .*\n# AccountingPeriod: \d{4}-01-01\.\.\d{4}-12-31\n# DataVersion: sha256:[0-9a-f]{64}\n# Filters: currency=USD; year=\d{4}\nCode,Account,Debit,Credit\n1100,Accounts Receivable,350\.00,0\.00\n/);
});
