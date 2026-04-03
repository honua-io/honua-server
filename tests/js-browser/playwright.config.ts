import { defineConfig, devices } from '@playwright/test';

const isCI = !!process.env.CI;

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: 1,
  reporter: [
    ['list'],
    ['html', { outputFolder: './playwright-report', open: 'never' }],
    ['./helpers/cert-reporter.ts'],
  ],
  globalSetup: './global-setup.ts',
  use: {
    baseURL: process.env.HONUA_BASE_URL ?? 'http://localhost:5000',
    screenshot: 'only-on-failure',
    trace: 'on-first-retry',
    ...devices['Desktop Chrome'],
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
