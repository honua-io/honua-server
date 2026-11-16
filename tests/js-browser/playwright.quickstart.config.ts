import { defineConfig } from '@playwright/test';

// Installation owns secrets and teardown. Never record passwords, cookies or request bodies.
export default defineConfig({
  testDir: './quickstart',
  workers: 1,
  retries: 0,
  timeout: 120_000,
  reporter: [['line']],
  outputDir: '/tmp/honua-quickstart-browser',
  use: {
    baseURL: 'https://localhost:18083',
    ignoreHTTPSErrors: true,
    viewport: { width: 1280, height: 900 },
    trace: 'off', screenshot: 'off', video: 'off',
  },
});
