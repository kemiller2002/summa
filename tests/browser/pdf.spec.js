// The invoice's PDF (WI-0030 slice 4, INV-DOC-011) in the real page: the
// person attaches the PDF they saved from the print dialog, the engine reads
// it through Limen's files pack, checks and fingerprints it, keeps it in this
// environment's IndexedDB artifact database through the store pack, records
// it with the invoice, and offers the same bytes back as a download.
import { test, expect } from "./support.js";

const nav = (page, label) => page.locator(".summa-nav__link", { hasText: label });
const running = (page) => expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

async function issueInvoice(page) {
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
  // The reviewed draft shows the document it will be issued as.
  await expect(page.locator("#invoice-preview")).toBeVisible();
  await expect(page.locator("#preview-total")).toHaveText("1,000.00 USD");
  await page.click("#issue-invoice");
  await expect(page).toHaveURL(/#\/invoices\/INV-0001$/);
}

// A small but real PDF: the signature, one empty page, and a trailer.
const pdf = Buffer.from(
  "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
    "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n",
  "latin1"
);

test("the PDF the person saved is stored with the invoice and downloads as the same bytes", async ({ app: { page } }) => {
  await issueInvoice(page);
  await expect(page.locator("#pdf-status")).toHaveText(/^Not stored yet/);
  await page.setInputFiles("#invoice-pdf", { name: "invoice.pdf", mimeType: "application/pdf", buffer: pdf });
  await expect(page.locator("#notice")).toHaveText("The PDF is stored with the invoice.");
  await expect(page.locator("#pdf-status")).toHaveText(/^Stored\. SHA-256 [0-9a-f]{12}…$/);
  await expect(page.locator("#invoice-pdf")).toHaveCount(0);

  // The record and the bytes both survive a reload.
  await page.reload();
  await running(page);
  await expect(page.locator("#pdf-status")).toHaveText(/^Stored\./);
  const [download] = await Promise.all([page.waitForEvent("download"), page.click("#download-pdf")]);
  expect(download.suggestedFilename()).toMatch(/^INV-\d{4}-0001\.pdf$/);
  const saved = await download.createReadStream();
  const chunks = [];
  for await (const chunk of saved) chunks.push(chunk);
  expect(Buffer.concat(chunks).equals(pdf)).toBe(true);
});

test("a file that is not a PDF is refused, and the invoice can still take one", async ({ app: { page } }) => {
  await issueInvoice(page);
  await page.setInputFiles("#invoice-pdf", { name: "invoice.html", mimeType: "text/html", buffer: Buffer.from("<html></html>") });
  await expect(page.locator("#error")).toHaveText(/^The file is not a PDF\./);
  await expect(page.locator("#pdf-status")).toHaveText(/^Not stored yet/);
  await expect(page.locator("#invoice-pdf")).toBeVisible();
});

// INV-DOC-005: a long invoice prints across pages professionally. Under print
// media, Folio repeats the table header, keeps each row and the totals whole,
// sets page margins, and nothing overflows the page width.
test("a long invoice prints across pages with repeated headers, whole rows and totals kept together", async ({ app: { page } }) => {
  await nav(page, "Customers").click();
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  for (let index = 0; index < 60; index += 1) {
    if (index > 0) await page.click("#add-line");
    const row = page.locator("#draft-lines tbody tr").nth(index);
    await row.locator(".line-description").fill(`Consulting session ${index + 1}, with a description long enough to wrap onto a second line in print`);
    await row.locator(".line-rate").fill("100");
  }
  await page.click("#review-draft");
  await page.click("#issue-invoice");
  await expect(page.locator("#invoice-lines tbody tr")).toHaveCount(60);

  await page.emulateMedia({ media: "print" });
  const layout = await page.evaluate(() => {
    const style = (selector) => getComputedStyle(document.querySelector(selector));
    const document_ = document.querySelector("#invoice-document");
    // Every rule, through @import and @media, to find Folio's @page margins.
    const rulesOf = (sheet) => {
      try {
        return [...sheet.cssRules].flatMap((rule) =>
          rule.styleSheet ? rulesOf(rule.styleSheet) : rule.cssRules ? [rule, ...[...rule.cssRules]] : [rule]);
      } catch {
        return [];
      }
    };
    const pageRules = [...document.styleSheets].flatMap(rulesOf).filter((rule) => rule.type === CSSRule.PAGE_RULE);
    return {
      thead: style("#invoice-lines thead").display,
      rows: [...document.querySelectorAll("#invoice-lines tbody tr")].every((row) => getComputedStyle(row).breakInside === "avoid"),
      totals: getComputedStyle(document.querySelector("#invoice-document ef-print-keep")).breakInside,
      repeated: [...document_.querySelectorAll("ef-print-header, ef-print-footer")].map((element) => element.getAttribute("repeat")),
      overflow: document_.scrollWidth - document_.clientWidth,
      margins: pageRules.some((rule) => rule.style.margin !== "" || rule.style.marginTop !== "")
    };
  });
  expect(layout.thead).toBe("table-header-group");
  expect(layout.rows).toBe(true);
  expect(layout.totals).toBe("avoid");
  expect(layout.repeated).toEqual(["page", "page"]);
  expect(layout.overflow).toBeLessThanOrEqual(0);
  expect(layout.margins).toBe(true);

  // Printed, the 60 lines take more than one page.
  const printed = (await page.pdf({ format: "Letter" })).toString("latin1");
  expect((printed.match(/\/Type\s*\/Page(?!s)/g) ?? []).length).toBeGreaterThan(1);
});
