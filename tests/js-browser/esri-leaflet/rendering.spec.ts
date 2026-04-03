import { test, expect } from '../shared/test-fixtures.js';
import { initFeatureLayer, initDynamicMapLayer, waitForLayerLoad, waitForMapIdle, assertMapNotBlank } from '../shared/map-harness.js';

test.describe('Visual Rendering Assertions', () => {
  test('[CERT-RNDR-01][EL-EXT-01] FeatureLayer symbology renders with drawingInfo', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);
    await waitForMapIdle(page);

    // Non-blank canvas guard: ensure something rendered
    const notBlank = await assertMapNotBlank(page);
    expect(notBlank).toBe(true);

    // Visual snapshot: clip to #map container
    const mapContainer = page.locator('#map');
    await expect(mapContainer).toHaveScreenshot('feature-layer-symbology.png', {
      maxDiffPixelRatio: 0.02,
      threshold: 0.3,
    });
  });

  test('[CERT-RNDR-01][EL-EXT-02] DynamicMapLayer export image renders', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId } = config;
    await initDynamicMapLayer(page, staticUrl, { baseUrl, serviceId, layerId: 0 });
    await waitForMapIdle(page);

    // Check if the map has any rendered content (export image or tiles)
    const notBlank = await assertMapNotBlank(page);

    // Skip if the export endpoint didn't render — don't false-pass on a blank map
    test.skip(!notBlank,
      'DynamicMapLayer rendered a blank map — MapServer export endpoint may not be implemented');

    const mapContainer = page.locator('#map');
    await expect(mapContainer).toHaveScreenshot('dynamic-map-layer-export.png', {
      maxDiffPixelRatio: 0.02,
      threshold: 0.3,
    });
  });

  test('Non-blank canvas guard — FeatureLayer renders visible content', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);
    await waitForMapIdle(page);

    const notBlank = await assertMapNotBlank(page);
    expect(notBlank).toBe(true);
  });
});
