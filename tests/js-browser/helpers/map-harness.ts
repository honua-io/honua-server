// Shared helper for creating and managing a MapLibre GL JS map inside a
// Playwright browser page. Injects maplibre-gl via CDN, creates the map,
// and waits for the `idle` event (all tiles rendered).

import type { Page } from '@playwright/test';
import { resolve } from 'node:path';
import { readFileSync } from 'node:fs';

// Resolve the local maplibre-gl distribution for injection.
const maplibreDistDir = resolve(import.meta.dirname, '..', 'node_modules', 'maplibre-gl', 'dist');

/** Options for creating a map. */
export interface MapOptions {
  /** Full URL to the MapLibre style JSON. */
  styleUrl: string;
  /** Map center [lng, lat]. Defaults to [-122.42, 37.77]. */
  center?: [number, number];
  /** Initial zoom level. Defaults to 14. */
  zoom?: number;
  /** Timeout (ms) waiting for the map `idle` event. Defaults to 25000. */
  idleTimeout?: number;
}

/** Serialized feature returned by queryRenderedFeatures. */
export interface RenderedFeature {
  type: 'Feature';
  geometry: Record<string, unknown>;
  properties: Record<string, unknown>;
  layer: { id: string; type: string };
  sourceLayer: string;
}

/** Return type from `createMap`. Provides helpers for assertions. */
export interface MapHandle {
  /** Wait for a subsequent idle event (e.g. after a style change). */
  waitForIdle: (timeout?: number) => Promise<void>;
  /** Call map.queryRenderedFeatures at a pixel coordinate. */
  queryRenderedFeatures: (point: { x: number; y: number }, layerIds?: string[]) => Promise<RenderedFeature[]>;
  /** Check if a layer exists and is visible. */
  isLayerVisible: (layerId: string) => Promise<boolean>;
  /** Count non-background pixels on the canvas. */
  countNonBackgroundPixels: (bgColor?: { r: number; g: number; b: number }) => Promise<number>;
  /** Take a screenshot of the map container. */
  screenshot: () => Promise<Buffer>;
}

/**
 * Creates a MapLibre GL JS map inside `page`, injects the library from the
 * local node_modules, and waits for the initial `idle` event.
 */
export async function createMap(page: Page, options: MapOptions): Promise<MapHandle> {
  const { styleUrl, center = [-122.42, 37.77], zoom = 14, idleTimeout = 25_000 } = options;

  // Navigate to a minimal HTML page with a map container.
  await page.setContent(`
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8">
      <style>
        * { margin: 0; padding: 0; }
        #map { width: 512px; height: 512px; }
      </style>
    </head>
    <body><div id="map"></div></body>
    </html>
  `);

  // Inject maplibre-gl CSS and JS from local node_modules.
  const cssContent = readFileSync(resolve(maplibreDistDir, 'maplibre-gl.css'), 'utf-8');
  await page.addStyleTag({ content: cssContent });
  const jsContent = readFileSync(resolve(maplibreDistDir, 'maplibre-gl.js'), 'utf-8');
  await page.addScriptTag({ content: jsContent });

  // Create the map and wait for idle.
  await page.evaluate(
    ({ styleUrl, center, zoom }) => {
      return new Promise<void>((resolve, reject) => {
        const timeoutId = setTimeout(() => reject(new Error('Map idle timeout')), 25000);
        const map = new (window as any).maplibregl.Map({
          container: 'map',
          style: styleUrl,
          center,
          zoom,
          fadeDuration: 0,
          trackResize: false,
        });
        (window as any).__map = map;
        map.once('idle', () => {
          clearTimeout(timeoutId);
          resolve();
        });
        map.on('error', (e: any) => {
          clearTimeout(timeoutId);
          reject(new Error(`MapLibre error: ${e.error?.message ?? e.message ?? 'unknown'}`));
        });
      });
    },
    { styleUrl, center, zoom },
  );

  // Wait an additional page-level timeout for idle to propagate.
  await page.waitForFunction(
    () => (window as any).__map?.loaded() === true,
    { timeout: idleTimeout },
  );

  const handle: MapHandle = {
    async waitForIdle(timeout = idleTimeout) {
      await page.evaluate(
        (t) =>
          new Promise<void>((resolve, reject) => {
            const map = (window as any).__map;
            if (!map) return reject(new Error('Map not initialized'));
            const timeoutId = setTimeout(() => reject(new Error('Map idle timeout')), t);
            map.once('idle', () => {
              clearTimeout(timeoutId);
              resolve();
            });
          }),
        timeout,
      );
    },

    async queryRenderedFeatures(point, layerIds) {
      return page.evaluate(
        ({ point, layerIds }) => {
          const map = (window as any).__map;
          const opts = layerIds ? { layers: layerIds } : undefined;
          const features = map.queryRenderedFeatures([point.x, point.y], opts);
          // Serialize to plain objects (MapLibre features have circular refs).
          return features.map((f: any) => ({
            type: 'Feature',
            geometry: f.geometry,
            properties: f.properties,
            layer: { id: f.layer?.id, type: f.layer?.type },
            sourceLayer: f.sourceLayer,
          }));
        },
        { point, layerIds },
      );
    },

    async isLayerVisible(layerId) {
      return page.evaluate((id) => {
        const map = (window as any).__map;
        const layer = map.getLayer(id);
        if (!layer) return false;
        const visibility = map.getLayoutProperty(id, 'visibility');
        return visibility !== 'none';
      }, layerId);
    },

    async countNonBackgroundPixels(bgColor = { r: 0, g: 0, b: 0 }) {
      return page.evaluate((bg) => {
        const map = (window as any).__map;
        const canvas = map.getCanvas() as HTMLCanvasElement;
        const gl = canvas.getContext('webgl2') ?? canvas.getContext('webgl');
        if (!gl) return 0;
        const pixels = new Uint8Array(canvas.width * canvas.height * 4);
        gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        let count = 0;
        for (let i = 0; i < pixels.length; i += 4) {
          const r = pixels[i], g = pixels[i + 1], b = pixels[i + 2], a = pixels[i + 3];
          // Skip fully transparent or matching background.
          if (a === 0) continue;
          if (r === bg.r && g === bg.g && b === bg.b) continue;
          count++;
        }
        return count;
      }, bgColor);
    },

    async screenshot() {
      const element = page.locator('#map');
      return element.screenshot();
    },
  };

  return handle;
}

/** Read the maplibre-gl version from node_modules for cert evidence. */
export function getMapLibreVersion(): string {
  const pkgPath = resolve(import.meta.dirname, '..', 'node_modules', 'maplibre-gl', 'package.json');
  const pkg = JSON.parse(readFileSync(pkgPath, 'utf-8'));
  return pkg.version;
}
