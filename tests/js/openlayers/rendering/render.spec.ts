/** Real Honua MVT geometry and independent canvas assertions for each style slice. */
import { test, expect } from '@playwright/test';
import MVT from 'ol/format/MVT.js';
import { assertFixtureGeometry, FIXTURE_TILE } from './fixture-geometry-oracle.js';
import { EvidenceCollector } from '../shared/evidence.js';
import { renderingVerdict } from './rendering-oracle.js';

const BASE_URL = process.env.HONUA_BASE_URL ?? 'http://localhost:5555';
const PAGE_URL = `http://localhost:${process.env.OL_TEST_PAGE_PORT ?? '9876'}`;
// These are the discriminating geometries in tests/seed/browser-compat.yaml.
const CASES = [
  { collection: '2000', geometry: 'Point', cert: 'CERT-RNDR-SYM-01', color: [30, 100, 200], minimum: 25 },
  { collection: '2001', geometry: 'LineString', cert: 'CERT-RNDR-LIN-01', color: [200, 30, 80], minimum: 12 },
  { collection: '2002', geometry: 'Polygon', cert: 'CERT-RNDR-FIL-01', color: [40, 180, 90], minimum: 50 },
];

for (const fixture of CASES) {
  test(`Honua ${fixture.geometry} tiles render their own style`, async ({ page }) => {
    const evidence = new EvidenceCollector('mvt');
    for (const id of ['CERT-RNDR-01', 'CERT-RNDR-URL-01', fixture.cert]) evidence.attempt(id);
    try {
      const metadataUrl = `${BASE_URL}/ogc/tiles/collections/${fixture.collection}/tiles/WebMercatorQuad`;
      const metadata = await fetch(metadataUrl, { headers: { Accept: 'application/json' } });
      expect(metadata.ok).toBe(true);
      expect(await metadata.json()).toBeTruthy();
      // Decode without a map extent/projection: canonical OpenLayers returns raw
      // integer tile coordinates (X right, Y down), independently graded below.
      const tile = await fetch(`${metadataUrl}/${FIXTURE_TILE.z}/${FIXTURE_TILE.y}/${FIXTURE_TILE.x}`);
      expect(tile.status).toBe(200);
      const decoded = new MVT().readFeatures(await tile.arrayBuffer());
      assertFixtureGeometry(fixture.collection, decoded.map(feature => ({
        name: feature.get('name'), layer: feature.get('layer'), type: feature.getType(),
        flatCoordinates: feature.getFlatCoordinates(), ends: feature.getEnds(),
      })));

      await page.goto(`${PAGE_URL}/test-page.html?baseUrl=${encodeURIComponent(PAGE_URL)}&collectionId=${fixture.collection}`);
      await page.waitForFunction(() => window.__RENDER_COMPLETE === true, null, { timeout: 30_000 });
      expect(await page.evaluate(() => window.__ERROR)).toBeNull();
      await expect.poll(() => page.evaluate(() => window.__FEATURE_COUNT)).toBeGreaterThan(0);
      const types = await page.evaluate(() => window.__GEOMETRY_TYPES);
      const pixels = async () => page.evaluate((color) => {
        const canvas = document.querySelector('#map canvas') as HTMLCanvasElement | null;
        const context = canvas?.getContext('2d');
        if (!canvas || !context) return 0;
        const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
        let count = 0;
        for (let i = 0; i < data.length; i += 4) {
          if (data[i + 3] >= 128 && color.every((channel, n) => Math.abs(data[i + n] - channel) <= 20)) count++;
        }
        return count;
      }, fixture.color);
      await expect.poll(pixels).toBeGreaterThanOrEqual(fixture.minimum);
      const measured = await pixels();
      const verdict = renderingVerdict(fixture.geometry, types, measured, fixture.minimum);
      expect(verdict).toBe('pass');
      evidence.recordExtension('JS-EXT-02', 'pass', { notes: `OpenLayers fetched and rendered real ${fixture.geometry} MVT features` });
      for (const id of ['CERT-RNDR-01', 'CERT-RNDR-URL-01', fixture.cert]) {
        evidence.record(id, 'pass', {
          measuredCount: measured,
          notes: `${fixture.geometry} decoded from Honua collection ${fixture.collection}; ${measured} independently colored canvas pixels; exact authored names/vertices decoded by OpenLayers`,
          evidenceRef: 'openlayers/rendering/render.spec.ts',
        });
      }
    } catch (error) {
      for (const id of ['CERT-RNDR-01', 'CERT-RNDR-URL-01', fixture.cert]) {
        evidence.record(id, 'fail', { notes: String(error), evidenceRef: 'openlayers/rendering/render.spec.ts' });
      }
      throw error;
    } finally {
      evidence.write();
    }
  });
}

declare global {
  interface Window {
    __RENDER_COMPLETE: boolean;
    __FEATURE_COUNT: number;
    __GEOMETRY_TYPES: string[];
    __ERROR: string | null;
  }
}
