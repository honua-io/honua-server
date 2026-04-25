// Style JSON and TileJSON discovery — proves the style-to-map init path.
// CERT mapping: CERT-CONN-01 (HTTP connection), CERT-RNDR-01 (style load + idle).

import { test, expect } from '@playwright/test';
import { validateStyleMin } from '@maplibre/maplibre-gl-style-spec';
import { createMap } from './support/map-harness.js';
import {
  BASE_URL,
  LINE_LAYER_ID,
  POINT_LAYER_ID,
  POINT_CENTER,
  POLYGON_LAYER_ID,
} from './support/constants.js';

const STYLE_LAYERS = [
  { name: 'point', layerId: POINT_LAYER_ID },
  { name: 'line', layerId: LINE_LAYER_ID },
  { name: 'polygon', layerId: POLYGON_LAYER_ID },
];

test.describe('Style Loading', () => {
  for (const layer of STYLE_LAYERS) {
    test(`[CERT-CONN-01] ${layer.name} style JSON is valid MapLibre v8`, async ({ request }) => {
      const response = await request.get(`${BASE_URL}/api/styles/${layer.layerId}.json`);
      expect(response.status()).toBe(200);

      const style = await response.json();
      expect(style.version).toBe(8);
      expect(style.sources).toBeDefined();
      expect(style.layers).toBeDefined();
      expect(Array.isArray(style.layers)).toBe(true);
      expect(style.layers.length).toBeGreaterThan(0);

      const validationErrors = validateStyleMin(style);
      expect(validationErrors.map((error) => error.message)).toEqual([]);
    });
  }

  test('[CERT-CONN-01] fetch TileJSON returns valid metadata with style URL', async ({ request }) => {
    const response = await request.get(`${BASE_URL}/tiles/${POINT_LAYER_ID}/tile.json`);
    expect(response.status()).toBe(200);

    const tileJson = await response.json();
    expect(tileJson.tilejson).toBe('3.0.0');
    expect(tileJson.tiles).toBeDefined();
    expect(Array.isArray(tileJson.tiles)).toBe(true);
    expect(tileJson.tiles.length).toBeGreaterThan(0);
    expect(tileJson.tiles[0]).toContain('.mvt');

    // TileJSON includes a style URL.
    expect(tileJson.style).toBeDefined();
    expect(tileJson.style).toContain(`/api/styles/${POINT_LAYER_ID}.json`);

    // Vector layers metadata is present.
    expect(tileJson.vector_layers).toBeDefined();
    expect(tileJson.vector_layers.length).toBeGreaterThan(0);
    expect(tileJson.vector_layers[0].id).toBe('layer');
  });

  test('[CERT-RNDR-01] MapLibre map initializes from style URL and reaches idle', async ({ page }) => {
    const styleUrl = `${BASE_URL}/api/styles/${POINT_LAYER_ID}.json`;
    const map = await createMap(page, {
      styleUrl,
      center: POINT_CENTER,
      zoom: 14,
    });

    // If we got here, the map reached idle without error.
    // Verify the map canvas exists and has dimensions.
    const canvasBox = await page.locator('#map canvas').boundingBox();
    expect(canvasBox).not.toBeNull();
    expect(canvasBox!.width).toBeGreaterThan(0);
    expect(canvasBox!.height).toBeGreaterThan(0);
  });
});
