// Sign-in through Fides, end to end in a real browser (WI-0035, WI-0043, SUM0-003,
// SUM0-004, SUM3-012): the deployment's configuration names a GitHub data
// location and the exchange, the page leaves for GitHub, GitHub sends it back
// with a code, Fides' client (running in the WASM engine) exchanges it, and
// Summa knows the person as the identity GitHub resolved. The deployment's
// configuration, Fides' exchange and GitHub's authorize page are fakes served
// by Playwright routes: Fides is not deployed anywhere yet.
import { expect } from "./support.js";
import { ACCESS_TOKEN, signInTest } from "./fake-deployment.js";

const storageText = (page, store) => page.evaluate((name) => JSON.stringify({ ...window[name] }), store);

signInTest("a person signs in with GitHub as the identity GitHub resolved, keeps it for the tab, then signs out", async ({ deployment: { page, origin, exchanged } }) => {
  await page.goto(`${origin}/#/customers?q=acme`);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");

  // Nothing of the books before sign-in; the link asked for is kept.
  await expect(page.locator("#sign-in")).toBeVisible();
  await expect(page.locator(".summa-shell")).toHaveCount(0);
  await expect(page).toHaveURL(`${origin}/#/sign-in?returnTo=%2Fcustomers%3Fq%3Dacme`);

  await page.check("#keep-sign-in");
  await page.click("#sign-in");

  // Back from GitHub: signed in, with the callback removed from the address,
  // at the link first asked for (WI-0043), which the tab then forgets.
  await expect(page.locator("#sign-out")).toContainText("octocat");
  await expect(page).toHaveURL(`${origin}/#/customers?q=acme`);
  expect(await storageText(page, "sessionStorage")).not.toContain("summa.returnTo");
  expect(exchanged).toEqual(["/v1/token"]);

  // The token is in this tab's session storage, as chosen, and nowhere else.
  expect(await storageText(page, "sessionStorage")).toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain("gho_");
  expect(await page.content()).not.toContain(ACCESS_TOKEN);

  // Kept for the tab: a reload keeps the session.
  await page.reload();
  await expect(page.locator("#sign-out")).toContainText("octocat");

  // Sign out: the token is cleared from the tab and revoked at GitHub.
  await page.click("#sign-out");
  await expect(page.locator("#sign-in")).toBeVisible();
  await expect(page.locator("#sign-in-notice")).toContainText("no longer holds your GitHub token");
  expect(exchanged).toContain("/v1/revoke");
  expect(await storageText(page, "sessionStorage")).not.toContain(ACCESS_TOKEN);
});

signInTest("this-page retention keeps the token in the page only, and a reload ends it", async ({ deployment: { page, origin } }) => {
  await page.goto(`${origin}/`);
  await page.click("#sign-in");
  await expect(page.locator("#sign-out")).toContainText("octocat");
  expect(await storageText(page, "sessionStorage")).not.toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain(ACCESS_TOKEN);

  await page.reload();
  await expect(page.locator("#sign-in")).toBeVisible();
});

signInTest("a callback that did not start in this tab is refused without calling the exchange", async ({ deployment: { page, origin, exchanged } }) => {
  await page.goto(`${origin}/?code=good-code&state=forged`);
  await expect(page.locator("#sign-in-notice")).toContainText("did not start in this tab");
  await expect(page.locator("#sign-out")).toHaveCount(0);
  expect(exchanged).toEqual([]);
});
