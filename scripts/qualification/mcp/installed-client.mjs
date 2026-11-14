import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';

import { CLIENT_SOURCE, SDK_VERSION, CLIENT_LOCK_SHA256 } from './client-pins.mjs';
export { CLIENT_SOURCE, SDK_VERSION, CLIENT_LOCK_SHA256 } from './client-pins.mjs';

export async function sourceSha256(moduleUrl) {
  return createHash('sha256').update(await readFile(new URL(moduleUrl))).digest('hex');
}

export function ownedClientEndpoint(baseUrl, apiKey) {
  const endpoint = new URL('/mcp', baseUrl);
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
    || endpoint.username || endpoint.password || !apiKey) throw new Error('invalid-owned-target');
  return endpoint;
}

export async function requirePublishedPackage(packageName, modulePath, expectedVersion) {
  const require = createRequire(import.meta.url);
  // SDK package exports do not expose its root package.json.
  let packageRoot = dirname(require.resolve(modulePath));
  let manifest;
  for (let depth = 0; depth < 6; depth++, packageRoot = dirname(packageRoot)) {
    let candidate;
    try { candidate = JSON.parse(await readFile(resolve(packageRoot, 'package.json'), 'utf8')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (candidate?.name === packageName) { manifest = candidate; break; }
  }
  if (!manifest) throw new Error('missing-installed-client-root');
  const lockBytes = await readFile(resolve(packageRoot, '../../..', 'package-lock.json'));
  const lockSha256 = createHash('sha256').update(lockBytes).digest('hex');
  const installed = JSON.parse(lockBytes).packages[`node_modules/${packageName}`];
  if (manifest.version !== expectedVersion || installed?.version !== expectedVersion
    || lockSha256 !== CLIENT_LOCK_SHA256) throw new Error('unexpected-installed-client');
  return { root: packageRoot, version: manifest.version, integrity: installed.integrity, lockSha256 };
}

export async function openInstalledClient({ baseUrl, apiKey, name, timeoutMs = 20_000 }) {
  const endpoint = ownedClientEndpoint(baseUrl, apiKey);
  const installed = await requirePublishedPackage('@modelcontextprotocol/sdk', '@modelcontextprotocol/sdk/client/index.js', SDK_VERSION);
  const budget = AbortSignal.timeout(timeoutMs);
  const fetchOwned = (url, options = {}) => fetch(url, { ...options, redirect: 'error',
    signal: AbortSignal.any([budget, ...(options.signal ? [options.signal] : [])]) });
  const client = new Client({ name, version: '1' });
  try {
    await client.connect(new StreamableHTTPClientTransport(endpoint, {
      requestInit: { headers: { 'X-API-Key': apiKey } }, fetch: fetchOwned,
    }));
    return { client, fetchOwned, endpoint, metadata: { clientSource: CLIENT_SOURCE,
      sdkVersion: installed.version, sdkIntegrity: installed.integrity, clientLockSha256: installed.lockSha256,
      clientHelperSha256: await sourceSha256(import.meta.url) } };
  } catch (error) {
    await client.close();
    throw error;
  }
}

export async function collectPages(fetchPage, reason) {
  const items = [];
  const cursors = new Set();
  let cursor;
  for (let page = 0; page < 100; page++) {
    const result = await fetchPage(cursor);
    items.push(...result.items);
    cursor = result.nextCursor;
    if (!cursor) return items;
    if (cursors.has(cursor)) throw new Error(reason);
    cursors.add(cursor);
  }
  throw new Error(reason);
}

export async function discoverInstalledTools(client) {
  return collectPages(async cursor => {
    const result = await client.listTools(cursor ? { cursor } : {}, { timeout: 5_000 });
    return { items: result.tools, nextCursor: result.nextCursor };
  }, 'incomplete-discovery');
}
