// Payments, customers, credit memos and engagements (WI-0030 slice 2) in
// the real page: each screen is reached by a link, opens again from its own
// address, and the credit memo is shown as its Folio document.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });
const running = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

async function addCustomer(page, name) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", name);
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText(`Customer ${name} added.`);
}

test("from a customer to an invoice, a payment and a credit memo, each at its own address", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await page.locator("#customers-table a", { hasText: "Acme" }).click();
  await expect(page).toHaveURL(/#\/customers\/CUST-0001$/);
  await expect(page.locator("#customer-title")).toHaveText("Acme");

  await page.click("#customer-new-invoice");
  await expect(page).toHaveURL(/#\/invoices\/new\?customer=CUST-0001$/);
  await expect(page.locator("#draft-customer")).toHaveValue("CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Assessment");
  await row.locator(".line-rate").fill("1000");
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await expect(page).toHaveURL(/#\/invoices\/INV-0001$/);

  await page.click('.summa-tab[data-tab="payments"]');
  await page.fill("#payment-amount", "400");
  await page.fill("#payment-reference", "CHK-1042");
  await page.click("#record-payment");
  await expect(page.locator("#invoice-outstanding")).toHaveText("600.00 USD");

  await page.fill("#credit-amount", "150");
  await page.fill("#credit-reason", "Workshop shortened");
  await page.click("#issue-credit");
  await expect(page.locator("#notice")).toHaveText("Credit memo CM-0001 issued and applied.");
  await expect(page.locator("#invoice-outstanding")).toHaveText("450.00 USD");

  await page.locator("#invoice-credits a", { hasText: "Credit memo CM-0001" }).click();
  await expect(page).toHaveURL(/#\/credit-memos\/CM-0001$/);
  await expect(page.locator("#credit-memo-document")).toBeVisible();
  await expect(page.locator("#credit-memo-reason")).toHaveText("Workshop shortened");
  await expect(page.locator("#credit-memo-amount")).toHaveText("150.00 USD");

  // The credit memo's own address opens it again.
  await page.reload();
  await running(page);
  await expect(page.locator("#credit-memo-document")).toBeVisible();
  await page.click("#credit-memo-invoice");
  await expect(page).toHaveURL(/#\/invoices\/INV-0001$/);

  await nav(page, "Customers").click();
  await page.locator("#customers-table a", { hasText: "Acme" }).click();
  await page.click('.summa-tab[data-tab="credits"]');
  await expect(page).toHaveURL(/#\/customers\/CUST-0001\?tab=credits$/);
  await expect(page.locator("#customer-credits tbody tr")).toHaveCount(1);
});

test("payments are filtered in the address and each opens to what it paid", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Work");
  await row.locator(".line-rate").fill("300");
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await page.click('.summa-tab[data-tab="payments"]');
  await page.fill("#payment-amount", "300");
  await page.fill("#payment-reference", "ACH-77");
  await page.click("#record-payment");

  await nav(page, "Payments").click();
  await expect(page).toHaveURL(/#\/payments$/);
  await page.fill("#payment-search", "ach-77");
  await page.selectOption("#payment-sort", "oldest");
  await expect(page).toHaveURL(/#\/payments\?q=ach-77&sort=oldest$/);
  await page.reload();
  await running(page);
  await expect(page.locator("#payment-search")).toHaveValue("ach-77");
  await expect(page.locator("#payments-table tbody tr")).toHaveCount(1);
  await page.locator("#payments-table a", { hasText: "PAY-0001" }).click();
  await expect(page).toHaveURL(/#\/payments\/PAY-0001$/);
  await expect(page.locator("#payment-amount-text")).toHaveText("300.00 USD");
  await expect(page.locator("#payment-allocations tbody tr")).toHaveCount(1);
});

test("an engagement is added and opens at its own address", async ({ app: { page } }) => {
  await addCustomer(page, "Acme");
  await nav(page, "Engagements").click();
  await page.selectOption("#engagement-customer", "CUST-0001");
  await page.fill("#engagement-name", "Retainer");
  await page.fill("#engagement-fee", "5000");
  await page.click("#add-engagement");
  await expect(page).toHaveURL(/#\/engagements\/ENG-0001$/);
  await expect(page.locator("#engagement-title")).toHaveText("Retainer");
  await expect(page.locator("#engagement-fee-text")).toHaveText("5,000.00 USD");
  await page.goBack();
  await expect(page).toHaveURL(/#\/engagements$/);
  await expect(page.locator("#engagements-table tbody tr")).toHaveCount(1);
});
