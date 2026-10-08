// Sign-in through Fides, end to end in a real browser (WI-0035, WI-0043, SUM0-003,
// SUM0-004, SUM3-012): the deployment's configuration names a GitHub data
// location and the exchange, the page leaves for GitHub, GitHub sends it back
// with a code, Fides' client (running in the WASM engine) exchanges it, and
// Summa knows the person as the identity GitHub resolved. The deployment's
// configuration, Fides' exchange and GitHub's authorize page are fakes served
// by Playwright routes: Fides is not deployed anywhere yet.
import { test, expect } from "./support.js";
import { createServer as createAppServer } from "../../tools/app_server.mjs";

const ACCESS_TOKEN = "gho_SUMMABROWSERACCESSTOKEN0123456789";
const REFRESH_TOKEN = "ghr_SUMMABROWSERREFRESHTOKEN0123456789";
const cors = { "access-control-allow-origin": "*", "access-control-allow-headers": "content-type", "access-control-allow-methods": "POST" };

const configuration = (origin) => ({
  environment: "test",
  environmentName: "test",
  location: { owner: "acme", repository: "summa-data", branch: "main", basePath: "deployments/test" },
  identity: { exchange: "https://fides.test", application: "summa-test", provider: "github", clientId: "Iv23liTEST", redirectUri: `${origin}/` },
  organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", defaultCurrency: "USD", timeZone: "America/New_York", administrators: ["583231"] }]
});

// The deployment, the exchange and GitHub's authorize page. Returns the
// exchange paths called, in order.
async function fakeDeployment(page, origin) {
  const exchanged = [];
  await page.route("**/summa.deployment.json", (route) => route.fulfill({ json: configuration(origin) }));
  await page.route("https://fides.test/**", async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    const path = new URL(request.url()).pathname;
    exchanged.push(path);
    const body = JSON.parse(request.postData() ?? "{}");
    if (path === "/v1/token" && body.code === "good-code") {
      const at = (hours) => new Date(Date.now() + hours * 3600_000).toISOString().replace(/\.\d+Z$/, "Z");
      return route.fulfill({
        status: 200,
        headers: cors,
        contentType: "application/json",
        body: JSON.stringify({
          accessToken: ACCESS_TOKEN,
          accessTokenExpiresAt: at(8),
          refreshToken: REFRESH_TOKEN,
          refreshTokenExpiresAt: at(24 * 180),
          identity: { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" }
        })
      });
    }
    if (path === "/v1/token") return route.fulfill({ status: 400, headers: cors, contentType: "application/json", body: '{"error":"code_rejected"}' });
    if (path === "/v1/revoke") return route.fulfill({ status: 204, headers: cors });
    return route.fulfill({ status: 404, headers: cors, contentType: "application/json", body: '{"error":"not_found"}' });
  });
  // GitHub's authorize page: the person approves, GitHub redirects back.
  await page.route("https://github.com/login/oauth/authorize**", (route) => {
    const url = new URL(route.request().url());
    const back = new URL(url.searchParams.get("redirect_uri"));
    back.searchParams.set("code", "good-code");
    back.searchParams.set("state", url.searchParams.get("state"));
    return route.fulfill({ status: 302, headers: { location: back.href } });
  });
  return exchanged;
}

const signInTest = test.extend({
  deployment: async ({ page }, use) => {
    const server = createAppServer();
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    const exchanged = await fakeDeployment(page, origin);
    await use({ page, origin, exchanged });
    server.close();
  }
});

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
  await expect(page.locator("#signed-in-login")).toHaveText("octocat");
  await expect(page).toHaveURL(`${origin}/#/customers?q=acme`);
  expect(await storageText(page, "sessionStorage")).not.toContain("summa.returnTo");
  expect(exchanged).toEqual(["/v1/token"]);

  // The token is in this tab's session storage, as chosen, and nowhere else.
  expect(await storageText(page, "sessionStorage")).toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain("gho_");
  expect(await page.content()).not.toContain(ACCESS_TOKEN);

  // Kept for the tab: a reload keeps the session.
  await page.reload();
  await expect(page.locator("#signed-in-login")).toHaveText("octocat");

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
  await expect(page.locator("#signed-in-login")).toHaveText("octocat");
  expect(await storageText(page, "sessionStorage")).not.toContain(ACCESS_TOKEN);
  expect(await storageText(page, "localStorage")).not.toContain(ACCESS_TOKEN);

  await page.reload();
  await expect(page.locator("#sign-in")).toBeVisible();
});

signInTest("a callback that did not start in this tab is refused without calling the exchange", async ({ deployment: { page, origin, exchanged } }) => {
  await page.goto(`${origin}/?code=good-code&state=forged`);
  await expect(page.locator("#sign-in-notice")).toContainText("did not start in this tab");
  await expect(page.locator("#signed-in-login")).toHaveCount(0);
  expect(exchanged).toEqual([]);
});
