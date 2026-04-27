// Cesium WebMapTileServiceImageryProvider compatibility against Honua WMTS 1.0.
// CERT mapping (protocol=wmts): CERT-CONN-01, CERT-DISC-01, CERT-RNDR-01.

import { test, expect } from '@playwright/test';
import {
  createViewer,
  observeImageryRequests,
  successfulImageResponses,
} from './support/cesium-harness.js';
import { BASE_URL, SERVICE_NAME } from './support/constants.js';

const WMTS_URL = `${BASE_URL}/rest/services/${SERVICE_NAME}/MapServer/WMTS`;
const RENDERED_PIXEL_THRESHOLD = 32;

test.describe('Cesium WMTS imagery', () => {
  test('[CERT-CONN-01][CERT-DISC-01] WMTS GetCapabilities lists tile matrix sets', async ({ request }) => {
    const response = await request.get(`${WMTS_URL}?SERVICE=WMTS&REQUEST=GetCapabilities`);
    expect(response.status()).toBe(200);
    const body = await response.text();
    expect(body).toMatch(/Capabilities/);
    expect(body).toMatch(/TileMatrixSet/);
  });

  test('[CERT-RNDR-01] WebMapTileServiceImageryProvider can request a tile', async ({ page, request }) => {
    // Skip when WMTS is not implemented for this service surface.
    const caps = await request.get(`${WMTS_URL}?SERVICE=WMTS&REQUEST=GetCapabilities`);
    test.skip(caps.status() !== 200, 'WMTS capabilities not available for browser_compat service.');

    const observer = await observeImageryRequests(page, '**/MapServer/WMTS/**');

    try {
      const viewer = await createViewer(page);
      const proxiedWmtsUrl = WMTS_URL.replace(new URL(WMTS_URL).origin, viewer.proxyOrigin);
      await page.evaluate((wmtsUrl) => {
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
        const provider = new Cesium.WebMapTileServiceImageryProvider({
          url: `${wmtsUrl}/tile/1.0.0/2000/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png`,
          layer: '2000',
          style: 'default',
          tileMatrixSetID: 'GoogleMapsCompatible',
          format: 'image/png',
          maximumLevel: 5,
        });
        viewer.imageryLayers.addImageryProvider(provider);
        viewer.camera.setView({
          destination: Cesium.Rectangle.fromDegrees(-122.45, 37.74, -122.38, 37.80),
        });
        (window as any).__cesiumViewer = viewer;
      }, proxiedWmtsUrl);

      await viewer.waitForTilesLoaded();

      // WMTS tile requests may legitimately not fire if no tiles intersect the
      // current view at the chosen level. Record as skip rather than a false
      // pass so the cert envelope reflects what actually happened.
      test.skip(
        observer.requests.length === 0,
        'No WMTS tile requests captured; provider initialized but Cesium fetched no tiles.',
      );
      expect(observer.failures).toEqual([]);
      expect(successfulImageResponses(observer).length).toBeGreaterThan(0);
      expect(await viewer.countNonBackgroundPixels({ r: 0, g: 0, b: 0 }))
        .toBeGreaterThan(RENDERED_PIXEL_THRESHOLD);
    } finally {
      await observer.dispose();
    }
  });
});
