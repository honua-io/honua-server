// Public installed Inspector against controlled peers; never candidate proof.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
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
    if (mode === 'oversize-gzip' && body.method === 'resources/list') result.resources[0].name = 'x'.repeat(4 * 1024 * 1024);
    const wire = JSON.stringify({ jsonrpc: '2.0', id: mode === 'wrong-id' && body.method === 'resources/list' ? 'unrelated' : body.id, result });
    const sse = ['sse', 'gzip-sse'].includes(mode) && body.method === 'resources/list';
    let bytes = Buffer.from(sse ? `event: message\ndata: ${wire}\n\n` : wire);
    const encoding = mode.startsWith('gzip-') || ['invalid-gzip', 'oversize-gzip'].includes(mode) ? 'gzip'
      : mode === 'deflate-json' ? 'deflate' : mode === 'br-json' ? 'br' : undefined;
    if (encoding && !(mode === 'invalid-gzip' && body.method === 'resources/list')) bytes = { gzip: gzipSync, deflate: deflateSync, br: brotliCompressSync }[encoding](bytes);
    response.writeHead(200, { 'Content-Type': sse ? 'text/event-stream' : 'application/json',
      'Mcp-Session-Id': 'inspector-control-session', ...(encoding ? { 'Content-Encoding': encoding } : {}) });
    response.end(bytes);
  });
  await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
  let child;
  let completedOutput; let observedExit;
  const invoke = async (endpoint) => {
    if (mode === 'no-wire') return { resources: [{ uri: 'honua://control/0' }, { uri: 'honua://control/1' }] };
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
    const result = await new Promise((resolve, reject) => {
      child = spawn(process.execPath, [...prefix,
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
        observedExit = code;
        try { completedOutput = JSON.parse(stdout); } catch { /* Missing output cannot establish the fault control. */ }
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
    } catch (error) { return { error, calls, completedOutput, observedExit }; }
  } finally {
    child?.kill('SIGTERM');
    peer.closeAllConnections();
    await new Promise((resolve) => peer.close(resolve));
    await rm(temporary, { recursive: true, force: true });
  }
}

for (const mode of ['json', 'sse', 'gzip-json', 'gzip-sse', 'deflate-json', 'br-json']) {
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

for (const mode of ['repeat', 'http-error', 'redirect', 'wrong-id', 'single-page', 'duplicate-uri', 'false-merged-output', 'no-wire', 'nonzero-exit', 'invalid-gzip', 'oversize-gzip']) {
  test(`wire observer rejects ${mode} without manufacturing client proof`, async () => {
    const result = await exercise(mode);
    assert.equal(result.proof, undefined);
    assert.match(result.error?.message ?? '', /^Inspector pagination wire proof failed\.$/);
    assert.ok(!result.error.message.includes('PRIVATE_CONTROL_DIAGNOSTIC'));
    if (mode === 'oversize-gzip') {
      assert.equal(result.observedExit, 0, 'CLI must accept the highly compressed oversized response before observer rejects it');
      assert.equal((result.completedOutput.result ?? result.completedOutput).resources.length, 3);
    }
    if (mode === 'nonzero-exit') {
      assert.equal(result.observedExit, 7);
      assert.equal((result.completedOutput.result ?? result.completedOutput).resources.length, 3);
      assert.deepEqual(result.calls.filter((call) => call.method === 'resources/list').map((call) => call.params?.cursor), [undefined, 'page-1', 'page-2']);
    }
    if (mode !== 'no-wire') assert.ok(result.calls.some((call) => call.method === 'resources/list'), 'negative must reach the real installed client wire operation');
  });
}
