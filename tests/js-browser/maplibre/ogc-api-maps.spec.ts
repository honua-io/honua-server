// OGC API Maps image source compatibility for MapLibre GL JS.

import { test, expect } from '@playwright/test';
import { createMap } from './support/map-harness.js';
import { BASE_URL, POINT_CENTER, POINT_LAYER_ID } from './support/constants.js';

const MAP_BBOX = '-122.44,37.76,-122.40,37.79';
const IMAGE_COORDINATES: [number, number][] = [
  [-122.44, 37.79],
  [-122.40, 37.79],
  [-122.40, 37.76],
  [-122.44, 37.76],
];

function ogcMapUrl(): string {
  const params = new URLSearchParams({
    bbox: MAP_BBOX,
    width: '256',
    height: '256',
    f: 'png',
  });
  return `${BASE_URL}/ogc/maps/collections/${POINT_LAYER_ID}/map?${params}`;
}

test.describe('OGC API Maps Image Source', () => {
  test('collection map endpoint returns image bytes or a no-raster problem response', async ({ request }) => {
    const response = await request.get(ogcMapUrl());
    expect([200, 404]).toContain(response.status());

    if (response.status() === 200) {
      expect(response.headers()['content-type'] ?? '').toContain('image/png');
      const body = await response.body();
      expect([...body.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
      return;
    }

    expect(response.headers()['content-type'] ?? '').toContain('application/json');
    const body = await response.json();
    expect(body.status ?? body.error?.code).toBe(404);
  });

  test('MapLibre can mount /ogc/maps output as an image source when raster data exists', async ({ page, request }) => {
    const response = await request.get(ogcMapUrl());
    test.skip(response.status() === 404, 'OGC API Maps raster fixture is not available for the browser seed layer.');
    expect(response.status()).toBe(200);

    const map = await createMap(page, {
      styleUrl: `${BASE_URL}/api/styles/${POINT_LAYER_ID}.json`,
      center: POINT_CENTER,
      zoom: 12,
    });

    const beforePixels = await map.countNonBackgroundPixels();
    await page.evaluate(
      ({ coordinates, url }) =>
        new Promise<void>((resolve, reject) => {
          const maplibreMap = (window as any).__map;
          if (!maplibreMap) {
            reject(new Error('MapLibre map not initialized'));
            return;
          }

          const timeoutId = setTimeout(() => reject(new Error('OGC API Maps image source idle timeout')), 15_000);
          maplibreMap.once('idle', () => {
            clearTimeout(timeoutId);
            resolve();
          });
          maplibreMap.addSource('ogc-api-map', {
            type: 'image',
            url,
            coordinates,
          });
          maplibreMap.addLayer({
            id: 'ogc-api-map',
            type: 'raster',
            source: 'ogc-api-map',
          });
        }),
      { coordinates: IMAGE_COORDINATES, url: ogcMapUrl() },
    );

    const afterPixels = await map.countNonBackgroundPixels();
    expect(afterPixels).toBeGreaterThanOrEqual(beforePixels);
  });
});
