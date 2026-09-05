import { test, expect } from './support/test-fixtures.js';
import {
  initDynamicMapLayer,
  initFeatureLayer,
  waitForLayerLoad,
  waitForMapIdle,
} from './support/map-harness.js';

test.describe('Esri client bug-hunt probe', () => {
  test('esri-leaflet FeatureLayer query returns the seeded Oahu features', async ({ page, staticUrl, config }) => {
    await initFeatureLayer(page, staticUrl, {
      baseUrl: config.baseUrl,
      serviceId: 'admin_sample',
      layerId: 3000,
      mapOptions: { center: [21.3069, -157.8583], zoom: 12 },
    });
    await waitForLayerLoad(page);

    const result = await page.evaluate(() => new Promise<any>((resolve, reject) => {
      (window as any).__featureLayer.query().where('1=1').run((error: any, collection: any) => {
        if (error) {
          reject(error);
          return;
        }
        resolve({
          type: collection?.type,
          count: collection?.features?.length ?? 0,
          names: (collection?.features ?? []).map((feature: any) => feature.properties?.name).sort(),
        });
      });
    }));

    expect(result.type).toBe('FeatureCollection');
    expect(result.count).toBe(4);
    expect(result.names).toContain('Honolulu Operations Center');
  });

  test('esri-leaflet DynamicMapLayer identify returns a FeatureCollection', async ({ page, staticUrl, config }) => {
    await initDynamicMapLayer(page, staticUrl, {
      baseUrl: config.baseUrl,
      serviceId: 'admin_sample',
      layerId: 0,
      mapOptions: { center: [21.3069, -157.8583], zoom: 12 },
    });
    await waitForMapIdle(page);

    const result = await page.evaluate(() => new Promise<any>((resolve) => {
      const layer = (window as any).__dynamicMapLayer;
      const map = (window as any).__map;
      layer.identify().at(map.getCenter()).on(map).run((error: any, collection: any) => {
        resolve({
          error: error?.message ?? null,
          type: collection?.type ?? null,
          count: collection?.features?.length ?? 0,
        });
      });
    }));

    expect(result.error).toBeNull();
    expect(result.type).toBe('FeatureCollection');
    expect(result.count).toBeGreaterThan(0);
  });
});
