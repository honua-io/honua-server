import { test, expect } from '../shared/test-fixtures.js';
import { initFeatureLayer, waitForLayerLoad, getAllFeatures } from '../shared/map-harness.js';

test.describe('FeatureLayer Popup and Field Access', () => {
  test('[CERT-ATTR-01] Feature attributes accessible via eachFeature', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const features = await getAllFeatures(page);

    expect(features.length).toBeGreaterThan(0);

    const firstFeature = features[0];
    expect(firstFeature).toHaveProperty('properties');
    expect(firstFeature).toHaveProperty('geometry');

    // Properties should be an object with at least one key
    expect(typeof firstFeature.properties).toBe('object');
    expect(Object.keys(firstFeature.properties).length).toBeGreaterThan(0);
  });

  test('[CERT-GEOM-01] Coordinate fidelity — coordinates within geographic range', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const features = await getAllFeatures(page);
    expect(features.length).toBeGreaterThan(0);

    // Verify coordinates are in valid geographic ranges
    for (const feature of features) {
      const geom = feature.geometry as any;
      if (!geom || !geom.coordinates) continue;

      const coords = flattenCoordinates(geom.coordinates);
      for (const [lng, lat] of coords) {
        expect(lng).toBeGreaterThanOrEqual(-180);
        expect(lng).toBeLessThanOrEqual(180);
        expect(lat).toBeGreaterThanOrEqual(-90);
        expect(lat).toBeLessThanOrEqual(90);
      }
    }
  });

  test('[CERT-GEOM-02] Output spatial reference matches WGS84 request', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    // Query with explicit outSR=4326 (WGS84) to verify the server honors SR requests
    const result = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }

        layer.query()
          .where('1=1')
          .limit(1)
          .params({ outSR: 4326 })
          .run((error: any, featureCollection: any) => {
            if (error) { reject(error); return; }

            const feature = featureCollection?.features?.[0];
            if (!feature?.geometry?.coordinates) {
              resolve({ valid: false, reason: 'No coordinates' });
              return;
            }

            const coords = feature.geometry.type === 'Point'
              ? [feature.geometry.coordinates]
              : flattenCoordsInBrowser(feature.geometry.coordinates);

            // WGS84 geographic coordinates: longitude [-180,180], latitude [-90,90]
            const allGeographic = coords.every(
              (c: number[]) => c[0] >= -180 && c[0] <= 180 && c[1] >= -90 && c[1] <= 90
            );

            resolve({ valid: allGeographic, coords: coords.slice(0, 3) });
          });

        // Helper function available in browser context
        function flattenCoordsInBrowser(arr: any): number[][] {
          if (typeof arr[0] === 'number') return [arr];
          const result: number[][] = [];
          for (const item of arr) {
            result.push(...flattenCoordsInBrowser(item));
          }
          return result;
        }
      });
    });

    expect((result as any).valid).toBe(true);
  });
});

/** Recursively flatten nested coordinate arrays to [lng, lat] pairs. */
function flattenCoordinates(coords: unknown): number[][] {
  if (!Array.isArray(coords)) return [];
  if (typeof coords[0] === 'number') return [coords as number[]];
  const result: number[][] = [];
  for (const item of coords) {
    result.push(...flattenCoordinates(item));
  }
  return result;
}
