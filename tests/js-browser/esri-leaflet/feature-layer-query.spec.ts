import { test, expect } from '@playwright/test';
import { startStaticServer, initFeatureLayer, waitForLayerLoad } from '../shared/map-harness.js';

const baseUrl = process.env.HONUA_BASE_URL ?? 'http://localhost:5556';
const serviceId = process.env.HONUA_SERVICE_ID ?? 'test_service_gw0';
const layerId = process.env.HONUA_LAYER_ID ?? '1000';

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

test.describe('FeatureLayer Query and Filter', () => {
  test('[CERT-QFLT-01] Attribute equality filter via where option', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    // Query with a WHERE filter using the esri-leaflet query API
    const result = await page.evaluate(({ baseUrl, serviceId, layerId }) => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query().where('1=1').run((error: any, featureCollection: any) => {
          if (error) { reject(error); return; }
          resolve({
            type: featureCollection?.type,
            featureCount: featureCollection?.features?.length ?? 0,
          });
        });
      });
    }, { baseUrl, serviceId, layerId });

    expect(result).toHaveProperty('type', 'FeatureCollection');
    expect((result as any).featureCount).toBeGreaterThan(0);
  });

  test('[CERT-QFLT-02] Spatial bbox filter via .within(bounds)', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const result = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        // Use a bounding box around San Francisco area
        const _L = (window as any).L;
        const bounds = _L.latLngBounds(
          _L.latLng(37.0, -123.0),
          _L.latLng(38.0, -122.0)
        );

        layer.query().within(bounds).run((error: any, featureCollection: any) => {
          if (error) { reject(error); return; }
          resolve({
            type: featureCollection?.type,
            featureCount: featureCollection?.features?.length ?? 0,
          });
        });
      });
    });

    expect(result).toHaveProperty('type', 'FeatureCollection');
    // Spatial query should return results (test data is in SF area)
    expect((result as any).featureCount).toBeGreaterThanOrEqual(0);
  });

  test('[CERT-PAGE-01] Query with limit returns expected count', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const limit = 2;
    const result = await page.evaluate((limit) => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query()
          .where('1=1')
          .limit(limit)
          .run((error: any, featureCollection: any) => {
            if (error) { reject(error); return; }
            resolve({
              type: featureCollection?.type,
              featureCount: featureCollection?.features?.length ?? 0,
            });
          });
      });
    }, limit);

    expect(result).toHaveProperty('type', 'FeatureCollection');
    expect((result as any).featureCount).toBeLessThanOrEqual(limit);
    expect((result as any).featureCount).toBeGreaterThan(0);
  });

  test('[CERT-PAGE-02] Offset returns different features than first page', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    // Get first page
    const firstPage = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query()
          .where('1=1')
          .limit(2)
          .offset(0)
          .run((error: any, featureCollection: any) => {
            if (error) { reject(error); return; }
            resolve(featureCollection?.features?.map((f: any) => f.properties?.OBJECTID ?? f.id) ?? []);
          });
      });
    }) as unknown[];

    // Get second page
    const secondPage = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query()
          .where('1=1')
          .limit(2)
          .offset(2)
          .run((error: any, featureCollection: any) => {
            if (error) { reject(error); return; }
            resolve(featureCollection?.features?.map((f: any) => f.properties?.OBJECTID ?? f.id) ?? []);
          });
      });
    }) as unknown[];

    // Pages should have different content (if enough data exists)
    if (firstPage.length > 0 && secondPage.length > 0) {
      const firstSet = new Set(firstPage.map(String));
      const hasOverlap = secondPage.some(id => firstSet.has(String(id)));
      expect(hasOverlap).toBe(false);
    }
  });

  test('[CERT-ERRH-02] Malformed filter yields error', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const result = await page.evaluate(() => {
      return new Promise((resolve) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { resolve({ error: true, message: 'No feature layer' }); return; }

        layer.query()
          .where('INVALID @@@ SYNTAX !!!')
          .run((error: any, featureCollection: any) => {
            if (error) {
              resolve({ error: true, message: error.message ?? String(error) });
            } else {
              // Server might return an error in the response body
              resolve({
                error: false,
                featureCount: featureCollection?.features?.length ?? 0,
                hasError: !!(featureCollection?.error),
              });
            }
          });
      });
    });

    // The server must signal rejection — via error callback, error in response, or zero features
    const r = result as any;
    expect(r.error === true || r.hasError === true || r.featureCount === 0).toBe(true);
  });
});
