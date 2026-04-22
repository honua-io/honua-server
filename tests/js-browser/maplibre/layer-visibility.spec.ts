// Per-geometry-type layer visibility — proves point (circle), line, and
// polygon (fill) layers each render via their default MapLibre styles.
// CERT mapping: CERT-RNDR-01 per geometry type.

import { test, expect } from '@playwright/test';
import { createMap } from './support/map-harness.js';
import {
  BASE_URL,
  POINT_LAYER_ID,
  LINE_LAYER_ID,
  POLYGON_LAYER_ID,
  POINT_CENTER,
  LINE_CENTER,
  POLYGON_CENTER,
} from './support/constants.js';

const GEOMETRY_LAYERS = [
  {
    name: 'Point (circle)',
    layerId: POINT_LAYER_ID,
    expectedMapLibreLayer: `layer-${POINT_LAYER_ID}-circle`,
    expectedType: 'circle',
    center: POINT_CENTER,
  },
  {
    name: 'LineString (line)',
    layerId: LINE_LAYER_ID,
    expectedMapLibreLayer: `layer-${LINE_LAYER_ID}-line`,
    expectedType: 'line',
    center: LINE_CENTER,
  },
  {
    name: 'Polygon (fill)',
    layerId: POLYGON_LAYER_ID,
    expectedMapLibreLayer: `layer-${POLYGON_LAYER_ID}-fill`,
    expectedType: 'fill',
    center: POLYGON_CENTER,
  },
];

test.describe('Layer Visibility', () => {
  for (const geom of GEOMETRY_LAYERS) {
    test(`[CERT-RNDR-01] ${geom.name} layer renders and is visible`, async ({ page }) => {
      const styleUrl = `${BASE_URL}/api/styles/${geom.layerId}.json`;
      const map = await createMap(page, {
        styleUrl,
        center: geom.center,
        zoom: 14,
      });

      // Verify the expected MapLibre layer exists and is visible.
      const visible = await map.isLayerVisible(geom.expectedMapLibreLayer);
      expect(visible).toBe(true);

      // Canvas should have rendered pixels.
      const pixelCount = await map.countNonBackgroundPixels();
      expect(pixelCount).toBeGreaterThan(0);
    });
  }
});
