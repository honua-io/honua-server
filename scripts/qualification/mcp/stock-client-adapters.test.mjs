import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { runStockClientContract } from './stock-client-contract.mjs';
import { ownedClientEndpoint, requirePublishedPackage } from './installed-client.mjs';

const key = 'synthetic-stock-client-key';
const rosters = { default: ['read'], setup: ['read', 'write'] };

async function withSyntheticPeer(change, exercise) {
  const calls = [];
  const server = createServer(async (request, response) => {
    if (request.headers['x-api-key'] !== key) {
      response.writeHead(401).end();
      return;
    }
    if (request.method !== 'POST') {
      response.writeHead(405).end();
      return;
    }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const message = JSON.parse(Buffer.concat(chunks));
    if (message.id === undefined) {
      response.writeHead(202).end();
      return;
    }
    const view = message.params?._meta?.['honua.io/workflow-view'] ?? 'default';
    let result;
    if (message.method === 'initialize') {
      result = { protocolVersion: message.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'synthetic-contract-peer', version: '1' } };
    } else if (message.method === 'tools/list') {
      result = { tools: (rosters[view] ?? []).map(name => ({ name, inputSchema: { type: 'object', properties: {} } })) };
    } else if (message.method === 'tools/call') {
      calls.push({ view, name: message.params.name, args: message.params.arguments });
      const structuredContent = message.params.name === 'read'
        ? { data: 'x'.repeat(2048) }
        : { code: 'invalid_argument', message: 'sourceId from honua_list_jobs is required.' };
      result = change({ isError: message.params.name === 'write', structuredContent,
        content: [{ type: 'text', text: JSON.stringify(structuredContent) }] });
    } else {
      response.writeHead(200, { 'Content-Type': 'application/json' })
        .end(JSON.stringify({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Unsupported fixture method' } }));
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    return await exercise(`http://127.0.0.1:${server.address().port}`, calls);
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}

for (const kind of ['sdk', 'inspector']) {
  test(`published ${kind} selects views and preserves real client channels against a synthetic peer`, async () => {
    await withSyntheticPeer(result => result, async (baseUrl, calls) => {
      const receipt = await runStockClientContract({ kind, baseUrl, apiKey: key, rosters });
      assert.equal(receipt.client, kind);
      assert.equal(receipt[kind === 'sdk' ? 'sdkVersion' : 'inspectorVersion'], kind === 'sdk' ? '1.32.1' : '2.10.1');
      assert.deepEqual(receipt.views, [
        { view: 'default', tools: 1, successes: 1, refusals: 0 },
        { view: 'setup', tools: 2, successes: 1, refusals: 1 },
      ]);
      assert.deepEqual(calls, [
        { view: 'default', name: 'read', args: {} },
        { view: 'setup', name: 'read', args: {} },
        { view: 'setup', name: 'write', args: {} },
      ]);
    });
  });

  test(`published ${kind} cannot hide wire-level text elision`, async () => {
    await withSyntheticPeer(result => ({ ...result, content: [{ type: 'text', text: 'elided' }] }), async baseUrl => {
      await assert.rejects(runStockClientContract({ kind, baseUrl, apiKey: key, rosters }), { message: 'missing-or-duplicate-payload-mirror' });
    });
  });
}

test('requires the exact published client version under the pinned lock', async () => {
  await assert.rejects(requirePublishedPackage('@modelcontextprotocol/inspector', '@modelcontextprotocol/inspector/package.json', '0.0.0'), { message: 'unexpected-installed-client' });
});

test('restricts credential-bearing clients to an owned loopback HTTP endpoint', () => {
  for (const base of ['https://localhost', 'http://example.com', 'http://user:password@localhost']) {
    assert.throws(() => ownedClientEndpoint(base, key), { message: 'invalid-owned-target' });
  }
});
