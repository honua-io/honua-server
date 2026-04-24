/**
 * Minimal Node.js HTTP server that serves the OpenLayers test page
 * and the OL distribution assets from node_modules.
 *
 * Started automatically by Playwright's webServer config.
 */

import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { resolve, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const PORT = parseInt(process.env.OL_TEST_PAGE_PORT ?? '9876', 10);
const ROOT = dirname(fileURLToPath(import.meta.url));
const NODE_MODULES = resolve(ROOT, '..', '..', 'node_modules');
const API_PROXY_PREFIXES = ['/ogc/', '/api/', '/tiles/'];
const PROXY_ORIGIN = `http://localhost:${PORT}`;

const MIME: Record<string, string> = {
  '.html': 'text/html',
  '.js': 'application/javascript',
  '.mjs': 'application/javascript',
  '.css': 'text/css',
  '.json': 'application/json',
  '.map': 'application/json',
};

function tryRead(filePath: string): Buffer | null {
  try {
    return readFileSync(filePath);
  } catch {
    return null;
  }
}

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', `http://localhost:${PORT}`);
  const pathname = url.pathname;

  if (API_PROXY_PREFIXES.some((prefix) => pathname.startsWith(prefix))) {
    const upstreamBaseUrl = process.env.HONUA_BASE_URL;
    if (!upstreamBaseUrl) {
      res.writeHead(502);
      res.end('HONUA_BASE_URL is not configured');
      return;
    }

    void (async () => {
      try {
        const upstreamUrl = new URL(`${pathname}${url.search}`, upstreamBaseUrl);
        const upstreamResponse = await fetch(upstreamUrl, { method: req.method });
        const contentType = upstreamResponse.headers.get('content-type');
        const upstreamOrigin = new URL(upstreamBaseUrl).origin;
        if (contentType) {
          res.setHeader('Content-Type', contentType);
        }

        res.writeHead(upstreamResponse.status);
        if (contentType?.includes('json')) {
          const body = await upstreamResponse.text();
          res.end(body.replaceAll(upstreamOrigin, PROXY_ORIGIN));
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

  // Serve test-page.html at root
  if (pathname === '/' || pathname === '/test-page.html') {
    const content = tryRead(resolve(ROOT, 'test-page.html'));
    if (content) {
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end(content);
      return;
    }
  }

  // Serve OL dist assets from node_modules
  if (pathname.startsWith('/ol/')) {
    const relPath = pathname.slice(4); // strip /ol/
    const filePath = resolve(NODE_MODULES, 'ol', relPath);
    const content = tryRead(filePath);
    if (content) {
      const ext = extname(filePath);
      res.writeHead(200, {
        'Content-Type': MIME[ext] ?? 'application/octet-stream',
      });
      res.end(content);
      return;
    }
  }

  res.writeHead(404);
  res.end('Not found');
});

server.listen(PORT, () => {
  console.log(`OpenLayers test page server listening on http://localhost:${PORT}`);
});
