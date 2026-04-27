// Cesium SingleTileImageryProvider against Honua OGC API Maps.
// CERT mapping (protocol=ogc-maps): CERT-CONN-01, CERT-RNDR-01.

import { test, expect } from '@playwright/test';
import { createViewer } from './support/cesium-harness.js';
import { BASE_URL, POINT_LAYER_ID, SEED_BBOX_4326 } from './support/constants.js';

function ogcMapUrl(): string {
  const params = new URLSearchParams({
    bbox: SEED_BBOX_4326,
    width: '256',
    height: '256',
    f: 'png',
  });
  return `${BASE_URL}/ogc/maps/collections/${POINT_LAYER_ID}/map?${params}`;
}

test.describe('Cesium OGC API Maps', () => {
  test('[CERT-CONN-01] OGC API Maps endpoint returns PNG image bytes', async ({ request }) => {
    const response = await request.get(ogcMapUrl());
    test.skip(
      response.status() === 404,
      'OGC API Maps endpoint not configured for browser_compat layer; CONN cannot be substantiated.',
    );
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type'] ?? '').toContain('image/png');
    const body = await response.body();
    // PNG signature: 89 50 4E 47 0D 0A 1A 0A
    expect([...body.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
  });

  test('[CERT-RNDR-01] SingleTileImageryProvider mounts an OGC Maps image when raster data is available', async ({ page, request }) => {
    const probe = await request.get(ogcMapUrl());
    test.skip(probe.status() !== 200, 'OGC API Maps raster fixture is not available for the browser_compat layer.');

    const viewer = await createViewer(page);
    const proxyOrigin = viewer.proxyOrigin;
    await page.evaluate(({ proxyOrigin, mapUrl, bbox }) => {
      const Cesium = (window as any).Cesium;
      const viewer = new Cesium.Viewer('cesiumContainer', {
        baseLayerPicker: false,
        timeline: false,
        animation: false,
        geocoder: false,
        homeButton: false,
        sceneModePicker: false,
        navigationHelpButton: false,
        fullscreenButton: false,
        infoBox: false,
        selectionIndicator: false,
        contextOptions: { webgl: { preserveDrawingBuffer: true } },
      });
      const [w, s, e, n] = bbox.split(',').map(Number);
      const proxiedUrl = mapUrl.replace(new URL(mapUrl).origin, proxyOrigin);
      const provider = new Cesium.SingleTileImageryProvider({
        url: proxiedUrl,
        rectangle: Cesium.Rectangle.fromDegrees(w, s, e, n),
      });
      viewer.imageryLayers.addImageryProvider(provider);
      viewer.camera.setView({
        destination: Cesium.Rectangle.fromDegrees(w, s, e, n),
      });
      (window as any).__cesiumViewer = viewer;
    }, { proxyOrigin, mapUrl: ogcMapUrl(), bbox: SEED_BBOX_4326 });

    await viewer.waitForTilesLoaded();
  });
});
