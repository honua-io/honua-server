import { type Page } from '@playwright/test';
import { createServer, type Server } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = resolve(__dirname, '..', 'fixtures');
const NODE_MODULES = resolve(__dirname, '..', 'node_modules');
const API_PROXY_PREFIXES = ['/rest/', '/temp/'];

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html',
  '.js': 'application/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.png': 'image/png',
};

/** Minimal static file server for the test page and vendored dependencies. */
export function createStaticServer(): { server: Server; close: () => Promise<void> } {
  const server = createServer(async (req, res) => {
    const url = new URL(req.url ?? '/', `http://localhost`);
    let filePath: string;

    if (API_PROXY_PREFIXES.some(prefix => url.pathname.startsWith(prefix))) {
      const upstreamBaseUrl = process.env.HONUA_BASE_URL;
      if (!upstreamBaseUrl) {
        res.writeHead(502);
        res.end('HONUA_BASE_URL is not configured');
        return;
      }

      try {
        const upstreamUrl = new URL(`${url.pathname}${url.search}`, upstreamBaseUrl);
        const upstreamResponse = await fetch(upstreamUrl, { method: req.method });
        const contentType = upstreamResponse.headers.get('content-type');
        if (contentType) {
          res.setHeader('Content-Type', contentType);
        }

        const body = Buffer.from(await upstreamResponse.arrayBuffer());
        res.writeHead(upstreamResponse.status);
        res.end(body);
      } catch {
        res.writeHead(502);
        res.end('Upstream proxy request failed');
      }

      return;
    }

    if (url.pathname.startsWith('/vendor/')) {
      // Serve from node_modules: /vendor/leaflet/dist/leaflet.js → node_modules/leaflet/dist/leaflet.js
      const relative = url.pathname.slice('/vendor/'.length);
      filePath = resolve(NODE_MODULES, relative);
    } else {
      // Serve from fixtures
      const relative = url.pathname === '/' ? 'test-page.html' : url.pathname.slice(1);
      filePath = resolve(FIXTURES_DIR, relative);
    }

    try {
      const content = await readFile(filePath);
      const ext = extname(filePath);
      res.writeHead(200, { 'Content-Type': MIME_TYPES[ext] ?? 'application/octet-stream' });
      res.end(content);
    } catch {
      res.writeHead(404);
      res.end('Not found');
    }
  });

  return {
    server,
    close: () => new Promise<void>((resolve, reject) => {
      server.close((err) => (err ? reject(err) : resolve()));
    }),
  };
}

/** Start the static server and return its URL. */
export async function startStaticServer(): Promise<{ url: string; close: () => Promise<void> }> {
  const { server, close } = createStaticServer();

  const port = await new Promise<number>((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const addr = server.address();
      if (addr && typeof addr === 'object') {
        resolve(addr.port);
      }
    });
  });

  return { url: `http://127.0.0.1:${port}`, close };
}

export interface MapConfig {
  baseUrl: string;
  serviceId: string;
  layerId: string | number;
  mapOptions?: Record<string, unknown>;
  layerOptions?: Record<string, unknown>;
}

/** Navigate to the test page and initialize a FeatureLayer. */
export async function initFeatureLayer(page: Page, staticUrl: string, config: MapConfig): Promise<void> {
  await page.goto(staticUrl);
  await page.evaluate((cfg) => {
    (window as any).__initFeatureLayer(cfg);
  }, {
    ...config,
    baseUrl: staticUrl,
  });
}

/** Navigate to the test page and initialize a DynamicMapLayer. */
export async function initDynamicMapLayer(page: Page, staticUrl: string, config: MapConfig): Promise<void> {
  await page.goto(staticUrl);
  await page.evaluate((cfg) => {
    (window as any).__initDynamicMapLayer(cfg);
  }, {
    ...config,
    baseUrl: staticUrl,
  });
}

/** Wait for the FeatureLayer 'load' event to fire. */
export async function waitForLayerLoad(page: Page, timeoutMs = 15000): Promise<void> {
  await page.waitForFunction(
    () => (window as any).__loadFired === true,
    { timeout: timeoutMs },
  );
}

/** Wait for network idle after map operations. */
export async function waitForMapIdle(page: Page, timeoutMs = 10000): Promise<void> {
  await page.waitForLoadState('networkidle');
  // Extra settle time for Leaflet canvas/tile rendering
  await page.waitForTimeout(500);
}

/** Check that the map container has non-blank rendered content. */
export async function assertMapNotBlank(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const mapEl = document.getElementById('map');
    if (!mapEl) return false;

    // Check for SVG paths (vector features)
    const svgPaths = mapEl.querySelectorAll('svg path');
    if (svgPaths.length > 0) return true;

    // Check for canvas with non-blank pixels
    const canvases = mapEl.querySelectorAll('canvas');
    for (const canvas of canvases) {
      const ctx = canvas.getContext('2d');
      if (!ctx) continue;
      const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      for (let i = 3; i < data.length; i += 4) {
        if (data[i] > 0) return true; // Non-transparent pixel found
      }
    }

    // Check for tile images
    const tileImages = mapEl.querySelectorAll('.leaflet-tile-container img, .leaflet-image-layer img');
    for (const img of tileImages) {
      if ((img as HTMLImageElement).complete && (img as HTMLImageElement).naturalWidth > 0) return true;
    }

    return false;
  });
}

/** Get all features currently loaded in the FeatureLayer. */
export async function getAllFeatures(page: Page): Promise<Array<{ properties: Record<string, unknown>; geometry: unknown }>> {
  return page.evaluate(() => {
    const layer = (window as any).__featureLayer;
    if (!layer) return [];

    const features: Array<{ properties: Record<string, unknown>; geometry: unknown }> = [];
    layer.eachFeature((f: any) => {
      features.push({
        properties: f.feature?.properties ?? {},
        geometry: f.feature?.geometry ?? null,
      });
    });
    return features;
  });
}
