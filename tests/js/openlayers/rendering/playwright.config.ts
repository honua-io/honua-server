import { defineConfig } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const ownershipScript = fileURLToPath(new URL('../../../../scripts/ci/owned-server.py', import.meta.url));
const port = execFileSync('python3', [ownershipScript, 'port', '--variable', 'OL_TEST_PAGE_PORT',
  '--ci-default', '9876'], { encoding: 'utf8', timeout: 5000 }).trim();
process.env.OL_TEST_PAGE_PORT = port;
process.env.OL_TEST_PAGE_NONCE = randomUUID();

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 60_000,
  retries: 0,
  workers: 1,
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
    command: 'node node_modules/tsx/dist/cli.mjs openlayers/rendering/serve-test-page.ts',
    url: `http://127.0.0.1:${port}/__honua_test_page/${process.env.OL_TEST_PAGE_NONCE}`,
    cwd: '../../',
    reuseExistingServer: false,
    timeout: 15_000,
  },
});
