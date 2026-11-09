import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';

export const CLIENT_SOURCE = '372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4';
export const SDK_VERSION = '1.32.1';

export async function openInstalledClient({ baseUrl, apiKey, name, view, timeoutMs = 20_000 }) {
  const endpoint = new URL('/mcp', baseUrl);
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
    || endpoint.username || endpoint.password || !apiKey) throw new Error('invalid-owned-target');
  if (view) endpoint.searchParams.set('view', view);
  const require = createRequire(import.meta.url);
  // SDK package exports do not expose its root package.json.
  let packageRoot = dirname(require.resolve('@modelcontextprotocol/sdk/client/index.js'));
  let sdk;
  for (let depth = 0; depth < 6; depth++, packageRoot = dirname(packageRoot)) {
    let candidate;
    try { candidate = JSON.parse(await readFile(resolve(packageRoot, 'package.json'), 'utf8')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (candidate?.name === '@modelcontextprotocol/sdk') { sdk = candidate; break; }
  }
  if (!sdk) throw new Error('missing-installed-sdk-root');
  const lockBytes = await readFile(resolve(packageRoot, '../../..', 'package-lock.json'));
  const lockSha256 = createHash('sha256').update(lockBytes).digest('hex');
  const installed = JSON.parse(lockBytes).packages['node_modules/@modelcontextprotocol/sdk'];
  if (sdk.version !== SDK_VERSION || installed?.version !== SDK_VERSION
    || lockSha256 !== '5e6b28f4c29b82b799190e9e92083c7aca38b879afc7e242bee4320a711e195f') throw new Error('unexpected-installed-sdk');
  const budget = AbortSignal.timeout(timeoutMs);
  const fetchOwned = (url, options = {}) => fetch(url, { ...options, redirect: 'error',
    signal: AbortSignal.any([budget, ...(options.signal ? [options.signal] : [])]) });
  const client = new Client({ name, version: '1' });
  try {
    await client.connect(new StreamableHTTPClientTransport(endpoint, {
      requestInit: { headers: { 'X-API-Key': apiKey } }, fetch: fetchOwned,
    }));
    return { client, fetchOwned, endpoint, metadata: { clientSource: CLIENT_SOURCE,
      sdkVersion: sdk.version, sdkIntegrity: installed.integrity, clientLockSha256: lockSha256,
      clientHelperSha256: createHash('sha256').update(await readFile(new URL(import.meta.url))).digest('hex') } };
  } catch (error) {
    await client.close();
    throw error;
  }
}

export async function discoverInstalledTools(client) {
  const tools = [];
  const cursors = new Set();
  let cursor;
  for (let page = 0; page < 100; page++) {
    const result = await client.listTools(cursor ? { cursor } : {}, { timeout: 5_000 });
    tools.push(...result.tools);
    cursor = result.nextCursor;
    if (!cursor) return tools;
    if (cursors.has(cursor)) throw new Error('incomplete-discovery');
    cursors.add(cursor);
  }
  throw new Error('incomplete-discovery');
}
