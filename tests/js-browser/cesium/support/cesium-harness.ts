// Shared helper for creating and managing a CesiumJS Viewer inside a
// Playwright browser page. Serves a same-origin static page that proxies
// Honua API requests so cross-origin headers are not required.
//
// Mirrors the proxy + canvas-sampling pattern used by tests/js-browser/maplibre/
// (see support/map-harness.ts). Cesium rendering is GPU-bound in a real browser
// but the WebGL drawing buffer is preserved so non-black pixel sampling is
// possible after a frame is presented.

import type { Page } from '@playwright/test';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { readFileSync } from 'node:fs';

const cesiumDistDir = resolve(import.meta.dirname, '..', 'node_modules', 'cesium', 'Build', 'Cesium');
const API_PROXY_PREFIXES = ['/api/', '/tiles/', '/ogc/', '/rest/', '/wms', '/wmts', '/wfs'];
const proxyOrigins = new Map<string, Promise<string>>();

/** Options for creating a Cesium viewer. */
export interface ViewerOptions {
  /** Optional initial center [lon, lat]. */
  center?: [number, number];
  /** Idle timeout (ms) for tilesLoaded. Defaults to 30000. */
  idleTimeout?: number;
}

/** Tile request observation captured via `page.route`. */
export interface TileRequest {
  url: string;
  status: number;
  contentType: string;
}

/** Captures image requests made by a Cesium imagery provider. */
export interface ImageryRequestObserver {
  requests: TileRequest[];
  failures: string[];
  dispose: () => Promise<void>;
}

/** Return type from `createViewer`. Provides helpers for assertions. */
export interface ViewerHandle {
  /** Wait until the globe reports tilesLoaded === true. */
  waitForTilesLoaded: (timeout?: number) => Promise<void>;
  /** Count non-background pixels on the Cesium canvas. */
  countNonBackgroundPixels: (bgColor?: { r: number; g: number; b: number }) => Promise<number>;
  /** Take a screenshot of the Cesium canvas. */
  screenshot: () => Promise<Buffer>;
  /** The same-origin proxy origin (e.g. http://127.0.0.1:43117). */
  proxyOrigin: string;
}

export async function observeImageryRequests(
  page: Page,
  urlPattern: Parameters<Page['route']>[0],
): Promise<ImageryRequestObserver> {
  const requests: TileRequest[] = [];
  const failures: string[] = [];
  const routeHandler: Parameters<Page['route']>[1] = async (route) => {
    const url = route.request().url();
    try {
      const response = await route.fetch();
      requests.push({
        url,
        status: response.status(),
        contentType: response.headers()['content-type'] ?? '',
      });
      await route.fulfill({ response });
    } catch (error) {
      failures.push(`${url}: ${error instanceof Error ? error.message : String(error)}`);
      await route.abort();
    }
  };

  await page.route(urlPattern, routeHandler);

  return {
    requests,
    failures,
    async dispose() {
      await page.unroute(urlPattern, routeHandler);
    },
  };
}

export function successfulImageResponses(observer: ImageryRequestObserver): TileRequest[] {
  return observer.requests.filter((request) =>
    request.status >= 200
    && request.status < 300
    && request.contentType.toLowerCase().includes('image/'));
}

async function getProxyOrigin(upstreamOrigin: string): Promise<string> {
  let proxyOriginPromise = proxyOrigins.get(upstreamOrigin);
  if (proxyOriginPromise) {
    return proxyOriginPromise;
  }

  proxyOriginPromise = (async () => {
    let port = 0;
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
            if (contentType?.includes('json') || contentType?.includes('xml')) {
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
        res.end(`<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    html, body, #cesiumContainer { width: 512px; height: 512px; margin: 0; padding: 0; }
    .cesium-widget-credits { display: none !important; }
  </style>
</head>
<body><div id="cesiumContainer"></div></body>
</html>`);
        return;
      }

      res.writeHead(404);
      res.end('Not found');
    });

    port = await new Promise<number>((resolvePort) => {
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
 * Loads CesiumJS from local node_modules into the page and returns the
 * proxy origin. Caller installs imagery providers via page.evaluate.
 */
export async function createViewer(page: Page, options: ViewerOptions = {}): Promise<ViewerHandle> {
  const { idleTimeout = 30_000 } = options;
  const upstreamOrigin = new URL(process.env.HONUA_BASE_URL ?? 'http://localhost:5000').origin;
  const proxyOrigin = await getProxyOrigin(upstreamOrigin);

  await page.goto(proxyOrigin);

  // Inject Cesium Widgets CSS + JS from local node_modules.
  const cssContent = readFileSync(resolve(cesiumDistDir, 'Widgets', 'widgets.css'), 'utf-8');
  await page.addStyleTag({ content: cssContent });
  const jsContent = readFileSync(resolve(cesiumDistDir, 'Cesium.js'), 'utf-8');
  await page.addScriptTag({ content: jsContent });

  // Set CESIUM_BASE_URL so static assets (worker scripts, fonts) resolve to a
  // path the page can serve. Cesium fetches some Workers lazily; static asset
  // failures are tolerated because this harness asserts at the imagery level
  // rather than the full 3D scene.
  await page.evaluate(() => {
    (window as any).CESIUM_BASE_URL = '/cesium-static/';
    (window as any).Cesium.Ion.defaultAccessToken = '';
  });

  const handle: ViewerHandle = {
    proxyOrigin,
    async waitForTilesLoaded(timeout = idleTimeout) {
      await page.waitForFunction(
        () => {
          const v = (window as any).__cesiumViewer;
          if (!v) return false;
          return v.scene?.globe?.tilesLoaded === true;
        },
        { timeout },
      );
    },
    async countNonBackgroundPixels(bgColor) {
      const screenshot = await page.locator('#cesiumContainer canvas').first().screenshot();
      const screenshotBase64 = screenshot.toString('base64');
      return page.evaluate(
        async ({ bg, screenshotBase64 }) => {
          const image = new Image();
          image.src = `data:image/png;base64,${screenshotBase64}`;
          await new Promise<void>((resolve, reject) => {
            image.onload = () => resolve();
            image.onerror = () => reject(new Error('Failed to decode Cesium screenshot'));
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
        { bg: bgColor, screenshotBase64 },
      );
    },
    async screenshot() {
      return page.locator('#cesiumContainer canvas').first().screenshot();
    },
  };

  return handle;
}

/** Read the cesium version from node_modules for cert evidence. */
export function getCesiumVersion(): string {
  try {
    const pkgPath = resolve(import.meta.dirname, '..', 'node_modules', 'cesium', 'package.json');
    const pkg = JSON.parse(readFileSync(pkgPath, 'utf-8'));
    return pkg.version ?? 'unknown';
  } catch {
    return 'unknown';
  }
}
