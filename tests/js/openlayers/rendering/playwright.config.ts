import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 60_000,
  retries: 1,
  globalSetup: './global-setup.ts',
  use: {
    headless: true,
    viewport: { width: 512, height: 512 },
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
  webServer: {
    command: 'npx tsx openlayers/rendering/serve-test-page.ts',
    port: 9876,
    cwd: '../../',
    reuseExistingServer: true,
    timeout: 15_000,
  },
});
