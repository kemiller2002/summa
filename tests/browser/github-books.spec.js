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
