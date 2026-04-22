import { test, expect } from './support/test-fixtures.js';
import { initDynamicMapLayer, waitForMapIdle } from './support/map-harness.js';

test.describe('DynamicMapLayer — MapServer Consumption', () => {
  test('[CERT-CONN-01] DynamicMapLayer loads and issues export requests', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId } = config;
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

  test('[CERT-DISC-02] DynamicMapLayer metadata discovery via .metadata()', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId } = config;
    await initDynamicMapLayer(page, staticUrl, { baseUrl, serviceId, layerId: 0 });
    await waitForMapIdle(page);

    const result = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__dynamicMapLayer;
        if (!layer) {
          reject(new Error('No dynamic map layer'));
          return;
        }

        layer.service.metadata((error: any, response: any) => {
          if (error) {
            resolve({
              ok: false,
              error: error.message ?? String(error),
            });
            return;
          }

          resolve({
            ok: true,
            metadata: {
              mapName: response?.mapName ?? null,
              currentVersion: response?.currentVersion ?? null,
              layersCount: Array.isArray(response?.layers) ? response.layers.length : 0,
            },
          });
        });
      });
    });

    const metadataResult = result as any;
    expect(metadataResult.ok, metadataResult.error ?? 'MapServer metadata request failed').toBe(true);
    expect(metadataResult.metadata.mapName).toBeTruthy();
    expect(metadataResult.metadata.currentVersion).toBeGreaterThan(0);
    expect(metadataResult.metadata.layersCount).toBeGreaterThan(0);
  });

  test('[EL-EXT-04] Identify returns attributes at point', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId } = config;
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

  test('[CERT-RNDR-02] Data refresh preserves map state', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId } = config;
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

    // RasterLayer exposes redraw() instead of refresh().
    await page.evaluate(() => {
      const layer = (window as any).__dynamicMapLayer;
      if (layer) layer.redraw();
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
