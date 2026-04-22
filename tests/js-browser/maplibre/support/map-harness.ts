// Shared helper for creating and managing a MapLibre GL JS map inside a
// Playwright browser page. Injects maplibre-gl via CDN, creates the map,
// and waits for the `idle` event (all tiles rendered).

import type { Page } from '@playwright/test';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { readFileSync } from 'node:fs';

// Resolve the local maplibre-gl distribution for injection.
const maplibreDistDir = resolve(import.meta.dirname, '..', '..', 'node_modules', 'maplibre-gl', 'dist');
const API_PROXY_PREFIXES = ['/api/', '/tiles/', '/ogc/'];
const proxyOrigins = new Map<string, Promise<string>>();

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

async function getProxyOrigin(upstreamOrigin: string): Promise<string> {
  let proxyOriginPromise = proxyOrigins.get(upstreamOrigin);
  if (proxyOriginPromise) {
    return proxyOriginPromise;
  }

  proxyOriginPromise = (async () => {
    const server = createServer((req, res) => {
      const url = new URL(req.url ?? '/', 'http://localhost');

      if (API_PROXY_PREFIXES.some((prefix) => url.pathname.startsWith(prefix))) {
        void (async () => {
          try {
            const upstreamUrl = new URL(`${url.pathname}${url.search}`, upstreamOrigin);
            const upstreamResponse = await fetch(upstreamUrl, { method: req.method });
            const contentType = upstreamResponse.headers.get('content-type');
            if (contentType) {
              res.setHeader('Content-Type', contentType);
            }

            res.writeHead(upstreamResponse.status);
            if (contentType?.includes('json')) {
              const proxyOrigin = `http://127.0.0.1:${port}`;
              const body = await upstreamResponse.text();
              res.end(body.replaceAll(upstreamOrigin, proxyOrigin));
              return;
            }

            const body = Buffer.from(await upstreamResponse.arrayBuffer());
            res.end(body);
          } catch {
            res.writeHead(502);
            res.end('Upstream proxy request failed');
          }
        })();
        return;
      }

      if (url.pathname === '/' || url.pathname === '/index.html') {
        res.writeHead(200, { 'Content-Type': 'text/html' });
        res.end(`
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
        return;
      }

      res.writeHead(404);
      res.end('Not found');
    });

    const port = await new Promise<number>((resolvePort) => {
      server.listen(0, '127.0.0.1', () => {
        const address = server.address();
        if (address && typeof address === 'object') {
          resolvePort(address.port);
        }
      });
    });

    process.once('exit', () => {
      server.close();
    });

    return `http://127.0.0.1:${port}`;
  })();

  proxyOrigins.set(upstreamOrigin, proxyOriginPromise);
  return proxyOriginPromise;
}

/**
 * Creates a MapLibre GL JS map inside `page`, injects the library from the
 * local node_modules, and waits for the initial `idle` event.
 */
export async function createMap(page: Page, options: MapOptions): Promise<MapHandle> {
  const { styleUrl, center = [-122.42, 37.77], zoom = 14, idleTimeout = 25_000 } = options;
  const upstreamStyleUrl = new URL(styleUrl);
  const upstreamOrigin = upstreamStyleUrl.origin;
  const proxyOrigin = await getProxyOrigin(upstreamOrigin);
  const proxiedStyleUrl = new URL(`${upstreamStyleUrl.pathname}${upstreamStyleUrl.search}`, proxyOrigin).toString();

  // Navigate to a same-origin test page so MapLibre can fetch styles and tiles
  // through the local proxy without relying on permissive CORS from Honua.
  await page.goto(proxyOrigin);

  // Inject maplibre-gl CSS and JS from local node_modules.
  const cssContent = readFileSync(resolve(maplibreDistDir, 'maplibre-gl.css'), 'utf-8');
  await page.addStyleTag({ content: cssContent });
  const jsContent = readFileSync(resolve(maplibreDistDir, 'maplibre-gl.js'), 'utf-8');
  await page.addScriptTag({ content: jsContent });

  // Create the map and wait for idle.
  await page.evaluate(
    ({ styleUrl, center, zoom, idleTimeout, proxyOrigin, upstreamOrigin }) => {
      return new Promise<void>((resolve, reject) => {
        const timeoutId = setTimeout(() => reject(new Error('Map idle timeout')), idleTimeout);
        const map = new (window as any).maplibregl.Map({
          container: 'map',
          style: styleUrl,
          center,
          zoom,
          fadeDuration: 0,
          trackResize: false,
          // Preserve the WebGL drawing buffer so gl.readPixels() can read
          // rendered pixels after the frame is presented.
          preserveDrawingBuffer: true,
          // Keep all Honua fetches on the local proxy origin so browser tests
          // do not depend on cross-origin headers from the API server.
          transformRequest: (url: string) => {
            if (url.startsWith('/')) {
              return { url: proxyOrigin + url };
            }
            if (url.startsWith(upstreamOrigin)) {
              return { url: proxyOrigin + url.slice(upstreamOrigin.length) };
            }
            return { url };
          },
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
    { styleUrl: proxiedStyleUrl, center, zoom, idleTimeout, proxyOrigin, upstreamOrigin },
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

    async countNonBackgroundPixels(bgColor) {
      const screenshot = await page.locator('#map').screenshot();
      const screenshotBase64 = screenshot.toString('base64');

      return page.evaluate(
        async ({ bg, screenshotBase64 }) => {
          const image = new Image();
          image.src = `data:image/png;base64,${screenshotBase64}`;

          await new Promise<void>((resolve, reject) => {
            image.onload = () => resolve();
            image.onerror = () => reject(new Error('Failed to decode map screenshot'));
          });

          const sampleCanvas = document.createElement('canvas');
          sampleCanvas.width = image.naturalWidth;
          sampleCanvas.height = image.naturalHeight;
          const context = sampleCanvas.getContext('2d');
          if (!context) return 0;

          context.drawImage(image, 0, 0);
          const pixels = context.getImageData(0, 0, sampleCanvas.width, sampleCanvas.height).data;
          const background = bg ?? { r: pixels[0], g: pixels[1], b: pixels[2] };

          let count = 0;
          for (let i = 0; i < pixels.length; i += 4) {
            const r = pixels[i];
            const g = pixels[i + 1];
            const b = pixels[i + 2];
            const a = pixels[i + 3];
            if (a === 0) continue;
            if (r === background.r && g === background.g && b === background.b) continue;
            count++;
          }

          return count;
        },
        {
          bg: bgColor,
          screenshotBase64,
        },
      );
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
  const pkgPath = resolve(import.meta.dirname, '..', '..', 'node_modules', 'maplibre-gl', 'package.json');
  const pkg = JSON.parse(readFileSync(pkgPath, 'utf-8'));
  return pkg.version;
}
