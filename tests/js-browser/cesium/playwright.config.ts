import { defineConfig, devices } from '@playwright/test';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const isCI = !!process.env.CI;

export default defineConfig({
  testDir: '.',
  fullyParallel: false,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: 1,
  reporter: [
    ['list'],
    [resolve(__dirname, 'support', 'cert-reporter.ts')],
  ],
  globalSetup: resolve(__dirname, 'global-setup.ts'),
  use: {
    baseURL: process.env.HONUA_BASE_URL ?? 'http://localhost:5000',
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
  // Cesium boot + globe initialization can be slow under CI emulators.
  timeout: 60_000,
  outputDir: resolve(__dirname, 'test-output'),
});
