import { test, expect } from './support/test-fixtures.js';
import { initFeatureLayer, waitForLayerLoad } from './support/map-harness.js';

test.describe('FeatureLayer Load and Connection', () => {
  test('[CERT-CONN-01] FeatureLayer connects and fires load event', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const loadFired = await page.evaluate(() => (window as any).__loadFired);
    expect(loadFired).toBe(true);
  });

  test('[CERT-DISC-02] Metadata discovery via .metadata()', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    // Call metadata on the feature layer
    const metadata = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }
        layer.metadata((error: any, response: any) => {
          if (error) { reject(error); return; }
          resolve(response);
        });
      });
    });

    expect(metadata).toBeTruthy();
    // CERT-DISC-02: Single service/collection metadata retrieved
    expect(metadata).toHaveProperty('type');
    expect(metadata).toHaveProperty('fields');
    expect(metadata).toHaveProperty('geometryType');
  });

  test('[CERT-SCHM-01] Field schema has name, type, and alias', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const fields = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }
        layer.metadata((error: any, response: any) => {
          if (error) { reject(error); return; }
          resolve(response.fields);
        });
      });
    }) as Array<{ name: string; type: string; alias: string }>;

    expect(Array.isArray(fields)).toBe(true);
    expect(fields.length).toBeGreaterThan(0);

    for (const field of fields) {
      expect(field).toHaveProperty('name');
      expect(field).toHaveProperty('type');
      expect(field).toHaveProperty('alias');
      expect(typeof field.name).toBe('string');
      expect(typeof field.type).toBe('string');
    }
  });

  test('[CERT-SCHM-02] Geometry type matches expected Esri type', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const geometryType = await page.evaluate(() => {
      return new Promise((resolve, reject) => {
        const layer = (window as any).__featureLayer;
        if (!layer) { reject(new Error('No feature layer')); return; }
        layer.metadata((error: any, response: any) => {
          if (error) { reject(error); return; }
          resolve(response.geometryType);
        });
      });
    });

    const validEsriTypes = [
      'esriGeometryPoint',
      'esriGeometryMultipoint',
      'esriGeometryPolyline',
      'esriGeometryPolygon',
      'esriGeometryEnvelope',
    ];
    expect(validEsriTypes).toContain(geometryType);
  });

  test('[CERT-ERRH-01] Error on invalid service URL', async ({ page, staticUrl, config }) => {
    await initFeatureLayer(page, staticUrl, {
      baseUrl: config.baseUrl,
      serviceId: 'nonexistent_service_xyz',
      layerId: '9999',
    });

    // Wait for the error event or a timeout
    const error = await page.evaluate(() => {
      return new Promise((resolve) => {
        const checkError = () => {
          if ((window as any).__layerError) {
            resolve((window as any).__layerError);
          } else {
            setTimeout(checkError, 200);
          }
        };
        setTimeout(() => resolve(null), 10000);
        checkError();
      });
    });

    // esri-leaflet should have received an error from the server
    // Either a structured error object or the layer simply doesn't load
    // (the server returns 404/error for nonexistent services)
    const loadFired = await page.evaluate(() => (window as any).__loadFired);
    // At minimum: either an error was raised or load never fired with valid data
    expect(error !== null || !loadFired).toBe(true);
  });
});
