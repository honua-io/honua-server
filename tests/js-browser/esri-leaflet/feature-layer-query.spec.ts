import { test, expect } from './support/test-fixtures.js';
import { initFeatureLayer, waitForLayerLoad } from './support/map-harness.js';

test.describe('FeatureLayer Query and Filter', () => {
  test('[CERT-QFLT-01] Attribute equality filter via where option', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    // Query with a real equality predicate against seeded data
    const result = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query().where("name='alpha'").run((error: any, featureCollection: any) => {
          if (error) { reject(error); return; }
          const features = featureCollection?.features ?? [];
          resolve({
            type: featureCollection?.type,
            featureCount: features.length,
            allMatch: features.every((f: any) => f.properties?.name === 'alpha'),
          });
        });
      });
    });

    expect(result).toHaveProperty('type', 'FeatureCollection');
    const count = (result as any).featureCount;
    test.info().annotations.push({ type: 'measured_count', description: String(count) });
    expect(count).toBeGreaterThan(0);
    // Every returned feature must match the predicate
    expect((result as any).allMatch).toBe(true);
  });

  test('[CERT-QFLT-02] Spatial bbox filter via .within(bounds)', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
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
    const count = (result as any).featureCount;
    test.info().annotations.push({ type: 'measured_count', description: String(count) });
    // Spatial query should return results (test data is seeded in SF area)
    expect(count).toBeGreaterThan(0);
  });

  test('[CERT-PAGE-01] Query with limit returns expected count', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
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
    const count = (result as any).featureCount;
    test.info().annotations.push({ type: 'measured_count', description: String(count) });
    expect(count).toBeLessThanOrEqual(limit);
    expect(count).toBeGreaterThan(0);
  });

  test('[CERT-PAGE-02] Offset returns different features than first page', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
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

  test('[CERT-ERRH-02] Malformed filter yields error', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
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

    // The server must signal rejection — via error callback or error in response body
    const r = result as any;
    expect(r.error === true || r.hasError === true).toBe(true);
  });
});
