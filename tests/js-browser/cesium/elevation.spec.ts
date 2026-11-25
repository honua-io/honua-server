import { expect, test } from '@playwright/test';
import { createViewer } from './support/cesium-harness.js';
import { POINT_CENTER, POINT_LAYER_ID, SEED_ELEVATION_METERS } from './support/constants.js';

test('[CERT-QFLT-01] Cesium Resource queries the seeded elevation at a geographic point', async ({ page }) => {
  const { proxyOrigin } = await createViewer(page);
  const elevation = await page.evaluate(async ({ proxyOrigin, coordinates, layerId }) => {
    const Cesium = (window as any).Cesium;
    const [longitude, latitude] = coordinates;
    const resource = new Cesium.Resource({
      url: `${proxyOrigin}/elevation/${layerId}/value`,
      queryParameters: { x: longitude, y: latitude, srid: 4326 },
    });
    return await resource.fetchJson();
  }, { proxyOrigin, coordinates: POINT_CENTER, layerId: POINT_LAYER_ID });

  expect(elevation).toMatchObject({
    layerId: POINT_LAYER_ID,
    elevation: SEED_ELEVATION_METERS,
    noData: false,
    outOfBounds: false,
  });
});
