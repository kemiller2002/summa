// UI-LAYOUT-INTERACTION@1.0.0 conformance for the accounting application's
// books on GitHub (summa#90, WI-0053): when GitHub cannot be reached a change
// is never reported as sent, retrying while it is still unreachable claims
// nothing, and a retry once it is back sends it (ULI-A07). GitHub's REST API
// is the Playwright-routed fake in ./github-fake.js.
import { expect, expectConsoleError } from "./support.js";
import { signInTest } from "./fake-deployment.js";
import { headFiles } from "./github-fake.js";

const CUSTOMER = "deployments/test/summa/datasets/org_acme/records/summa.customer/CUST-0001.json";

signInTest("@ui-conformance books on GitHub: an unsent change is never shown as sent, and Send now delivers it once GitHub is back (ULI-A07, ULI-A10)", async ({ deployment: { page, origin, github } }) => {
  signInTest.setTimeout(180_000);
  expectConsoleError(page, /ERR_INTERNET_DISCONNECTED|Failed to load resource/);
  await page.goto(`${origin}/#/customers`);
  await page.getByRole("checkbox", { name: "Keep me signed in while this tab is open" }).check();
  await page.getByRole("button", { name: "Sign in with GitHub" }).click();
  await page.getByRole("button", { name: "Set up the books" }).click();
  await expect(page.getByRole("navigation", { name: "Primary" })).toBeVisible();

  github.reachable(false);
  await page.getByRole("textbox", { name: "Name", exact: true }).fill("Acme");
  await page.getByRole("textbox", { name: "Billing address" }).fill("1 Main Street");
  await page.getByRole("button", { name: "Add customer" }).click();
  const unsent = page.locator("#unsent-text");
  await expect(unsent).toHaveText("1 change has not reached GitHub yet.", { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).not.toContain(CUSTOMER);

  // Retrying while GitHub is still unreachable claims nothing.
  await page.getByRole("button", { name: "Send now" }).click();
  await expect(unsent).toHaveText("1 change has not reached GitHub yet.", { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).not.toContain(CUSTOMER);

  // GitHub is back: the same control sends the change, and the waiting
  // notice goes only once GitHub has it.
  github.reachable(true);
  await page.getByRole("button", { name: "Send now" }).click();
  await expect(page.locator("#unsent")).toHaveCount(0, { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).toContain(CUSTOMER);
  await expect(page.getByRole("link", { name: "Acme" })).toBeVisible();
});
