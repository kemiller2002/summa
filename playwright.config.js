// End-to-end configuration for both Summa web pages.
//
// The .NET tests prove the engines decide correctly. They cannot prove the
// engine reaches the browser: the [JSExport] shim, the WASM transport,
// Limen's kernel, the files/transfer packs, the HTML bindings and the Forma
// and Folio presentation sit between the two, and none of them is
// type-checked against the others. These tests drive the real pages, served
// by the real Node servers against throwaway repositories, in a real browser.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

// Some environments ship a Chromium that Playwright did not download itself
// and must not try to. Where that binary exists it is used as-is; everywhere
// else Playwright resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } }]
});
