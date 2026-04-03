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
