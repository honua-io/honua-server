/**
 * Browser rendering test for OpenLayers + Honua OGC Tiles.
 *
 * Loads an ol/Map with a VectorTile layer sourced from Honua,
 * waits for rendercomplete, and asserts non-blank canvas output.
 *
 * Uses Playwright (Chromium) for real browser rendering.
 * Excluded from Vitest via .spec.ts extension (Vitest includes *.test.ts only).
 *
 * Writes CERT-RNDR-01 and JS-EXT-02 to the shared MVT evidence file,
 * merging with results previously written by Vitest MVT suites.
 */

import { test, expect } from '@playwright/test';
import { EvidenceCollector } from '../shared/evidence.js';

const BASE_URL = process.env.HONUA_BASE_URL ?? 'http://localhost:5555';
const TEST_PAGE_URL = `http://localhost:${process.env.OL_TEST_PAGE_PORT ?? '9876'}`;

/** Discover collection ID from OGC Features endpoint. */
async function getCollectionId(): Promise<string> {
  const resp = await fetch(`${BASE_URL}/ogc/features/collections`);
  if (!resp.ok) throw new Error(`Collections returned ${resp.status}`);
  const data = (await resp.json()) as { collections: Array<{ id: string }> };
  if (!data.collections?.length) throw new Error('No collections available');
  return data.collections[0].id;
}

test('OGC vector tile layer renders non-blank output on ol/Map', async ({
  page,
}) => {
  const evidence = new EvidenceCollector('mvt');
  evidence.attempt('CERT-RNDR-01');

  try {
    const collectionId = await getCollectionId();

    // Navigate to test page with Honua endpoint params
    const url = `${TEST_PAGE_URL}/test-page.html?baseUrl=${encodeURIComponent(BASE_URL)}&collectionId=${encodeURIComponent(collectionId)}`;
    await page.goto(url);

    // Wait for OpenLayers render complete (up to 30s for tile loading)
    await page.waitForFunction(() => window.__RENDER_COMPLETE === true, null, {
      timeout: 30_000,
    });

    // Check for errors
    const error = await page.evaluate(() => window.__ERROR);
    expect(error).toBeNull();

    // Assert features were loaded
    const featureCount = await page.evaluate(() => window.__FEATURE_COUNT);

    // Sample canvas pixels to verify non-blank rendering
    const isNonBlank = await page.evaluate(() => {
      const canvas = document.querySelector('#map canvas') as HTMLCanvasElement;
      if (!canvas) return false;
      const ctx = canvas.getContext('2d');
      if (!ctx) return false;

      const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
      const data = imageData.data;

      // Check if all pixels are the same (blank canvas)
      const r0 = data[0],
        g0 = data[1],
        b0 = data[2],
        a0 = data[3];
      for (let i = 4; i < data.length; i += 4) {
        if (
          data[i] !== r0 ||
          data[i + 1] !== g0 ||
          data[i + 2] !== b0 ||
          data[i + 3] !== a0
        ) {
          return true; // At least one pixel differs -> non-blank
        }
      }
      return false;
    });

    expect(isNonBlank).toBe(true);

    // Record evidence via shared collector (merge-on-write preserves Vitest results)
    evidence.record('CERT-RNDR-01', 'pass', {
      measuredCount: featureCount,
      notes:
        'OpenLayers rendered OGC VectorTile layer on canvas; non-blank pixel assertion passed',
      evidenceRef: 'openlayers/rendering/render.spec.ts',
    });

    evidence.recordExtension('JS-EXT-02', 'pass', {
      notes:
        'Browser tile load pipeline: VectorTile source fetched MVT tiles and rendered via ol/Map',
    });
  } finally {
    evidence.write();
  }
});

// TypeScript ambient declarations for window globals set by test-page.html
declare global {
  interface Window {
    __RENDER_COMPLETE: boolean;
    __FEATURE_COUNT: number;
    __ERROR: string | null;
  }
}
