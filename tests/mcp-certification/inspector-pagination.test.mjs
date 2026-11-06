// Public installed Inspector against controlled peers; never candidate proof.
import assert from 'node:assert/strict';
import { createServer, request as httpRequest } from 'node:http';
import { spawn } from 'node:child_process';
import { readFile, writeFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';
import { observeInspectorPagination } from './inspector-pagination.mjs';

const packageRoot = new URL('./node_modules/@modelcontextprotocol/inspector/', import.meta.url);
const installed = JSON.parse(await readFile(new URL('package.json', packageRoot)));
assert.equal(installed.version, '2.10.1');
const SOURCE = 'a'.repeat(40);
const VIEW_KEY = 'honua.io/workflow-view';
const CONTROL_KEY = 'inspector-control-key';

// Candidate-shaped control peer. These mirror the server's real defaults:
// McpPagination.DefaultListPageSize (12), the 8 static resources, the bounded
// default workflow view, and the authenticated explicit `full` tools/list export
// (about 132 tools). Server page sizes are not changed to manufacture pages.
const DEFAULT_LIST_PAGE_SIZE = 12;
const STATIC_RESOURCES = ['catalog/processes', 'catalog/features', 'ops/health', 'ops/findings',
  'published-services', 'deployments', 'jobs', 'results'].map((path) => `honua://${path}`);
const FULL_CATALOG = Array.from({ length: 132 }, (_, index) => `honua_control_tool_${String(index).padStart(3, '0')}`);

function honuaShaped(body, headers) {
  const offset = body.params?.cursor === undefined ? 0 : Number(/^l1:(\d+)$/.exec(body.params.cursor)?.[1] ?? NaN);
  if (!Number.isSafeInteger(offset)) return { error: { code: -32602, message: 'invalid cursor' } };
  const page = (items) => {
    const next = offset + DEFAULT_LIST_PAGE_SIZE;
    return { items: items.slice(offset, next), ...(next < items.length ? { nextCursor: `l1:${next}` } : {}) };
  };
  if (body.method === 'resources/list') {
    const { items, nextCursor } = page(STATIC_RESOURCES);
    return { result: { resources: items.map((uri) => ({ uri, name: uri })), ...(nextCursor ? { nextCursor } : {}) } };
  }
  const view = body.params?.view ?? body.params?._meta?.[VIEW_KEY];
  if (view !== 'full') {
    // Bounded default view: one page, no continuation.
    return { result: { tools: FULL_CATALOG.slice(0, DEFAULT_LIST_PAGE_SIZE).map((name) => ({ name, inputSchema: { type: 'object' } })) } };
  }
  if (headers['x-api-key'] !== CONTROL_KEY) return { error: { code: -32001, message: 'authentication required' } };
  const { items, nextCursor } = page(FULL_CATALOG);
  return { result: { tools: items.map((name) => ({ name, inputSchema: { type: 'object' } })), ...(nextCursor ? { nextCursor } : {}) } };
}

function scripted(mode, body) {
  const cursor = body.params?.cursor;
  const index = cursor === 'page-1' ? 1 : cursor === 'page-2' ? 2 : 0;
  return { result: { tools: [{ name: `control_tool_${mode === 'duplicate-name' ? 0 : index}`, inputSchema: { type: 'object' } }],
    ...(index < 2 && mode !== 'single-page' ? { nextCursor: mode === 'repeat' ? 'page-1' : `page-${index + 1}` } : {}) } };
}

const HONUA_SHAPED = new Set(['honua-defaults', 'default-view', 'anonymous-full', 'resources-list']);

async function exercise(mode) {
  const calls = [];
  const temporary = await mkdtemp(join(tmpdir(), 'honua-inspector-pages-'));
  const peer = createServer(async (request, response) => {
    if (request.method !== 'POST') { response.writeHead(request.method === 'DELETE' ? 200 : 405).end(); return; }
    const chunks = []; for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    calls.push(body);
    if (!('id' in body)) { response.writeHead(202).end(); return; }
    const listed = ['tools/list', 'resources/list'].includes(body.method);
    let outcome;
    if (body.method === 'initialize') outcome = { result: { protocolVersion: body.params.protocolVersion,
      capabilities: { tools: {}, resources: {} }, serverInfo: { name: 'pagination-control-peer', version: '1' } } };
    else if (listed && HONUA_SHAPED.has(mode)) outcome = honuaShaped(body, request.headers);
    else if (body.method === 'tools/list') {
      if (mode === 'http-error') { response.writeHead(503).end('PRIVATE_CONTROL_DIAGNOSTIC'); return; }
      if (mode === 'redirect') { response.writeHead(307, { Location: 'https://external.invalid' }).end(); return; }
      outcome = scripted(mode, body);
    } else outcome = { result: {} };
    if (mode === 'oversize-gzip' && body.method === 'tools/list') outcome.result.tools[0].description = 'x'.repeat(4 * 1024 * 1024);
    const wire = JSON.stringify({ jsonrpc: '2.0', id: mode === 'wrong-id' && body.method === 'tools/list' ? 'unrelated' : body.id, ...outcome });
    const sse = ['sse', 'gzip-sse'].includes(mode) && body.method === 'tools/list';
    let bytes = Buffer.from(sse ? `event: message\ndata: ${wire}\n\n` : wire);
    const encoding = mode.startsWith('gzip-') || ['invalid-gzip', 'oversize-gzip'].includes(mode) ? 'gzip'
      : mode === 'deflate-json' ? 'deflate' : mode === 'br-json' ? 'br' : undefined;
    if (encoding && !(mode === 'invalid-gzip' && body.method === 'tools/list')) bytes = { gzip: gzipSync, deflate: deflateSync, br: brotliCompressSync }[encoding](bytes);
    response.writeHead(200, { 'Content-Type': sse ? 'text/event-stream' : 'application/json',
      'Mcp-Session-Id': 'inspector-control-session', ...(encoding ? { 'Content-Encoding': encoding } : {}) });
    response.end(bytes);
  });
  await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
  let child;
  let completedOutput; let observedExit;
  const invoke = async (endpoint) => {
    if (mode === 'no-wire') return { tools: [{ name: 'control_tool_0' }, { name: 'control_tool_1' }] };
    if (mode === 'view-override') return directViewOverride(endpoint);
    const launcher = new URL('clients/launcher/build/index.js', packageRoot).pathname;
    let prefix = [launcher];
    if (mode === 'nonzero-exit') {
      const wrapper = join(temporary, 'nonzero-control.mjs');
      await writeFile(wrapper, `import { spawnSync } from 'node:child_process';
const value = spawnSync(process.execPath, process.argv.slice(2), { encoding: 'utf8', timeout: 5500 });
process.stdout.write(value.stdout ?? '', () => process.stderr.write(value.stderr ?? '', () =>
process.exit(value.status === 0 ? 7 : (value.status ?? 1))));`);
      prefix = [wrapper, launcher];
    }
    // The canonical producer's authenticated full-catalog argv. Controls drop
    // exactly one of its parts to prove each is required.
    const method = mode === 'resources-list' ? 'resources/list' : 'tools/list';
    const metadata = ['default-view', 'missing-view', 'resources-list'].includes(mode) ? [] : ['--metadata', `${VIEW_KEY}="full"`];
    const credential = ['anonymous-full', 'missing-credential'].includes(mode) ? [] : ['--header', `X-API-Key: ${CONTROL_KEY}`];
    const result = await new Promise((resolve, reject) => {
      child = spawn(process.execPath, [...prefix,
        '--cli', endpoint, '--transport', 'http', '--method', method, '--format', 'json', ...metadata, ...credential], {
        env: { PATH: process.env.PATH, MCP_AUTO_OPEN_ENABLED: 'false', MCP_STORAGE_DIR: temporary,
          MCP_INSPECTOR_SECRET_STORE: 'file', MCP_INSPECTOR_SECRET_FILE: join(temporary, 'secrets.json') },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      let stdout = ''; let stderr = '';
      child.stdout.on('data', (chunk) => { stdout += chunk; });
      child.stderr.on('data', (chunk) => { stderr += chunk; });
      const timer = setTimeout(() => child.kill('SIGTERM'), 10_000);
      child.on('error', (error) => { clearTimeout(timer); reject(error); });
      child.on('close', (code, signal) => {
        clearTimeout(timer);
        observedExit = code;
        try { completedOutput = JSON.parse(stdout); } catch { /* Missing output cannot establish the fault control. */ }
        if (code !== 0 || signal) reject(new Error('Public Inspector CLI refused or timed out.'));
        else { try { resolve(JSON.parse(stdout)); } catch (error) { reject(error); } }
      });
    });
    if (mode === 'false-merged-output') {
      const merged = result.result ?? result;
      merged.tools = merged.tools.slice(0, 1);
    }
    return result;
  };
  try {
    try {
      return { proof: await observeInspectorPagination({ endpoint: `http://127.0.0.1:${peer.address().port}/mcp`,
        invoke, inspectorVersion: installed.version, sourceSha: SOURCE }), calls, completedOutput, observedExit };
    } catch (error) { return { error, calls, completedOutput, observedExit }; }
  } finally {
    child?.kill('SIGTERM');
    peer.closeAllConnections();
    await new Promise((resolve) => peer.close(resolve));
    await rm(temporary, { recursive: true, force: true });
  }
}

// A wire client the Inspector cannot impersonate: a full-view _meta selector
// overridden by a higher-precedence params.view on every page. The observer
// must not accept a chain whose server-side view is not the full export.
async function directViewOverride(endpoint) {
  const post = (body) => new Promise((resolve, reject) => {
    const request = httpRequest(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json',
      Accept: 'application/json, text/event-stream', 'X-API-Key': CONTROL_KEY, 'Mcp-Session-Id': 'inspector-control-session' } }, (response) => {
      const chunks = []; response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => resolve(JSON.parse(Buffer.concat(chunks).toString())));
    });
    request.on('error', reject);
    request.end(JSON.stringify(body));
  });
  const tools = [];
  let cursor;
  for (let id = 1; id <= 3; id++) {
    const { result } = await post({ jsonrpc: '2.0', id, method: 'tools/list',
      params: { view: 'setup', _meta: { [VIEW_KEY]: 'full' }, ...(cursor ? { cursor } : {}) } });
    tools.push(...result.tools);
    cursor = result.nextCursor;
    if (!cursor) break;
  }
  return { tools };
}

const toolCursors = (calls) => calls.filter((call) => call.method === 'tools/list').map((call) => call.params?.cursor);

for (const mode of ['json', 'sse', 'gzip-json', 'gzip-sse', 'deflate-json', 'br-json']) {
  test(`installed public Inspector traverses actual three-page authenticated full tools/list ${mode} chain`, async () => {
    const result = await exercise(mode);
    assert.equal(result.error, undefined, result.error?.message);
    assert.deepEqual(toolCursors(result.calls), [undefined, 'page-1', 'page-2']);
    assert.ok(result.calls.filter((call) => call.method === 'tools/list').every((call) => call.params?._meta?.[VIEW_KEY] === 'full'));
    assert.equal(result.proof.schema, 'honua.inspector-pagination-observer/v2');
    assert.equal(result.proof.method, 'tools/list');
    assert.equal(result.proof.workflowView, 'full');
    assert.equal(result.proof.authenticated, true);
    assert.equal(result.proof.inspectorVersion, '2.10.1');
    assert.equal(result.proof.sourceSha, SOURCE);
    assert.equal(result.proof.pageCount, 3);
    assert.equal(result.proof.cursorHandoffCount, 2);
    assert.equal(result.proof.toolCount, 3);
    assert.equal(result.proof.clientResultMatchesWire, true);
    assert.ok(!JSON.stringify(result.proof).includes(CONTROL_KEY), 'credential value must never be retained');
  });
}

test('installed public Inspector proves multi-page tools/list at candidate-shaped real default page size', async () => {
  const result = await exercise('honua-defaults');
  assert.equal(result.error, undefined, result.error?.message);
  const pages = Math.ceil(FULL_CATALOG.length / DEFAULT_LIST_PAGE_SIZE);
  assert.equal(pages, 11);
  assert.deepEqual(toolCursors(result.calls),
    [undefined, ...Array.from({ length: pages - 1 }, (_, index) => `l1:${(index + 1) * DEFAULT_LIST_PAGE_SIZE}`)]);
  assert.equal(result.proof.pageCount, pages);
  assert.equal(result.proof.cursorHandoffCount, pages - 1);
  assert.equal(result.proof.toolCount, FULL_CATALOG.length);
  assert.equal(result.proof.clientResultMatchesWire, true);
  assert.deepEqual((result.completedOutput.result ?? result.completedOutput).tools.map(({ name }) => name), FULL_CATALOG);
});

test('candidate-shaped resources/list is one default page and cannot prove cursor traversal', async () => {
  // Why the proof moved: 8 static resources never exceed the 12-item default page.
  const result = await exercise('resources-list');
  assert.equal(result.proof, undefined);
  assert.match(result.error?.message ?? '', /^Inspector pagination wire proof failed\.$/);
  assert.equal(result.observedExit, 0);
  const resources = result.calls.filter((call) => call.method === 'resources/list');
  assert.equal(resources.length, 1);
  assert.equal(resources[0].params?.cursor, undefined);
  assert.equal((result.completedOutput.result ?? result.completedOutput).resources.length, STATIC_RESOURCES.length);
});

for (const mode of ['repeat', 'http-error', 'redirect', 'wrong-id', 'single-page', 'duplicate-name', 'false-merged-output',
  'no-wire', 'nonzero-exit', 'invalid-gzip', 'oversize-gzip', 'default-view', 'anonymous-full', 'missing-view',
  'missing-credential', 'view-override']) {
  test(`wire observer rejects ${mode} without manufacturing client proof`, async () => {
    const result = await exercise(mode);
    assert.equal(result.proof, undefined);
    assert.match(result.error?.message ?? '', /^Inspector pagination wire proof failed\.$/);
    assert.ok(!result.error.message.includes('PRIVATE_CONTROL_DIAGNOSTIC'));
    const output = result.completedOutput?.result ?? result.completedOutput;
    if (mode === 'oversize-gzip') {
      assert.equal(result.observedExit, 0, 'CLI must accept the highly compressed oversized response before observer rejects it');
      assert.equal(output.tools.length, 3);
    }
    if (mode === 'nonzero-exit') {
      assert.equal(result.observedExit, 7);
      assert.equal(output.tools.length, 3);
      assert.deepEqual(toolCursors(result.calls), [undefined, 'page-1', 'page-2']);
    }
    if (mode === 'default-view') {
      // The bounded default view returns one page without continuation.
      assert.equal(result.observedExit, 0);
      assert.deepEqual(toolCursors(result.calls), [undefined]);
      assert.equal(output.tools.length, DEFAULT_LIST_PAGE_SIZE);
    }
    if (['missing-view', 'missing-credential'].includes(mode)) {
      // A lenient peer still paginates; the observer itself must refuse the chain.
      assert.equal(result.observedExit, 0);
      assert.deepEqual(toolCursors(result.calls), [undefined, 'page-1', 'page-2']);
      assert.equal(output.tools.length, 3);
    }
    if (mode === 'anonymous-full') {
      const listed = result.calls.filter((call) => call.method === 'tools/list');
      assert.ok(listed.length >= 1 && listed.every((call) => call.params?._meta?.[VIEW_KEY] === 'full'));
    }
    if (mode === 'view-override') assert.deepEqual(toolCursors(result.calls), [undefined, 'page-1', 'page-2']);
    if (mode !== 'no-wire') assert.ok(result.calls.some((call) => ['tools/list', 'resources/list'].includes(call.method)),
      'negative must reach the real wire operation');
  });
}
