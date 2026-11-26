import { expect, test } from '@playwright/test';
import { createViewer } from './support/cesium-harness.js';
import { POINT_CENTER, POINT_LAYER_ID, SEED_ELEVATION_METERS } from './support/constants.js';

test('[CERT-CONN-01] CesiumTerrainProvider loads and decodes Honua quantized-mesh terrain', async ({ page }) => {
  const { proxyOrigin } = await createViewer(page);
  const terrain = await page.evaluate(async ({ proxyOrigin, coordinates, layerId }) => {
    const Cesium = (window as any).Cesium;
    const provider = await Cesium.CesiumTerrainProvider.fromUrl(`${proxyOrigin}/terrain/${layerId}/`);
    const position = Cesium.Cartographic.fromDegrees(...coordinates);
    const level = 12;
    const tile = provider.tilingScheme.positionToTileXY(position, level);
    const data = await provider.requestTileGeometry(tile.x, tile.y, level);
    const rectangle = provider.tilingScheme.tileXYToRectangle(tile.x, tile.y, level);
    return {
      quantizedMesh: data instanceof Cesium.QuantizedMeshTerrainData,
      height: data?.interpolateHeight(rectangle, position.longitude, position.latitude),
    };
  }, { proxyOrigin, coordinates: POINT_CENTER, layerId: POINT_LAYER_ID });

  expect(terrain.quantizedMesh).toBe(true);
  expect(terrain.height).toBeCloseTo(SEED_ELEVATION_METERS, 1);
});
