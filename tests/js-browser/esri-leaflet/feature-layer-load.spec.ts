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

test.describe('FeatureLayer Load and Connection', () => {
  test('[CERT-CONN-01] FeatureLayer connects and fires load event', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);

    const loadFired = await page.evaluate(() => (window as any).__loadFired);
    expect(loadFired).toBe(true);
  });

  test('[CERT-DISC-01][CERT-DISC-02] Metadata discovery via .metadata()', async ({ page }) => {
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
    // CERT-DISC-01: Service info returned
    expect(metadata).toHaveProperty('type');
    // CERT-DISC-02: Layer metadata available
    expect(metadata).toHaveProperty('fields');
    expect(metadata).toHaveProperty('geometryType');
  });

  test('[CERT-SCHM-01] Field schema has name, type, and alias', async ({ page }) => {
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

  test('[CERT-SCHM-02] Geometry type matches expected Esri type', async ({ page }) => {
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

  test('[CERT-ERRH-01] Error on invalid service URL', async ({ page }) => {
    await initFeatureLayer(page, staticUrl, {
      baseUrl,
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
