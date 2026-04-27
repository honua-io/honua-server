// Cesium UrlTemplateImageryProvider against Honua OGC API Tiles.
// CERT mapping (protocol=ogc-tiles): CERT-CONN-01, CERT-DISC-01, CERT-RNDR-01,
// JS-CES-TILE-01 (URL template parameter substitution correctness).

import { test, expect } from '@playwright/test';
import {
  createViewer,
  observeImageryRequests,
  successfulImageResponses,
} from './support/cesium-harness.js';
import { BASE_URL, POINT_LAYER_ID } from './support/constants.js';

const RENDERED_PIXEL_THRESHOLD = 32;

test.describe('Cesium OGC API Tiles', () => {
  test('[CERT-CONN-01][CERT-DISC-01] OGC API Tiles landing page is reachable', async ({ request }) => {
    const response = await request.get(`${BASE_URL}/ogc/tiles`);
    test.skip(
      response.status() === 404,
      'OGC API Tiles landing page not configured for this server; CONN/DISC cannot be substantiated.',
    );
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body).toBeTruthy();
  });

  test('[CERT-RNDR-01][JS-CES-TILE-01] UrlTemplateImageryProvider substitutes {z}/{x}/{y} correctly', async ({ page, request }) => {
    // The OGC tiles endpoint may not be available for the seed layer in WebMercatorQuad;
    // skip gracefully if so.
    const probe = await request.get(`${BASE_URL}/ogc/tiles/collections/${POINT_LAYER_ID}/tiles/WebMercatorQuad`);
    test.skip(probe.status() === 404, 'OGC API Tiles not configured for browser_compat layer.');

    const observer = await observeImageryRequests(page, '**/ogc/tiles/**');

    try {
      const viewer = await createViewer(page);
      const proxyOrigin = viewer.proxyOrigin;
      await page.evaluate(({ proxyOrigin, layerId }) => {
        const Cesium = (window as any).Cesium;
        const viewer = new Cesium.Viewer('cesiumContainer', {
          baseLayer: false,
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
        viewer.scene.backgroundColor = Cesium.Color.BLACK;
        if (viewer.scene.skyBox) viewer.scene.skyBox.show = false;
        if (viewer.scene.skyAtmosphere) viewer.scene.skyAtmosphere.show = false;
        if (viewer.scene.sun) viewer.scene.sun.show = false;
        if (viewer.scene.moon) viewer.scene.moon.show = false;
        viewer.scene.globe.baseColor = Cesium.Color.BLACK;
        viewer.scene.globe.showGroundAtmosphere = false;
        const provider = new Cesium.UrlTemplateImageryProvider({
          url: `${proxyOrigin}/ogc/tiles/collections/${layerId}/tiles/WebMercatorQuad/{z}/{y}/{x}?f=png`,
          maximumLevel: 5,
        });
        viewer.imageryLayers.addImageryProvider(provider);
        viewer.camera.setView({
          destination: Cesium.Rectangle.fromDegrees(-122.45, 37.74, -122.38, 37.80),
        });
        (window as any).__cesiumViewer = viewer;
      }, { proxyOrigin, layerId: POINT_LAYER_ID });

      await viewer.waitForTilesLoaded();

      // No tile request observed means the provider initialized but Cesium did
      // not request any tiles for the current camera/level. Record as skip so
      // the cert envelope does not falsely report substitution evidence.
      test.skip(
        observer.requests.length === 0,
        'No OGC API Tiles requests captured; cannot substantiate {z}/{x}/{y} substitution.',
      );

      expect(observer.failures).toEqual([]);
      expect(successfulImageResponses(observer).length).toBeGreaterThan(0);
      expect(await viewer.countNonBackgroundPixels({ r: 0, g: 0, b: 0 }))
        .toBeGreaterThan(RENDERED_PIXEL_THRESHOLD);

      for (const request of observer.requests) {
        expect(request.url).not.toContain('{z}');
        expect(request.url).not.toContain('{x}');
        expect(request.url).not.toContain('{y}');
      }
    } finally {
      await observer.dispose();
    }
  });
});
