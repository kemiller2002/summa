// The deployment, Fides' exchange and GitHub's authorize page, as fakes for
// the browser suite (WI-0035, WI-0037). Fides is not deployed anywhere yet.
import { createServer as createAppServer } from "../../tools/app_server.mjs";
import { test, expectConsoleError } from "./support.js";
import { repository, serveGitHub } from "./github-fake.js";

export const ACCESS_TOKEN = "gho_SUMMABROWSERACCESSTOKEN0123456789";
export const REFRESH_TOKEN = "ghr_SUMMABROWSERREFRESHTOKEN0123456789";
const cors = { "access-control-allow-origin": "*", "access-control-allow-headers": "content-type", "access-control-allow-methods": "POST" };

export const configuration = (origin) => ({
  environment: "test",
  environmentName: "test",
  location: { owner: "acme", repository: "summa-data", branch: "main", basePath: "deployments/test" },
  identity: { exchange: "https://fides.test", application: "summa-test", provider: "github", clientId: "Iv23liTEST", redirectUri: `${origin}/` },
  organizations: [{ id: "org_acme", displayName: "Acme Consulting", slug: "acme", defaultCurrency: "USD", timeZone: "America/New_York", administrators: ["583231"] }]
});

// The deployment, the exchange and GitHub's authorize page. Returns the
// exchange paths called, in order.
export async function fakeDeployment(page, origin) {
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

// A deployment whose books live on GitHub: the app's server, the
// deployment's fakes, and GitHub's REST API over `files` (an empty data
// repository unless a test says otherwise).
export const signInTest = test.extend({
  files: [{ "README.md": "Summa data\n" }, { option: true }],
  deployment: async ({ page, files }, use) => {
    const server = createAppServer();
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    // Arca reads files that do not exist yet (an empty repository): GitHub
    // answers 404, which the browser logs.
    expectConsoleError(page, /status of 404/);
    const exchanged = await fakeDeployment(page, origin);
    const github = await serveGitHub(page.context(), repository({ owner: "acme", name: "summa-data", files }), origin);
    await use({ page, origin, exchanged, github });
    server.close();
  }
});
