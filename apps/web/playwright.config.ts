import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end checks against a running stack (`docker compose up --build`, http://localhost:8080) or
 * another URL in MARKETLENS_WEB_URL. They use real market data, so they assert on shapes and
 * behaviour, never on exact prices.
 */
export default defineConfig({
  testDir: "e2e",
  timeout: 30_000,
  expect: { timeout: 15_000 },
  fullyParallel: true,
  reporter: [["list"]],
  use: {
    baseURL: process.env.MARKETLENS_WEB_URL ?? "http://localhost:8080",
    locale: "en-US",
    trace: "retain-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
