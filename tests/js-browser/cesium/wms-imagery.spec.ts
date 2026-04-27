// Cesium WebMapServiceImageryProvider compatibility against Honua WMS 1.3.0.
// CERT mapping (protocol=wms): CERT-CONN-01, CERT-DISC-01, CERT-RNDR-01,
// JS-CES-IMG-01 (WMS request param compliance).

import { test, expect } from '@playwright/test';
import {
  createViewer,
  observeImageryRequests,
  successfulImageResponses,
} from './support/cesium-harness.js';
import { BASE_URL, SERVICE_NAME } from './support/constants.js';

const WMS_URL = `${BASE_URL}/rest/services/${SERVICE_NAME}/MapServer/WMS`;
const RENDERED_PIXEL_THRESHOLD = 32;

test.describe('Cesium WMS imagery', () => {
  test('[CERT-CONN-01][CERT-DISC-01] WMS GetCapabilities is reachable and parseable', async ({ request }) => {
    const response = await request.get(`${WMS_URL}?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0`);
    expect(response.status()).toBe(200);
    const body = await response.text();
    expect(body).toContain('<WMS_Capabilities');
    expect(body).toContain('GetMap');
  });

  test('[CERT-RNDR-01][JS-CES-IMG-01] WebMapServiceImageryProvider issues spec-compliant GetMap requests', async ({ page }) => {
    const observer = await observeImageryRequests(page, '**/MapServer/WMS**');

    try {
      const viewer = await createViewer(page);
      const proxiedWmsUrl = WMS_URL.replace(new URL(WMS_URL).origin, viewer.proxyOrigin);
      await page.evaluate((wmsUrl) => {
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
        const provider = new Cesium.WebMapServiceImageryProvider({
          url: wmsUrl,
          layers: '2000',
          parameters: {
            service: 'WMS',
            version: '1.3.0',
            request: 'GetMap',
            format: 'image/png',
            transparent: true,
          },
        });
        viewer.imageryLayers.addImageryProvider(provider);
        viewer.camera.setView({
          destination: Cesium.Rectangle.fromDegrees(-122.45, 37.74, -122.38, 37.80),
        });
        (window as any).__cesiumViewer = viewer;
      }, proxiedWmsUrl);

      await viewer.waitForTilesLoaded();

      expect(observer.failures).toEqual([]);
      expect(observer.requests.length).toBeGreaterThan(0);
      expect(successfulImageResponses(observer).length).toBeGreaterThan(0);
      const sampleUrl = new URL(observer.requests[0].url);
      const params = new URLSearchParams(sampleUrl.search.toLowerCase());
      expect(params.get('service')).toBe('wms');
      expect(params.get('request')).toBe('getmap');
      expect(params.get('version')).toBe('1.3.0');
      // 1.3.0 uses CRS (not SRS); width/height are mandatory.
      expect(params.get('crs') ?? params.get('srs')).toBeTruthy();
      expect(params.get('width')).toBeTruthy();
      expect(params.get('height')).toBeTruthy();
      expect(params.get('bbox')).toBeTruthy();
      expect(await viewer.countNonBackgroundPixels({ r: 0, g: 0, b: 0 }))
        .toBeGreaterThan(RENDERED_PIXEL_THRESHOLD);
    } finally {
      await observer.dispose();
    }
  });

  test('[CERT-ERRH-01] Invalid WMS layer name returns a structured error', async ({ request }) => {
    const url = `${WMS_URL}?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS=NOT_A_LAYER&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=128&HEIGHT=128&FORMAT=image/png`;
    const response = await request.get(url);
    // WMS 1.3 reports errors as 200 with ServiceException XML or 4xx.
    expect([200, 400, 404]).toContain(response.status());
    const ct = response.headers()['content-type'] ?? '';
    if (response.status() === 200) {
      const body = await response.text();
      expect(body).toMatch(/ServiceException|exception/i);
    } else {
      expect(ct).toMatch(/xml|json/);
    }
  });
});
