// This adapter proves installed-client acknowledgement only. The enclosing
// lifecycle harness independently proves native cessation and absence of output.
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

export const CLIENT_SOURCE = '372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4';
export const SDK_VERSION = '1.32.1';

function acknowledgement(result, jobId) {
  if (result.isError !== undefined && result.isError !== false) throw new Error('tool-error');
  const structured = result.structuredContent;
  const text = result.content?.filter((item) => item.type === 'text').map((item) => JSON.parse(item.text));
  const outputs = [structured, ...(text ?? [])].filter((value) => value !== undefined);
  if (!outputs.length || outputs.some((value) => value?.jobId !== jobId
    || value.status !== 'cancellation_requested' || value.cancellationRequested !== true
    || value.result !== undefined || value.results !== undefined)) throw new Error('invalid-acknowledgement');
  return { jobId, status: 'cancellation_requested', cancellationRequested: true };
}

export async function cancelDurableJob({ baseUrl, apiKey, jobId }) {
  const endpoint = new URL('/mcp', baseUrl);
  if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname)
    || endpoint.username || endpoint.password || !apiKey || !/^[A-Za-z0-9_-]{1,128}$/.test(jobId)) throw new Error('invalid-owned-target');
  const require = createRequire(import.meta.url);
  // Package exports map package.json to a generated dist package on this SDK;
  // locate its actual named root from the authenticated installed entry point.
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
  const client = new Client({ name: 'honua-gp-lifecycle-cancellation', version: '1' });
  const budget = AbortSignal.timeout(20_000);
  const transport = new StreamableHTTPClientTransport(endpoint, {
    requestInit: { headers: { 'X-API-Key': apiKey } },
    fetch: (url, options) => fetch(url, { ...options, redirect: 'error',
      signal: AbortSignal.any([budget, ...(options?.signal ? [options.signal] : [])]) }),
  });
  try {
    await client.connect(transport);
    // Public discovery must expose the actual tool; a scripted direct JSON-RPC
    // request or HTTP success does not establish installed-client support.
    let cursor;
    let found = false;
    const cursors = new Set();
    for (let page = 0; page < 100; page++) {
      const result = await client.listTools(cursor ? { cursor } : {}, { timeout: 5_000 });
      found ||= result.tools.some((tool) => tool.name === 'honua_cancel_job'
        && tool.inputSchema?.required?.includes('jobId') && tool.inputSchema.properties?.jobId?.type === 'string');
      cursor = result.nextCursor;
      if (!cursor) break;
      if (cursors.has(cursor) || page === 99) throw new Error('incomplete-discovery');
      cursors.add(cursor);
    }
    if (!found) throw new Error('missing-cancellation-tool');
    const responses = [];
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await client.callTool({ name: 'honua_cancel_job', arguments: { jobId } }, undefined, { timeout: 5_000 });
      responses.push(acknowledgement(response, jobId));
    }
    return { schema: 'honua.gp-mcp-cancellation-ack/v1', clientSource: CLIENT_SOURCE,
      sdkVersion: sdk.version, sdkIntegrity: installed.integrity, clientLockSha256: lockSha256, adapterSha256: createHash('sha256').update(await readFile(new URL(import.meta.url))).digest('hex'),
      jobId, tool: 'honua_cancel_job', responses, executionCessationProven: false };
  } finally {
    await client.close();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = await cancelDurableJob({ baseUrl: process.argv[2], jobId: process.argv[3], apiKey: process.env.HONUA_GP_MCP_API_KEY });
    process.stdout.write(JSON.stringify(result) + '\n');
  } catch {
    // Do not echo server responses, credentials, URLs or provider diagnostics.
    process.stderr.write('Installed MCP durable-job cancellation acknowledgement failed.\n');
    process.exitCode = 1;
  }
}
