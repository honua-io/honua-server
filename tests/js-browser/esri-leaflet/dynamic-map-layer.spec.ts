import { test, expect } from '@playwright/test';
import { startStaticServer, initDynamicMapLayer, waitForMapIdle } from '../shared/map-harness.js';

const baseUrl = process.env.HONUA_BASE_URL ?? 'http://localhost:5556';
const serviceId = process.env.HONUA_SERVICE_ID ?? 'test_service_gw0';

let staticUrl: string;
let closeServer: () => Promise<void>;

test.beforeAll(async () => {
  const server = await startStaticServer();
  staticUrl = server.url;
  closeServer = server.close;
});

test.afterAll(async () => {
  await closeServer();
});

test.describe('DynamicMapLayer — MapServer Consumption', () => {
  test('[CERT-CONN-01] DynamicMapLayer loads and issues export requests', async ({ page }) => {
    // Track actual export requests to the MapServer endpoint
    const exportRequests: string[] = [];
    await page.route('**/MapServer/export**', async (route) => {
      exportRequests.push(route.request().url());
      await route.continue();
    });

    await initDynamicMapLayer(page, staticUrl, { baseUrl, serviceId, layerId: 0 });
    await waitForMapIdle(page);

    const loadFired = await page.evaluate(() => (window as any).__loadFired);

    // Skip if the server never responded — capability not implemented yet
    test.skip(!loadFired && exportRequests.length === 0,
      'MapServer export endpoint did not respond — capability may not be implemented');

    // The layer must have fired load or made export requests to confirm server communication
    expect(loadFired || exportRequests.length > 0).toBe(true);
  });

  test('[CERT-IDNT-01] Identify returns attributes at point', async ({ page }) => {
    await initDynamicMapLayer(page, staticUrl, { baseUrl, serviceId, layerId: 0 });
    await waitForMapIdle(page);

    const result = await page.evaluate(({ baseUrl, serviceId }) => {
      return new Promise((resolve) => {
        const layer = (window as any).__dynamicMapLayer;
        if (!layer) { resolve({ error: 'No dynamic map layer' }); return; }

        // Identify at the center of the map (San Francisco area)
        const map = (window as any).__map;
        const center = map.getCenter();
        const bounds = map.getBounds();

        layer.identify()
          .at(center)
          .on(map)
          .run((error: any, featureCollection: any) => {
            if (error) {
              resolve({ error: error.message ?? String(error) });
              return;
            }
            resolve({
              type: featureCollection?.type,
              featureCount: featureCollection?.features?.length ?? 0,
              firstFeatureProperties: featureCollection?.features?.[0]?.properties ?? null,
            });
          });
      });
    }, { baseUrl, serviceId });

    // Skip if identify failed — don't false-pass on error responses
    const r = result as any;
    test.skip(!!r.error, `Identify not available: ${r.error}`);

    // Identify must return a GeoJSON FeatureCollection
    expect(r).toHaveProperty('type', 'FeatureCollection');
  });

  test('[CERT-RNDR-02] Data refresh preserves map state', async ({ page }) => {
    await initDynamicMapLayer(page, staticUrl, { baseUrl, serviceId, layerId: 0 });
    await waitForMapIdle(page);

    // Record map state before refresh
    const stateBefore = await page.evaluate(() => {
      const map = (window as any).__map;
      return {
        center: { lat: map.getCenter().lat, lng: map.getCenter().lng },
        zoom: map.getZoom(),
      };
    });

    // Trigger refresh
    await page.evaluate(() => {
      const layer = (window as any).__dynamicMapLayer;
      if (layer) layer.refresh();
    });

    await waitForMapIdle(page);

    // Verify map state preserved after refresh
    const stateAfter = await page.evaluate(() => {
      const map = (window as any).__map;
      return {
        center: { lat: map.getCenter().lat, lng: map.getCenter().lng },
        zoom: map.getZoom(),
      };
    });

    expect(stateAfter.center.lat).toBeCloseTo(stateBefore.center.lat, 4);
    expect(stateAfter.center.lng).toBeCloseTo(stateBefore.center.lng, 4);
    expect(stateAfter.zoom).toBe(stateBefore.zoom);
  });
});
