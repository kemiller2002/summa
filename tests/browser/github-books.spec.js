// Books on GitHub, end to end in a real browser (WI-0037): sign in through
// Fides (faked), set the organization up in an empty data repository, work
// in the books, and find each change on the branch as one commit made by
// Arca's GitHub adapter running in the WASM engine. GitHub's REST API is a
// fake served by Playwright routes (./github-fake.js).
import { expect, expectConsoleError } from "./support.js";
import { signInTest } from "./fake-deployment.js";
import { headFiles, history } from "./github-fake.js";

const FOLDER = "deployments/test/summa/datasets/org_acme";

signInTest("a listed administrator sets the books up on GitHub and each change is one commit there", async ({ deployment: { page, origin, github } }) => {
  await page.goto(`${origin}/#/customers`);
  await page.check("#keep-sign-in");
  await page.click("#sign-in");

  // An empty data repository: the listed administrator may set it up.
  await expect(page.locator("#found-books")).toBeVisible();
  await page.click("#found-books");
  await expect(page.locator(".summa-shell")).toBeVisible();
  await expect(page).toHaveURL(`${origin}/#/customers`);
  expect(Object.keys(headFiles(github.current()))).toContain(`${FOLDER}/records/summa.organization/org_acme.json`);

  // A change is shown at once and committed on the branch.
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText("Customer Acme added.");
  await expect.poll(() => Object.keys(headFiles(github.current()))).toContain(`${FOLDER}/records/summa.customer/CUST-0001.json`);
  expect(history(github.current())[0].message).toMatch(/^summa: add a customer\n/);

  // Another page opens the same books from GitHub.
  await page.reload();
  await expect(page.locator(".summa-shell")).toBeVisible();
  await expect(page.locator("#customers-table tbody tr", { hasText: "Acme" })).toHaveCount(1);
});

signInTest("a change made while GitHub cannot be reached is kept in this browser and sent when it can", async ({ deployment: { page, origin, github } }) => {
  expectConsoleError(page, /ERR_INTERNET_DISCONNECTED|Failed to load resource/);
  await page.goto(`${origin}/#/customers`);
  await page.check("#keep-sign-in");
  await page.click("#sign-in");
  await page.click("#found-books");
  await expect(page.locator(".summa-shell")).toBeVisible();

  github.reachable(false);
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  // Shown at once, and said to be waiting.
  await expect(page.locator("#customers-table tbody tr", { hasText: "Acme" })).toHaveCount(1);
  // (after the GitHub adapter has given up retrying for now)
  await expect(page.locator("#unsent-text")).toHaveText("1 change has not reached GitHub yet.", { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).not.toContain(`${FOLDER}/records/summa.customer/CUST-0001.json`);

  // GitHub comes back; the page is reloaded and sends what it kept first.
  github.reachable(true);
  await page.reload();
  await expect(page.locator(".summa-shell")).toBeVisible();
  await expect(page.locator("#customers-table tbody tr", { hasText: "Acme" })).toHaveCount(1);
  await expect(page.locator("#unsent")).toHaveCount(0);
  expect(Object.keys(headFiles(github.current()))).toContain(`${FOLDER}/records/summa.customer/CUST-0001.json`);
});

signInTest("books founded on GitHub can be invoiced at once: their accounts are founded with them", async ({ deployment: { page, origin, github } }) => {
  await page.goto(`${origin}/#/customers`);
  await page.check("#keep-sign-in");
  await page.click("#sign-in");
  await page.click("#found-books");
  await expect(page.locator(".summa-shell")).toBeVisible();
  // The starting chart is the organization's own records from the first commit.
  const files = Object.keys(headFiles(github.current()));
  for (const code of ["1000", "1100", "2100", "2200", "4000", "6500"]) {
    expect(files).toContain(`${FOLDER}/records/summa.account/${code}.json`);
  }

  await page.locator(".summa-nav__link", { hasText: "Settings" }).click();
  await page.fill("#company-legal-name", "Echelon Foundry LLC");
  await page.fill("#company-address", "1 Foundry Way");
  await page.fill("#company-email", "billing@echelon.example");
  await page.fill("#company-payment", "ACH to account ending 6789");
  await page.click("#save-company");

  await page.locator(".summa-nav__link", { hasText: "Customers" }).click();
  await page.fill("#customer-name", "Acme");
  await page.fill("#customer-address", "1 Main Street");
  await page.click("#add-customer");
  await expect(page.locator("#notice")).toHaveText("Customer Acme added.");

  await page.click("#new-invoice");
  await page.selectOption("#draft-customer", "CUST-0001");
  const row = page.locator("#draft-lines tbody tr").first();
  await row.locator(".line-description").fill("Assessment");
  await row.locator(".line-hours").fill("10");
  await row.locator(".line-rate").fill("100.00");
  await page.click("#review-draft");
  await expect(page.locator("#notice")).toHaveText("Ready to issue. Check the preview, then issue it.");
  await page.click("#issue-invoice");
  await page.click("#confirm-issue");
  await expect(page.locator("#invoice-total")).toHaveText("1,000.00 USD");
  // Shown at once; committed once GitHub has taken the draft and then the
  // issue, in order. Measured at about 1.1 s for the draft and 2.3 s for the
  // issue (WI-0029, SUM0-034), so the issue lands about 3.4 s after review.
  await expect
    .poll(() => Object.keys(headFiles(github.current())).some((p) => p.startsWith(`${FOLDER}/records/summa.invoice/`)), { timeout: 10_000 })
    .toBe(true);
  expect(history(github.current())[0].message).toMatch(/^summa: issue an invoice\n/);
});
