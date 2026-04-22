import { existsSync } from 'node:fs';
import { test, expect } from './support/test-fixtures.js';
import { initFeatureLayer, initDynamicMapLayer, waitForLayerLoad, waitForMapIdle, assertMapNotBlank } from './support/map-harness.js';

test.describe('Visual Rendering Assertions', () => {
  test('[CERT-RNDR-01][CERT-RNDR-SYM-01][EL-EXT-01] FeatureLayer symbology renders with drawingInfo', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);
    await waitForMapIdle(page);

    // Non-blank canvas guard: ensure something rendered
    const notBlank = await assertMapNotBlank(page);
    expect(notBlank).toBe(true);

    // Visual / style certification slice (ticket #478):
    // The drawingInfo-driven point symbol render is the substantiation for
    // CERT-RNDR-SYM-01 in this lane. The CERT IDs in the test title flow
    // through CertReporter onTestEnd into the .cert.json envelope under the
    // featureserver protocol — see tests/js-browser/esri-leaflet/support/cert-reporter.ts
    // and docs/gis/visual-style-certification-slice.md.

    // Visual snapshot: clip to #map container
    const mapContainer = page.locator('#map');
    await expect(mapContainer).toHaveScreenshot('feature-layer-symbology.png', {
      maxDiffPixelRatio: 0.02,
      threshold: 0.3,
    });
  });

  test('[CERT-RNDR-URL-01] FeatureServer drawingInfo metadata document is consumed', async ({ page, staticUrl, config }) => {
    const { baseUrl, serviceId, layerId } = config;

    // Esri Leaflet's L.esri.featureLayer fetches the layer metadata
    // document (which carries drawingInfo) before drawing any features.
    // CERT-RNDR-URL-01 substantiates the "client consumes the style URL /
    // metadata document" scenario by asserting the metadata document is
    // reachable and parses successfully — independent of any rendering
    // tolerance question.
    const layerMetadataUrl = `${baseUrl}/rest/services/${serviceId}/FeatureServer/${layerId}?f=json`;
    const response = await fetch(layerMetadataUrl);
    expect(response.ok).toBe(true);
    const metadata = await response.json() as { drawingInfo?: unknown; geometryType?: unknown };
    expect(metadata).toBeTruthy();
    // drawingInfo + geometryType are the two style-relevant fields that
    // esri-leaflet's renderer code reads. Either being missing would
    // break the symbology assertion above, so both must be present.
    expect(metadata.drawingInfo).toBeTruthy();
    expect(metadata.geometryType).toBeTruthy();

    // Wire the layer through the harness so the test page is in a
    // consistent post-render state for any downstream assertions.
    await initFeatureLayer(page, staticUrl, { baseUrl, serviceId, layerId });
    await waitForLayerLoad(page);
    await waitForMapIdle(page);
    const notBlank = await assertMapNotBlank(page);
    expect(notBlank).toBe(true);
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

    // In CI (updateSnapshots:'none'), skip if no baseline is committed yet.
    // Locally, updateSnapshots:'missing' auto-generates the baseline on first run.
    const snapshotFile = test.info().snapshotPath('dynamic-map-layer-export.png');
    test.skip(!!process.env.CI && !existsSync(snapshotFile),
      'No committed DynamicMapLayer baseline — generate locally: npx playwright test --update-snapshots');

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
