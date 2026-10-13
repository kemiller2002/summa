// UI-LAYOUT-INTERACTION@1.0.0 conformance for the accounting application's
// books on GitHub (summa#90, WI-0053): when GitHub cannot be reached a change
// is never reported as sent, retrying while it is still unreachable claims
// nothing, and a retry once it is back sends it (ULI-A07). GitHub's REST API
// is the Playwright-routed fake in ./github-fake.js.
import { expect, expectConsoleError } from "./support.js";
import { signInTest } from "./fake-deployment.js";
import { commitExternally, headFiles } from "./github-fake.js";
import { expectActionOutcome } from "./ui-conformance-helpers.js";

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

// ---- Formerly UNKNOWN actions (summa#95 follow-up, WI-0054) -----------------

const FOLDER = "deployments/test/summa/datasets/org_acme";
const MANIFEST = `${FOLDER}/records/summa.organization/org_acme.json`;
const MEMBER = `${FOLDER}/records/summa.member/github_583231.json`;

async function foundBooks(page, origin) {
  await page.goto(`${origin}/#/customers`);
  await page.getByRole("checkbox", { name: "Keep me signed in while this tab is open" }).check();
  await page.getByRole("button", { name: "Sign in with GitHub" }).click();
  await page.getByRole("button", { name: "Set up the books" }).click();
  await expect(page.getByRole("navigation", { name: "Primary" })).toBeVisible();
}

// A queued change, waiting because GitHub could not be reached.
async function keepUnsent(page, github, name) {
  github.reachable(false);
  await page.getByRole("textbox", { name: "Name", exact: true }).fill(name);
  await page.getByRole("button", { name: "Add customer" }).click();
  await expect(page.locator("#unsent-text")).toHaveText("1 change has not reached GitHub yet.", { timeout: 45_000 });
}

// A customer record as Summa writes it (format observed on the fake).
const customerRecord = (name) => JSON.stringify({
  arcaRecord: 1,
  body: { active: true, billingAddress: "", billingName: name, defaultTerms: null, email: "", id: "CUST-0001", name, paymentProfileId: null },
  id: "CUST-0001", mutability: "mutable", schemaVersion: 1, type: "summa.customer"
});

signInTest("@ui-conformance Sign out in the header ends the session and returns to the sign-in gate (ULI-A02)", async ({ deployment: { page, origin } }) => {
  await foundBooks(page, origin);
  await expectActionOutcome(page, {
    action: { role: "button", name: "Sign out octocat" },
    outcome: { kind: "visible", target: { role: "heading", name: "Sign in to Summa" }, expected: true }
  });
  await expect(page.getByRole("navigation", { name: "Primary" })).toHaveCount(0);
  // Signed out means signed out: a reload does not bring the books back.
  await page.reload();
  await expect(page.getByRole("heading", { name: "Sign in to Summa" })).toBeVisible();
});

signInTest("@ui-conformance a change another sign-in left is held; Send them as me sends it (ULI-A07)", async ({ deployment: { page, origin, github } }) => {
  signInTest.setTimeout(180_000);
  expectConsoleError(page, /ERR_INTERNET_DISCONNECTED|Failed to load resource/);
  await foundBooks(page, origin);
  await keepUnsent(page, github, "Acme");
  // Make the kept change look as an earlier Summa left it for another person.
  await page.evaluate(() => {
    const key = Object.keys(localStorage).find((k) => k.startsWith("arca.queue."));
    const queue = JSON.parse(localStorage.getItem(key));
    for (const entry of queue.entries) Object.assign(entry.operation, { accountId: null, actorId: "github:999" });
    localStorage.setItem(key, JSON.stringify(queue));
  });
  github.reachable(true);
  await page.reload();
  await expect(page.getByRole("button", { name: "Send them as me" })).toBeVisible({ timeout: 45_000 });
  // Held, not sent as whoever signed in.
  expect(Object.keys(headFiles(github.current()))).not.toContain(CUSTOMER);
  await page.getByRole("button", { name: "Send them as me" }).click();
  await expect(page.locator("#unsent")).toHaveCount(0, { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).toContain(CUSTOMER);
});

signInTest("@ui-conformance a change another sign-in left can be discarded, and is then not sent (ULI-A07)", async ({ deployment: { page, origin, github } }) => {
  signInTest.setTimeout(180_000);
  expectConsoleError(page, /ERR_INTERNET_DISCONNECTED|Failed to load resource/);
  await foundBooks(page, origin);
  await keepUnsent(page, github, "Acme");
  await page.evaluate(() => {
    const key = Object.keys(localStorage).find((k) => k.startsWith("arca.queue."));
    const queue = JSON.parse(localStorage.getItem(key));
    for (const entry of queue.entries) Object.assign(entry.operation, { accountId: null, actorId: "github:999" });
    localStorage.setItem(key, JSON.stringify(queue));
  });
  github.reachable(true);
  await page.reload();
  await expect(page.getByRole("button", { name: "Discard them" })).toBeVisible({ timeout: 45_000 });
  await page.getByRole("button", { name: "Discard them" }).click();
  await expect(page.locator("#unsent")).toHaveCount(0, { timeout: 45_000 });
  expect(Object.keys(headFiles(github.current()))).not.toContain(CUSTOMER);
});

signInTest("@ui-conformance a kept change GitHub moved under waits for the person, who may give it up (ULI-A07)", async ({ deployment: { page, origin, github } }) => {
  signInTest.setTimeout(180_000);
  expectConsoleError(page, /ERR_INTERNET_DISCONNECTED|Failed to load resource/);
  await foundBooks(page, origin);
  await keepUnsent(page, github, "Acme");
  // Meanwhile someone else records CUST-0001 on GitHub.
  github.update((state) => commitExternally(state, { [CUSTOMER]: customerRecord("Globex") }, "summa: add a customer (elsewhere)"));
  github.reachable(true);
  await page.reload();
  const giveUp = page.getByRole("button", { name: "Give this change up" });
  await expect(giveUp).toBeVisible({ timeout: 45_000 });
  // Nothing is overwritten while it waits.
  expect(JSON.parse(headFiles(github.current())[CUSTOMER]).body.name).toBe("Globex");
  await giveUp.click();
  await expect(page.locator("#unsent")).toHaveCount(0, { timeout: 45_000 });
  expect(JSON.parse(headFiles(github.current())[CUSTOMER]).body.name).toBe("Globex");
});

signInTest("@ui-conformance books with no listed administrator open only after a listed one confirms (ULI-A02, ULI-A04)", async ({ deployment: { page, origin, github } }) => {
  await foundBooks(page, origin);
  // The roster loses its only administrator (an edit outside Summa).
  github.update((state) => commitExternally(state, { [MEMBER]: null }, "remove the member record"));
  await page.reload();
  await expect(page.getByRole("heading", { name: "These books have no listed administrator" })).toBeVisible();
  await expectActionOutcome(page, {
    action: { role: "button", name: "Confirm me as administrator" },
    outcome: { kind: "visible", target: { role: "navigation", name: "Primary" }, expected: true },
    // Writing to GitHub and reopening the books takes seconds, not one frame.
    timeoutMs: 45_000
  });
  await expect.poll(() => Object.keys(headFiles(github.current()))).toContain(MEMBER);
});

signInTest("@ui-conformance books written by an earlier Summa are updated in one commit by Update the books (ULI-A02)", async ({ deployment: { page, origin, github } }) => {
  await foundBooks(page, origin);
  // Rewrite the manifest as schema 1 left it: without the three accounts.
  github.update((state) => {
    const files = headFiles(state);
    const manifest = JSON.parse(files[MANIFEST]);
    for (const key of ["customerCreditsAccount", "customerDepositsAccount", "badDebtAccount"]) delete manifest.body.accounting[key];
    manifest.schemaVersion = 1;
    const arca = files[`${FOLDER}/arca-manifest.json`].replace('"summa.organization":2', '"summa.organization":1');
    return commitExternally(state, { [MANIFEST]: JSON.stringify(manifest), [`${FOLDER}/arca-manifest.json`]: arca }, "schema 1 books");
  });
  await page.reload();
  await expect(page.getByRole("heading", { name: "These books need updating before anyone works in them" })).toBeVisible();
  await expectActionOutcome(page, {
    action: { role: "button", name: "Update the books" },
    outcome: { kind: "visible", target: { role: "navigation", name: "Primary" }, expected: true },
    // Writing to GitHub and reopening the books takes seconds, not one frame.
    timeoutMs: 45_000
  });
  await expect.poll(() => JSON.parse(headFiles(github.current())[MANIFEST]).schemaVersion).toBe(2);
  expect(JSON.parse(headFiles(github.current())[MANIFEST]).body.accounting.badDebtAccount).toBe("6500");
});

signInTest("@ui-conformance Check the books reads GitHub and reports the result (ULI-A02, ULI-A07)", async ({ deployment: { page, origin } }) => {
  await foundBooks(page, origin);
  await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Settings", exact: true }).click();
  // Before this repair the folder read waited for a later message and the
  // check never answered in the browser (the same Turn defect as Confirm).
  await page.getByRole("button", { name: "Check the books" }).click();
  await expect(page.getByText("No record was edited outside Summa, and every record passed its checks.")).toBeVisible({ timeout: 45_000 });
});
