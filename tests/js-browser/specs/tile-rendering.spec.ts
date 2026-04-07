// MVT tile decode and render pipeline — proves tiles are fetched and canvas
// is not blank after rendering.
// CERT mapping: JS-EXT-01 (PBF/MVT decode), JS-EXT-02 (tile load pipeline).

import { test, expect } from '@playwright/test';
import { createMap } from '../helpers/map-harness.js';
import { BASE_URL, POINT_LAYER_ID, POINT_CENTER } from '../helpers/constants.js';

test.describe('Tile Rendering', () => {
  test('[JS-EXT-01][JS-EXT-02] MVT tiles are fetched and decoded successfully', async ({ page }) => {
    const tileRequests: { url: string; status: number; contentType: string }[] = [];

    // Intercept tile requests to verify they complete with correct content-type.
    await page.route('**/tiles/**/*.mvt', async (route) => {
      const response = await route.fetch();
      tileRequests.push({
        url: route.request().url(),
        status: response.status(),
        contentType: response.headers()['content-type'] ?? '',
      });
      await route.fulfill({ response });
    });

    const styleUrl = `${BASE_URL}/api/styles/${POINT_LAYER_ID}.json`;
    await createMap(page, {
      styleUrl,
      center: POINT_CENTER,
      zoom: 14,
    });

    // Verify at least one tile was requested.
    expect(tileRequests.length).toBeGreaterThan(0);

    // Tiles must be either 200 (with MVT body) or 204 (empty tile — legitimate
    // for tile coordinates that fall outside the layer's data extent). At
    // least one 200 response is required to prove the decode pipeline works.
    let okCount = 0;
    for (const req of tileRequests) {
      expect([200, 204]).toContain(req.status);
      if (req.status === 200) {
        okCount++;
        // MVT tiles should return application/vnd.mapbox-vector-tile or application/x-protobuf.
        expect(
          req.contentType.includes('application/vnd.mapbox-vector-tile') ||
          req.contentType.includes('application/x-protobuf'),
        ).toBe(true);
      }
    }
    expect(okCount).toBeGreaterThan(0);
  });

  test('[JS-EXT-01] canvas is not blank after tile render', async ({ page }) => {
    const styleUrl = `${BASE_URL}/api/styles/${POINT_LAYER_ID}.json`;
    const map = await createMap(page, {
      styleUrl,
      center: POINT_CENTER,
      zoom: 14,
    });

    // The canvas should have non-background pixels (features rendered).
    const pixelCount = await map.countNonBackgroundPixels();
    expect(pixelCount).toBeGreaterThan(0);
  });
});
