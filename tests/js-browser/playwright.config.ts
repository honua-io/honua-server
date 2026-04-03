import { defineConfig, devices } from '@playwright/test';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const baseURL = process.env.HONUA_BASE_URL ?? 'http://localhost:5556';

export default defineConfig({
  testDir: './esri-leaflet',
  fullyParallel: false,
  workers: 1,
  retries: 1,
  reporter: [
    ['list'],
    ['./shared/cert-reporter.ts'],
  ],
  globalSetup: resolve(__dirname, 'global-setup.ts'),
  use: {
    baseURL,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  expect: {
    toHaveScreenshot: {
      maxDiffPixelRatio: 0.02,
      threshold: 0.3,
    },
  },
  outputDir: './test-results',
});
