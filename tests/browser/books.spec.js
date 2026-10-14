// The ledger, journal entries, reports and periods (WI-0030 slice 3) in the
// real page: reached by links, with the account, dates and basis in the
// address, so a reload or a shared link opens the same figures.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });
const running = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

async function invoicePartlyPaid(page) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Assessment");
  await row.locator(".line-rate").fill("1000");
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await page.click("#confirm-issue");
  await page.click('.summa-tab[data-tab="payments"]');
  await page.fill("#payment-amount", "400");
  await page.click("#record-payment");
  await expect(page.locator("#invoice-outstanding")).toHaveText("600.00 USD");
}

test("the ledger and its entries are reached by links and keep their account in the address", async ({ app: { page } }) => {
  await invoicePartlyPaid(page);
  await nav(page, "Ledger").click();
  await expect(page).toHaveURL(/#\/ledger$/);
  await expect(page.locator("#journal-table tbody tr")).toHaveCount(2);
  await page.selectOption("#ledger-account", "1100");
  await expect(page).toHaveURL(/#\/ledger\?account=1100$/);
  await page.reload();
  await running(page);
  await expect(page.locator("#ledger-account")).toHaveValue("1100");
  await expect(page.locator("#ledger-table tbody tr td:last-child")).toHaveText(["1,000.00", "600.00"]);
  await page.locator("#ledger-table a").last().click();
  await expect(page).toHaveURL(/#\/ledger\/entries\/JE-PAY-0001$/);
  await expect(page.locator("#entry-debits")).toHaveText("400.00");
  await expect(page.locator("#entry-credits")).toHaveText("400.00");
  // Every entry leads back to what posted it (v0.4 §22).
  await page.click("#entry-origin");
  await expect(page).toHaveURL(/#\/payments\/PAY-0001$/);
});

test("reports keep their dates and basis in the address", async ({ app: { page, origin } }) => {
  await invoicePartlyPaid(page);
  await nav(page, "Reports").click();
  await page.click("#income-statement-link");
  await expect(page).toHaveURL(/#\/reports\/income-statement$/);
  await page.selectOption("#income-basis", "cash");
  await expect(page).toHaveURL(/#\/reports\/income-statement\?basis=cash$/);
  await expect(page.locator("#income-revenue")).toHaveText("400.00 USD");
  await page.click('.summa-tab[data-range="year-to-date"]');
  await expect(page).toHaveURL(/#\/reports\/income-statement\?from=\d{4}-01-01&to=\d{4}-\d{2}-\d{2}&basis=cash$/);
  await page.locator("#income-accounts a").first().click();
  await expect(page).toHaveURL(/#\/ledger\?account=4000&from=\d{4}-01-01&to=/);

  // A link to the trial balance on a date opens with that date.
  await page.goto(`${origin}/#/reports/trial-balance?asOf=2000-01-01`);
  await running(page);
  await expect(page.locator("#trial-as-of")).toHaveValue("2000-01-01");
  await expect(page.locator("#trial-balance-table tbody tr")).toHaveCount(0);
  await page.goto(`${origin}/#/reports/balance-sheet`);
  await running(page);
  await expect(page.locator("#sheet-balances")).toBeVisible();
});

test("saved report views are links; each keeps its view and period in the address (SUM4-029, SUM4-030)", async ({ app: { page, origin } }) => {
  await invoicePartlyPaid(page);
  await nav(page, "Reports").click();
  await page.locator("#saved-views a", { hasText: "Revenue by client" }).click();
  await expect(page).toHaveURL(/#\/reports\/management$/);
  await expect(page.locator("#management-title")).toHaveText("Revenue by client");
  await expect(page.locator("#management-table tbody tr")).toHaveCount(1);
  await expect(page.locator("#management-table tbody tr td").first()).toHaveText("Acme");
  await expect(page.locator("#management-total")).toHaveText("1,000.00");

  await page.locator(".summa-tab", { hasText: "Outstanding by customer" }).click();
  await expect(page).toHaveURL(/#\/reports\/management\?view=outstanding-by-customer$/);
  await expect(page.locator("#management-total")).toHaveText("600.00");
  await expect(page.locator("#management-from")).toHaveCount(0);

  // Profitability: costs Summa has no source for are not shown as zero.
  await page.goto(`${origin}/#/reports/management?view=engagement-contribution`);
  await running(page);
  await expect(page.locator("#contribution-table tbody tr td").nth(2)).toHaveText("Not available");
  await expect(page.locator("#management-note")).toContainText("no source in Summa yet");
});

test("a period is closed from its own page and says so", async ({ app: { page } }) => {
  await invoicePartlyPaid(page);
  await nav(page, "Periods").click();
  await expect(page).toHaveURL(/#\/periods$/);
  const month = await page.evaluate(() => { const d = new Date(); return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}`; });
  await page.locator(`#periods-table a[href="#/periods/${month}"]`).click();
  await expect(page).toHaveURL(new RegExp(`#/periods/${month}$`));
  await page.click("#close-period");
  await page.click("#confirm-close");
  await expect(page.locator("#period-state")).toHaveText("Closed");
  await page.reload();
  await running(page);
  await expect(page.locator("#period-state")).toHaveText("Closed");
  await page.click("#reopen-period");
  await page.click("#confirm-reopen");
  await expect(page.locator("#period-state")).toHaveText("Open");
});
