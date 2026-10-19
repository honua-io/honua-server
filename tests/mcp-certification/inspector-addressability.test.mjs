// Public installed CLI addressability controls, never candidate certification.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

const packageRoot = new URL('./node_modules/@modelcontextprotocol/inspector/', import.meta.url);
const installed = JSON.parse(await readFile(new URL('package.json', packageRoot)));
assert.equal(installed.version, '2.10.1');

async function publicCli(method, toolName) {
  const calls = [];
  const requests = [];
  const marker = `authored-known-result-${randomUUID()}`;
  const temporary = await mkdtemp(join(tmpdir(), 'honua-inspector-addressability-'));
  const peer = createServer(async (request, response) => {
    requests.push(request.method);
    if (request.method !== 'POST') {
      response.writeHead(request.method === 'DELETE' ? 200 : 405).end();
      return;
    }
    const chunks = []; let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > 64 * 1024) { response.writeHead(413).end(); return; }
      chunks.push(chunk);
    }
    const message = JSON.parse(Buffer.concat(chunks).toString());
    calls.push(message);
    if (!('id' in message)) { response.writeHead(202).end(); return; }
    let result;
    if (message.method === 'initialize') result = { protocolVersion: message.params.protocolVersion,
      capabilities: { tools: {} }, serverInfo: { name: 'authored-addressability-peer', version: '1' } };
    else if (message.method === 'tools/list') result = { tools: [{ name: 'authored_known_tool',
      description: 'Controlled public CLI call', inputSchema: { type: 'object', properties: {} } }] };
    else if (message.method === 'tools/call') result = { content: [{ type: 'text', text: marker }], isError: false };
    else if (message.method === 'ping') result = {};
    const reply = result === undefined
      ? { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Method not found on authored peer' } }
      : { jsonrpc: '2.0', id: message.id, result };
    response.writeHead(200, { 'Content-Type': 'application/json', 'Mcp-Session-Id': 'addressability-control-session' });
    response.end(JSON.stringify(reply));
  });
  await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
  let child;
  try {
    const outcome = await new Promise((resolve, reject) => {
      const args = [new URL('clients/launcher/build/index.js', packageRoot).pathname, '--cli',
        `http://127.0.0.1:${peer.address().port}/mcp`, '--transport', 'http', '--method', method, '--format', 'json'];
      if (toolName) args.push('--tool-name', toolName);
      child = spawn(process.execPath, args, { stdio: ['ignore', 'pipe', 'pipe'], env: {
        PATH: process.env.PATH, MCP_AUTO_OPEN_ENABLED: 'false', MCP_STORAGE_DIR: temporary,
        MCP_INSPECTOR_SECRET_STORE: 'file', MCP_INSPECTOR_SECRET_FILE: join(temporary, 'secrets.json'),
      } });
      let stdout = ''; let stderr = ''; let timedOut = false; let oversized = false;
      const collect = (name, chunk) => {
        if (oversized) return;
        if (name === 'stdout') stdout += chunk; else stderr += chunk;
        if (stdout.length + stderr.length > 512 * 1024) { oversized = true; child.kill('SIGTERM'); }
      };
      child.stdout.on('data', (chunk) => collect('stdout', chunk));
      child.stderr.on('data', (chunk) => collect('stderr', chunk));
      const timer = setTimeout(() => { timedOut = true; child.kill('SIGTERM'); }, 10_000);
      const killTimer = setTimeout(() => { timedOut = true; child.kill('SIGKILL'); }, 12_000);
      const clearTimers = () => { clearTimeout(timer); clearTimeout(killTimer); };
      child.on('error', (error) => { clearTimers(); reject(error); });
      child.on('close', (code, signal) => { clearTimers(); resolve({ code, signal, stdout, stderr, timedOut, oversized }); });
    });
    assert.equal(outcome.timedOut, false, 'timeout is not public method refusal');
    assert.equal(outcome.oversized, false);
    assert.equal(outcome.signal, null, 'process termination is not protocol proof');
    return { ...outcome, calls, requests, marker };
  } finally {
    child?.kill('SIGTERM');
    peer.closeAllConnections();
    await new Promise((resolve) => peer.close(resolve));
    await rm(temporary, { recursive: true, force: true });
  }
}

test('installed Inspector publicly invokes a known tool and returns the authored peer result', async () => {
  const result = await publicCli('tools/call', 'authored_known_tool');
  assert.equal(result.code, 0);
  assert.equal(result.calls.filter((value) => value.method === 'initialize').length, 1);
  assert.ok(result.calls.some((value) => value.method === 'tools/list'));
  const calls = result.calls.filter((value) => value.method === 'tools/call');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].params.name, 'authored_known_tool');
  const parsed = JSON.parse(result.stdout);
  assert.equal((parsed.result ?? parsed).content[0].text, result.marker);
});

test('installed Inspector refuses public one-shot ping before any outbound frame', async () => {
  const result = await publicCli('ping');
  assert.notEqual(result.code, 0);
  assert.match(result.stdout + result.stderr, /Unsupported method: ping/);
  assert.deepEqual(result.calls, [], 'an error from the peer cannot establish local unaddressability');
  assert.deepEqual(result.requests, [], 'the public allowlist must refuse before connecting');
});

test('installed Inspector rejects an unknown tool locally after real discovery without tools/call', async () => {
  const result = await publicCli('tools/call', '__authored_missing_tool__');
  assert.equal(result.code, 5);
  assert.match(result.stdout + result.stderr, /tool_not_found/);
  assert.match(result.stdout + result.stderr, /Tool '__authored_missing_tool__' not found on server/);
  assert.equal(result.calls.filter((value) => value.method === 'initialize').length, 1);
  assert.ok(result.calls.some((value) => value.method === 'tools/list'));
  assert.equal(result.calls.filter((value) => value.method === 'tools/call').length, 0);
});
