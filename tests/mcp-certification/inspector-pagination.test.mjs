// Public installed Inspector against controlled peers; never candidate proof.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { observeInspectorPagination } from './inspector-pagination.mjs';

const packageRoot = new URL('./node_modules/@modelcontextprotocol/inspector/', import.meta.url);
const installed = JSON.parse(await readFile(new URL('package.json', packageRoot)));
assert.equal(installed.version, '2.10.1');
const SOURCE = 'a'.repeat(40);

async function exercise(mode) {
  const calls = [];
  const temporary = await mkdtemp(join(tmpdir(), 'honua-inspector-pages-'));
  const peer = createServer(async (request, response) => {
    if (request.method !== 'POST') { response.writeHead(request.method === 'DELETE' ? 200 : 405).end(); return; }
    const chunks = []; for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    calls.push(body);
    if (!('id' in body)) { response.writeHead(202).end(); return; }
    let result;
    if (body.method === 'initialize') result = { protocolVersion: body.params.protocolVersion,
      capabilities: { resources: {} }, serverInfo: { name: 'pagination-control-peer', version: '1' } };
    else if (body.method === 'resources/list') {
      if (mode === 'http-error') { response.writeHead(503).end('PRIVATE_CONTROL_DIAGNOSTIC'); return; }
      if (mode === 'redirect') { response.writeHead(307, { Location: 'https://external.invalid' }).end(); return; }
      const cursor = body.params?.cursor;
      const index = cursor === 'page-1' ? 1 : cursor === 'page-2' ? 2 : 0;
      result = { resources: [{ uri: `honua://control/${mode === 'duplicate-uri' ? 0 : index}`, name: `resource-${index}` }],
        ...(index < 2 && mode !== 'single-page' ? { nextCursor: mode === 'repeat' ? 'page-1' : `page-${index + 1}` } : {}) };
    } else result = {};
    const wire = JSON.stringify({ jsonrpc: '2.0', id: mode === 'wrong-id' && body.method === 'resources/list' ? 'unrelated' : body.id, result });
    const sse = mode === 'sse' && body.method === 'resources/list';
    response.writeHead(200, { 'Content-Type': sse ? 'text/event-stream' : 'application/json', 'Mcp-Session-Id': 'inspector-control-session' });
    response.end(sse ? `event: message\ndata: ${wire}\n\n` : wire);
  });
  await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
  let child;
  const invoke = async (endpoint) => {
    if (mode === 'no-wire') return { resources: [{ uri: 'honua://control/0' }, { uri: 'honua://control/1' }] };
    const result = await new Promise((resolve, reject) => {
      child = spawn(process.execPath, [new URL('clients/launcher/build/index.js', packageRoot).pathname,
        '--cli', endpoint, '--transport', 'http', '--method', 'resources/list', '--format', 'json'], {
        env: { PATH: process.env.PATH, MCP_AUTO_OPEN_ENABLED: 'false', MCP_STORAGE_DIR: temporary,
          MCP_INSPECTOR_SECRET_STORE: 'file', MCP_INSPECTOR_SECRET_FILE: join(temporary, 'secrets.json') },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      let stdout = ''; let stderr = '';
      child.stdout.on('data', (chunk) => { stdout += chunk; });
      child.stderr.on('data', (chunk) => { stderr += chunk; });
      const timer = setTimeout(() => child.kill('SIGTERM'), 6_000);
      child.on('error', (error) => { clearTimeout(timer); reject(error); });
      child.on('close', (code, signal) => {
        clearTimeout(timer);
        if (code !== 0 || signal) reject(new Error('Public Inspector CLI refused or timed out.'));
        else { try { resolve(JSON.parse(stdout)); } catch (error) { reject(error); } }
      });
    });
    if (mode === 'false-merged-output') {
      const merged = result.result ?? result;
      merged.resources = merged.resources.slice(0, 1);
    }
    return result;
  };
  try {
    try {
      return { proof: await observeInspectorPagination({ endpoint: `http://127.0.0.1:${peer.address().port}/mcp`,
        invoke, inspectorVersion: installed.version, sourceSha: SOURCE }), calls };
    } catch (error) { return { error, calls }; }
  } finally {
    child?.kill('SIGTERM');
    peer.closeAllConnections();
    await new Promise((resolve) => peer.close(resolve));
    await rm(temporary, { recursive: true, force: true });
  }
}

for (const mode of ['json', 'sse']) {
  test(`installed public Inspector traverses actual three-page ${mode} chain`, async () => {
    const result = await exercise(mode);
    assert.equal(result.error, undefined, result.error?.message);
    assert.deepEqual(result.calls.filter((call) => call.method === 'resources/list').map((call) => call.params?.cursor), [undefined, 'page-1', 'page-2']);
    assert.equal(result.proof.inspectorVersion, '2.10.1');
    assert.equal(result.proof.sourceSha, SOURCE);
    assert.equal(result.proof.pageCount, 3);
    assert.equal(result.proof.cursorHandoffCount, 2);
    assert.equal(result.proof.resourceCount, 3);
    assert.equal(result.proof.clientResultMatchesWire, true);
  });
}

for (const mode of ['repeat', 'http-error', 'redirect', 'wrong-id', 'single-page', 'duplicate-uri', 'false-merged-output', 'no-wire']) {
  test(`wire observer rejects ${mode} without manufacturing client proof`, async () => {
    const result = await exercise(mode);
    assert.equal(result.proof, undefined);
    assert.match(result.error?.message ?? '', /^Inspector pagination wire proof failed\.$/);
    assert.ok(!result.error.message.includes('PRIVATE_CONTROL_DIAGNOSTIC'));
    if (mode !== 'no-wire') assert.ok(result.calls.some((call) => call.method === 'resources/list'), 'negative must reach the real installed client wire operation');
  });
}
