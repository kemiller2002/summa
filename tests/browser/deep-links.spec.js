// Deep links in the accounting application (WI-0041, SUM-LINK-001..012):
// every view, filter, sort, search and date is in the address, so a copied
// link opens the same view: after a reload, in a new tab, after Back, and on
// a static host at a sub-path with no base href.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "./support.js";
import { copies } from "../../tools/pages/assemble-site.mjs";
import { contentType } from "../../tools/web_static.mjs";

const REPOSITORY = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });

async function addCustomer(page, name) {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", name);
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText(`Customer ${name} added.`);
}

async function issueInvoice(page, customerId, rate) {
  await page.click("#new-invoice");
  await expect(page).toHaveURL(/#\/invoices\/new$/);
  await page.selectOption("#draft-customer", customerId);
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Consulting");
  await row.locator(".line-rate").fill(rate);
  await page.click("#review-draft");
  // Saved, the new invoice is its draft: the address says which one.
  await expect(page).toHaveURL(/#\/drafts\/D-\d{4}$/);
  await page.click("#issue-invoice");
  await expect(page).toHaveURL(/#\/invoices\/INV-\d{4}$/);
}

async function twoInvoices(page) {
  await addCustomer(page, "Acme");
  await addCustomer(page, "Beta");
  await issueInvoice(page, "CUST-0001", "100");
  await issueInvoice(page, "CUST-0002", "250");
}

const running = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

test("navigation is links: each view has its own address", async ({ app: { page } }) => {
  await expect(page).toHaveURL(/\/$/);
  await nav(page, "Invoices").click();
  await expect(page).toHaveURL(/#\/invoices$/);
  await expect(nav(page, "Invoices")).toHaveAttribute("aria-current", "page");
  await nav(page, "Receivables").click();
  await expect(page).toHaveURL(/#\/receivables$/);
  await expect(page.locator("#receivables-title")).toBeVisible();
});

test("filters, sort and search are in the address and survive a reload", async ({ app: { page } }) => {
  await twoInvoices(page);
  await nav(page, "Invoices").click();
  await expect(page.locator("#invoices-table tbody tr")).toHaveCount(2);
  await page.fill("#invoice-search", "acme");
  await expect(page).toHaveURL(/#\/invoices\?q=acme$/);
  await page.selectOption("#invoice-sort", "number");
  await page.locator('.invoice-status[data-status="unpaid"]').check();
  await page.selectOption("#invoice-customer", "CUST-0001");
  await expect(page).toHaveURL(/#\/invoices\?q=acme&status=unpaid&customer=CUST-0001&sort=number$/);
  await expect(page.locator("#invoices-table tbody tr")).toHaveCount(1);

  await page.reload();
  await running(page);
  await expect(page.locator("#invoice-search")).toHaveValue("acme");
  await expect(page.locator("#invoice-sort")).toHaveValue("number");
  // A select whose options come from the books still shows the linked choice.
  await expect(page.locator("#invoice-customer")).toHaveValue("CUST-0001");
  await expect(page.locator('.invoice-status[data-status="unpaid"]')).toBeChecked();
  await expect(page.locator("#invoices-table tbody tr")).toHaveCount(1);
  await expect(page.locator("#invoice-count-text")).toHaveText("1 of 2 invoices");
});

test("Back returns to the previous place, not the previous filter", async ({ app: { page } }) => {
  await twoInvoices(page);
  await nav(page, "Home").click();
  await nav(page, "Invoices").click();
  await page.fill("#invoice-search", "a");
  await page.fill("#invoice-search", "ac");
  await page.fill("#invoice-search", "acme");
  await page.locator("#invoices-table tbody tr a").first().click();
  await expect(page).toHaveURL(/#\/invoices\/INV-0001$/);
  await page.goBack();
  await expect(page).toHaveURL(/#\/invoices\?q=acme$/);
  await expect(page.locator("#invoice-search")).toHaveValue("acme");
  await page.goBack();
  await expect(page).toHaveURL(/\/#\/$/);
  await expect(page.locator("#dashboard-title")).toBeVisible();
});

test("a link opened in a new tab opens the same view", async ({ app: { page, origin } }) => {
  await twoInvoices(page);
  await page.click('.summa-tab[data-tab="history"]');
  await expect(page).toHaveURL(/#\/invoices\/INV-0002\?tab=history$/);

  const other = await page.context().newPage();
  await other.goto(`${origin}/#/invoices/INV-0002?tab=history`);
  await running(other);
  await expect(other.locator("#invoice-title")).toContainText("INV-");
  await expect(other.locator("#invoice-history tbody tr").first()).toBeVisible();
  await expect(other.locator('.summa-tab[data-tab="history"]')).toHaveAttribute("aria-pressed", "true");
  await other.close();
});

test("links that open nothing say so", async ({ app: { page, origin } }) => {
  for (const hash of ["#/no-such-place", "#/invoices/INV-9999"]) {
    await page.goto(`${origin}/${hash}`);
    await running(page);
    await expect(page.locator("#not-found-title")).toHaveText("Nothing is at this link");
  }
  await page.goto(`${origin}/#/periods/2026-13`);
  await running(page);
  await expect(page.locator("#invalid-link")).toHaveText("The link's 'period' is '2026-13', but Summa expects month.");
  await page.locator("#main a", { hasText: "Go to the home page" }).click();
  await expect(page.locator("#dashboard-title")).toBeVisible();
});

test("Copy link copies the canonical address of the view", async ({ app: { page } }) => {
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await addCustomer(page, "Acme");
  await page.selectOption("#customer-sort", "balance");
  await page.click("#copy-link");
  await expect(page.locator("#notice")).toHaveText("Link copied.");
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied).toBe(page.url());
  expect(copied).toMatch(/#\/customers\?sort=balance$/);
});

test("the skip link moves to the content without losing the place", async ({ app: { page } }) => {
  await nav(page, "Customers").click();
  await page.locator(".ef-skip-link").focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/customers$/);
  await expect(page.locator("#customers-title")).toBeVisible();
});

// GitHub Pages serves the site from a sub-path (https://owner.github.io/summa/)
// with no base href and no server routing: the place lives after the "#".
test("deep links work on a static host at a sub-path", async ({ page }) => {
  const prefix = "/summa/";
  const server = http.createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    const relative = decodeURIComponent(url.pathname.slice(prefix.length)).replace(/\/$/, "/index.html");
    const file = path.resolve(REPOSITORY, relative);
    const published = url.pathname.startsWith(prefix) && copies.some((dir) => file.startsWith(path.join(REPOSITORY, dir) + path.sep));
    if (!published) { res.writeHead(404); res.end(); return; }
    fs.readFile(file, (error, content) => {
      if (error) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { "Content-Type": contentType(file) });
      res.end(content);
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const site = `http://127.0.0.1:${server.address().port}${prefix}app/`;
  try {
    await page.goto(`${site}#/invoices?sort=due&overdue=true`);
    await running(page);
    await expect(page).toHaveURL(`${site}#/invoices?overdue=true&sort=due`);
    await expect(page.locator("#invoices-title")).toBeVisible();
    await nav(page, "Customers").click();
    await expect(page).toHaveURL(`${site}#/customers`);
  } finally {
    server.close();
  }
});
