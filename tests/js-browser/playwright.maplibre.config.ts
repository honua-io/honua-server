import { defineConfig, devices } from '@playwright/test';

const isCI = !!process.env.CI;
const apiKey = process.env.HONUA_API_KEY;
const extraHTTPHeaders = apiKey ? { 'X-API-Key': apiKey } : undefined;

export default defineConfig({
  testDir: './maplibre',
  fullyParallel: false,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: 1,
  reporter: [
    ['list'],
    ['./maplibre/support/cert-reporter.ts'],
  ],
  globalSetup: './maplibre/global-setup.ts',
  use: {
    baseURL: process.env.HONUA_BASE_URL ?? 'http://localhost:5000',
    ...(extraHTTPHeaders ? { extraHTTPHeaders } : {}),
    screenshot: 'only-on-failure',
    trace: 'on-first-retry',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  timeout: 30_000,
  outputDir: './test-results',
});
