import { test as base } from '@playwright/test';
import { startStaticServer } from './map-harness.js';

/** Shared test configuration derived from environment variables. */
export interface TestConfig {
  baseUrl: string;
  serviceId: string;
  layerId: string;
}

/** Extend Playwright's base test with worker-scoped shared fixtures. */
export const test = base.extend<object, { staticUrl: string; config: TestConfig }>({
  // eslint-disable-next-line no-empty-pattern
  staticUrl: [async ({}, use) => {
    const server = await startStaticServer();
    await use(server.url);
    await server.close();
  }, { scope: 'worker' }],

  // eslint-disable-next-line no-empty-pattern
  config: [async ({}, use) => {
    await use({
      baseUrl: process.env.HONUA_BASE_URL ?? 'http://localhost:5556',
      serviceId: process.env.HONUA_SERVICE_ID ?? 'test_service_gw0',
      layerId: process.env.HONUA_LAYER_ID ?? '1000',
    });
  }, { scope: 'worker' }],
});

export { expect } from '@playwright/test';
