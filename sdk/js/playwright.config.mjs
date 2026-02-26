import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./test/playwright",
  timeout: 30_000,
  expect: {
    timeout: 10_000,
  },
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: process.env.CI ? "dot" : "list",
  use: {
    headless: true,
  },
});
