// Per-geometry-type layer visibility — proves point (circle), line, and
// polygon (fill) layers each render via their default MapLibre styles.
// CERT mapping: CERT-RNDR-01 per geometry type.

import { test, expect } from '@playwright/test';
import { createMap } from '../helpers/map-harness.js';

const BASE_URL = process.env.HONUA_BASE_URL ?? 'http://localhost:5000';

const GEOMETRY_LAYERS = [
  {
    name: 'Point (circle)',
    layerId: 2000,
    expectedMapLibreLayer: 'layer-2000-circle',
    expectedType: 'circle',
    center: [-122.4194, 37.7749] as [number, number],
  },
  {
    name: 'LineString (line)',
    layerId: 2001,
    expectedMapLibreLayer: 'layer-2001-line',
    expectedType: 'line',
    center: [-122.4200, 37.7750] as [number, number],
  },
  {
    name: 'Polygon (fill)',
    layerId: 2002,
    expectedMapLibreLayer: 'layer-2002-fill',
    expectedType: 'fill',
    center: [-122.4200, 37.7750] as [number, number],
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
