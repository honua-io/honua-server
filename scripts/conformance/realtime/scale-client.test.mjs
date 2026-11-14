import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import test from 'node:test';
import { runScale } from './scale-client.mjs';

async function twoTasks(mode = 'normal') {
  const streams = new Set();
  const servers = [];
  const writes = [0, 0];
  let generation = 0;
  const targets = [];
  for (let task = 0; task < 2; task++) {
    const server = createServer(async (request, response) => {
      if (request.headers['x-api-key'] !== 'test-key') {
        response.writeHead(401).end();
        return;
      }
      if (request.method === 'GET') {
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        response.write(': ready\n\n');
        streams.add(response);
        response.on('close', () => streams.delete(response));
        return;
      }
      writes[task]++;
      let body = '';
      for await (const chunk of request) body += chunk;
      const name = JSON.parse(body).adds[0].attributes.name;
      const cursor = ++generation;
      const frame = `event: feature-change\nid: features:${cursor}\ndata: ${JSON.stringify({ cursor, attributes: { name } })}\n\n`;
      for (const stream of streams) {
        if (mode === 'disconnect' && cursor === 1) { stream.end(); continue; }
        if (mode === 'loss' && cursor === 1) continue;
        if (mode === 'reset' && cursor === 1) stream.write('event: reset\ndata: {"reason":"retention"}\n\n');
        else {
          stream.write(frame);
          if (mode === 'duplicate' && cursor === 1) stream.write(frame);
          if (mode === 'repeated-edit' && cursor === 1) {
            stream.write(frame.replace('features:1', 'features:2').replace('"cursor":1', '"cursor":2'));
          }
          if (mode === 'authorization' && cursor === 1) {
            stream.write('event: status\ndata: {"status":"error","reason":"access-revoked"}\n\n');
          }
          if (mode === 'reorder' && cursor === 2) stream.write(frame.replaceAll('features:2', 'features:1').replace('"cursor":2', '"cursor":1'));
        }
      }
      response.writeHead(mode === 'edit-failure' ? 500 : 200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ addResults: [{ success: mode !== 'edit-failure', objectId: cursor }] }));
    });
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    servers.push(server);
    targets.push({ url: `http://127.0.0.1:${server.address().port}`, headers: { 'X-API-Key': 'test-key' } });
  }
  return {
    targets, writes,
    close: async () => {
      for (const stream of streams) stream.destroy();
      for (const server of servers) server.closeAllConnections();
      await Promise.all(servers.map(server => new Promise(resolve => server.close(resolve))));
    },
  };
}

async function withTasks(mode, action) {
  const tasks = await twoTasks(mode);
  try { await action(tasks); }
  finally { await tasks.close(); }
}

function profile(tasks) {
  return { targets: tasks.targets, subscribers: 4, editsPerSecond: 4, seconds: 1, settleMilliseconds: 100 };
}

test('two task subscribers each receive every alternating edit and retain missing gate evidence', async () => {
  await withTasks('normal', async tasks => {
    const result = await runScale(profile(tasks));
    assert.equal(result.edits, 4);
    assert.equal(result.deliveries, 16);
    assert.deepEqual(tasks.writes, [2, 2]);
    assert.equal(result.deliveryPassed, true);
    assert.equal(result.scaleGatePassed, false);
    assert.equal(result.missingEvidence.length, 3);
    assert.ok(result.requestToEventP95Milliseconds >= 0);
    assert.ok(result.maximumScheduleLagMilliseconds >= 0);
    assert.ok(result.clientCpuCoreEquivalentPercent > 0);
    assert.ok(result.clientEventLoopP95Milliseconds >= 0);
    assert.ok(!JSON.stringify(result).includes('test-key'));
  });
});

test('zero subscriber baseline still writes through both tasks', async () => {
  await withTasks('normal', async tasks => {
    const result = await runScale({ ...profile(tasks), subscribers: 0 });
    assert.equal(result.deliveries, 0);
    assert.equal(result.requestToEventP95Milliseconds, null);
    assert.deepEqual(tasks.writes, [2, 2]);
  });
});

for (const [mode, error] of [
  ['duplicate', /Duplicate or out-of-order/],
  ['repeated-edit', /Duplicate or out-of-order/],
  ['authorization', /authorization or transport ended/],
  ['reorder', /Duplicate or out-of-order/],
  ['reset', /received reset/],
  ['loss', /Delivery settle timeout/],
  ['edit-failure', /Edit failed: HTTP 500/],
  ['disconnect', /Stream ended after/],
]) {
  test(`rejects ${mode} instead of reporting a delivery pass`, async () => {
    await withTasks(mode, async tasks => {
      await assert.rejects(runScale(profile(tasks)), error);
    });
  });
}

test('rejects a single origin masquerading as two tasks before making requests', async () => {
  await assert.rejects(runScale({
    targets: [{ url: 'http://127.0.0.1:1' }, { url: 'http://127.0.0.1:1/other' }],
  }), /distinct origins/);
});
