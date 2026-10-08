// The accounting application end to end (WI-0030): the real page, the WASM
// engine, Limen's kernel, Forma and Folio, in a real browser. The books live
// in the browser's storage, so each test starts from an empty profile.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });

async function addCustomer(page, name, terms) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", name);
  await page.fill("#customer-email", "ap@example.com");
  await page.fill("#customer-address", "1 Main Street\nSpringfield");
  if (terms !== undefined) await page.fill("#customer-terms", terms);
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText(`Customer ${name} added.`);
}

async function draftInvoice(page, customerId, lines) {
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", customerId);
  for (const [index, [description, hours, rate]] of lines.entries()) {
    if (index > 0) await page.click("#add-line");
    const row = page.locator("#draft-lines tbody tr").nth(index);
    await row.locator(".line-description").fill(description);
    await row.locator(".line-hours").fill(hours);
    await row.locator(".line-rate").fill(rate);
  }
}

test("a new browser starts its own books, marked as a local demo", async ({ app: { page } }) => {
  await expect(page.locator("[data-pages-banner]")).toHaveText("SUMMA · LOCAL · local development · books are kept only in this browser");
  await expect(page.locator("#notice")).toHaveText("A new set of books was started in this browser.");
  await expect(page.locator("#total-outstanding")).toHaveText("0.00 USD");
  await expect(nav(page, "Home")).toHaveAttribute("aria-current", "page");
});

test("customer, invoice, review, issue, payment: the first vertical slice in the browser", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp", "30");
  await expect(page.locator("#customers-table tbody tr")).toHaveCount(1);

  await draftInvoice(page, "CUST-0001", [["Architecture assessment", "34.5", "175.00"], ["Workshop", "2", "90"]]);
  await expect(page.locator("#draft-total")).toHaveText("6,217.50 USD");
  await expect(page.locator("#issue-invoice")).toBeDisabled();
  await page.click("#review-draft");
  await expect(page.locator("#notice")).toHaveText("Ready to issue. Check the preview, then issue it.");
  await expect(page.locator("#issue-invoice")).toBeEnabled();
  await page.click("#issue-invoice");

  // The issued invoice, as its Folio document.
  await expect(page.locator("#notice")).toHaveText(/^Invoice INV-\d{4}-0001 issued\.$/);
  await expect(page.locator("#invoice-document")).toBeVisible();
  await expect(page.locator("#invoice-lines tbody tr")).toHaveCount(2);
  await expect(page.locator("#invoice-total")).toHaveText("6,217.50 USD");
  await expect(page.locator("#invoice-status")).toHaveText("Unpaid");

  // A partial payment, then the rest.
  await page.fill("#payment-amount", "1000.00");
  await page.fill("#payment-reference", "ACH-1");
  await page.click("#record-payment");
  await expect(page.locator("#invoice-status")).toHaveText("Partly paid");
  await expect(page.locator("#invoice-outstanding")).toHaveText("5,217.50 USD");
  await page.fill("#payment-amount", "6000.00");
  await page.click("#record-payment");
  await expect(page.locator("#error")).toHaveText("That is more than the 5,217.50 USD still owed.");
  await page.fill("#payment-amount", "5217.50");
  await page.click("#record-payment");
  await expect(page.locator("#invoice-status")).toHaveText("Paid");
  await expect(page.locator("#payment-form")).toHaveCount(0);

  await nav(page, "Home").click();
  await expect(page.locator("#total-outstanding")).toHaveText("0.00 USD");
  await expect(page.locator("#invoice-count")).toHaveText("1");
});

test("a draft that cannot be issued says why and what resolves it", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await nav(page, "Settings").click();
  await page.fill("#company-address", "");
  await page.click("#save-company");
  await draftInvoice(page, "CUST-0001", [["Consulting", "1", "100"]]);
  await page.click("#review-draft");
  await expect(page.locator("#blockers li")).toHaveText(["The issuer's address is missing. Complete the company information in the organization settings."]);
  await expect(page.locator("#issue-invoice")).toBeDisabled();
});

test("the books survive a reload and stay checked", async ({ app: { page } }) => {
  await addCustomer(page, "ABC Corp");
  await draftInvoice(page, "CUST-0001", [["Consulting", "2", "150"]]);
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await expect(page.locator("#invoice-total")).toHaveText("300.00 USD");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#total-outstanding")).toHaveText("300.00 USD");
  await nav(page, "Receivables").click();
  await expect(page.locator("#aging-table tbody tr td").first()).toHaveText("ABC Corp");

  // Tampered books are refused, never used.
  await page.evaluate(() => {
    const books = JSON.parse(localStorage.getItem("summa.local.books"));
    const path = Object.keys(books.records).find((p) => p.startsWith("records/summa.entry/"));
    books.records[path] = books.records[path].replace('"minor":30000', '"minor":30001');
    localStorage.setItem("summa.local.books", JSON.stringify(books));
  });
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#storage-problems li").first()).toBeVisible();
  await page.click("#reset-books");
  await expect(page.locator("#total-outstanding")).toHaveText("0.00 USD");
});

test("keyboard only: every control is reachable and named", async ({ app: { page } }) => {
  await page.keyboard.press("Tab");
  await expect(page.locator(".ef-skip-link")).toBeFocused();
  for (const label of ["Home", "Invoices", "Customers", "Receivables", "Settings"]) {
    await expect(nav(page, label)).toBeVisible();
  }
  await nav(page, "Customers").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#customers-title")).toBeVisible();
});
