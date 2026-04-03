// Interactive feature inspection via queryRenderedFeatures — proves that
// features are queryable at known coordinates after render.
// CERT mapping: CERT-RNDR-01 (interactive inspection path).

import { test, expect } from '@playwright/test';
import { createMap } from '../helpers/map-harness.js';
import { BASE_URL, POINT_LAYER_ID, POINT_CENTER, POLYGON_LAYER_ID, POLYGON_CENTER } from '../helpers/constants.js';

test.describe('Feature Query', () => {
  test('[CERT-RNDR-01] queryRenderedFeatures returns features at known point', async ({ page }) => {
    const styleUrl = `${BASE_URL}/api/styles/${POINT_LAYER_ID}.json`;
    const map = await createMap(page, {
      styleUrl,
      center: POINT_CENTER,
      zoom: 16,
    });

    // Query the center of the 512x512 canvas where a seeded point should be.
    const features = await map.queryRenderedFeatures(
      { x: 256, y: 256 },
      [`layer-${POINT_LAYER_ID}-circle`],
    );

    expect(features.length).toBeGreaterThan(0);

    const feature = features[0];
    // Verify feature has expected source-layer.
    expect(feature.sourceLayer).toBe('layer');
    // Verify feature has properties from the seed data.
    expect(feature.properties).toBeDefined();
    expect(feature.properties.name).toBeDefined();
    expect(typeof feature.properties.name).toBe('string');
  });

  test('[CERT-RNDR-01] queryRenderedFeatures returns polygon features', async ({ page }) => {
    const styleUrl = `${BASE_URL}/api/styles/${POLYGON_LAYER_ID}.json`;
    const map = await createMap(page, {
      styleUrl,
      // Center on the seeded polygon's interior.
      center: POLYGON_CENTER,
      zoom: 16,
    });

    const features = await map.queryRenderedFeatures(
      { x: 256, y: 256 },
      [`layer-${POLYGON_LAYER_ID}-fill`],
    );

    expect(features.length).toBeGreaterThan(0);
    expect(features[0].sourceLayer).toBe('layer');
    expect(features[0].properties.name).toBeDefined();
  });
});
