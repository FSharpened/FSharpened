// Browser end-to-end and accessibility tests for the built IDE page (ADR-0024).
// Chromium only. The page is served from site/public by a tiny static server started inside the tests
// (support/server.mjs); the gateway is always mocked, so a run never touches the network.
import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "tests",
  outputDir: "../../artifacts/ide-e2e/results",
  timeout: 30_000,
  expect: { timeout: 7_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: process.env.CI ? 2 : 3,
  reporter: process.env.CI ? [["list"]] : [["list"]],
  use: {
    ...devices["Desktop Chrome"],
    viewport: { width: 1280, height: 800 },
    serviceWorkers: "block", // the site's service worker would cache between tests
    acceptDownloads: true,
    trace: "off",
  },
  projects: [{ name: "chromium", use: { browserName: "chromium" } }],
});
