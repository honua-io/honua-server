// Scripted wire controls exercise the installed SDK, not a Honua candidate.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { cancelDurableJob } from './cancel-durable-job.mjs';

async function run(mode) {
  const calls = [];
  const ack = { jobId: 'job-1', status: 'cancellation_requested', cancellationRequested: true };
  const server = createServer(async (request, response) => {
    if (request.method !== 'POST') { response.writeHead(200).end(); return; }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    calls.push({ body, apiKey: request.headers['x-api-key'], session: request.headers['mcp-session-id'] });
    if (!('id' in body)) { response.writeHead(202).end(); return; }
    let result;
    if (body.method === 'initialize') result = { protocolVersion: '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'scripted-gp-control', version: '1' } };
    else if (body.method === 'tools/list') result = { tools: mode === 'missing-tool' ? [] : [{ name: 'honua_cancel_job', inputSchema: { type: 'object', required: ['jobId'], properties: { jobId: { type: 'string' } } } }],
      ...(mode === 'cursor-loop' ? { nextCursor: 'repeat' } : {}) };
    else {
      let value = { ...ack };
      if (mode === 'wrong-job') value.jobId = 'different-job';
      if (mode === 'false-ack') value.cancellationRequested = false;
      if (mode === 'completed') value.status = 'successful';
      if (mode === 'output') value.results = { output: 'unexpected' };
      result = { content: [{ type: 'text', text: JSON.stringify(mode === 'contradictory-text' ? { ...value, jobId: 'wrong' } : value) }],
        structuredContent: value, ...(mode === 'tool-error' ? { isError: true } : mode === 'nonboolean-error' ? { isError: 'false' } : {}) };
      if (mode === 'empty') result = { content: [] };
    }
    response.writeHead(200, { 'Content-Type': 'application/json', 'Mcp-Session-Id': 'gp-control-session' });
    response.end(JSON.stringify({ jsonrpc: '2.0', id: body.id, result }));
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    try {
      return { result: await cancelDurableJob({ baseUrl: `http://127.0.0.1:${server.address().port}`, apiKey: 'control-key', jobId: 'job-1' }), calls };
    } catch (error) { return { error, calls }; }
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  }
}

test('actual installed SDK discovers and repeats durable cancellation, declaring acknowledgement only', async () => {
  const { result, error, calls } = await run('positive');
  assert.equal(error, undefined, error?.message);
  assert.equal(result.executionCessationProven, false);
  assert.equal(result.sdkVersion, '1.32.1');
  assert.match(result.adapterSha256, /^[a-f0-9]{64}$/);
  assert.equal(result.responses.length, 2);
  const requests = calls.filter(({ body }) => body.method === 'tools/call');
  assert.equal(requests.length, 2);
  assert.ok(requests.every(({ body, apiKey, session }) => body.params.name === 'honua_cancel_job'
    && body.params.arguments.jobId === 'job-1' && apiKey === 'control-key' && session === 'gp-control-session'));
});

for (const mode of ['missing-tool', 'cursor-loop', 'wrong-job', 'false-ack', 'completed', 'output', 'contradictory-text', 'tool-error', 'nonboolean-error', 'empty']) {
  test(`installed SDK adapter rejects ${mode}`, async () => {
    const { error, calls } = await run(mode);
    assert.ok(error, 'malformed peer response must fail');
    assert.ok(calls.some(({ body }) => body.method === 'tools/list'), 'must reach actual SDK discovery');
    if (!['missing-tool', 'cursor-loop'].includes(mode)) {
      assert.ok(calls.some(({ body }) => body.method === 'tools/call'), 'must reach actual SDK cancellation');
    }
    const expected = mode === 'missing-tool' ? /missing-cancellation-tool/ : mode === 'cursor-loop' ? /incomplete-discovery/
      : mode === 'tool-error' ? /tool-error/ : mode === 'nonboolean-error' ? /isError|boolean|validation/i : /invalid-acknowledgement/;
    assert.match(error.message, expected);
  });
}

test('owned loopback restriction rejects external credentials target before connection', async () => {
  await assert.rejects(cancelDurableJob({ baseUrl: 'https://external.invalid', apiKey: 'control-key', jobId: 'job-1' }), /invalid-owned-target/);
});
