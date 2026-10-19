// Observer negative controls, not installed-client or candidate observations.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createServer } from 'node:http';
import { gzipSync } from 'node:zlib';
import { cancellationWireObserver, queryArguments } from './inspector-browser-cancellation.mjs';
import { until } from './postgres-cancellation-observer.mjs';
import { fixture } from './test/cancellation-fixture.mjs';

const malformedEnvelopes = ['missing-jsonrpc', 'wrong-jsonrpc', 'numeric-jsonrpc', 'null-jsonrpc',
  'null-notification-id', 'numeric-notification-id', 'string-notification-id'];

async function exercise(mode) {
  let reply; let original; let reached = false; let notificationForwarded = 0;
  const peer = createServer(async (request, response) => {
    const chunks = []; for await (const chunk of request) chunks.push(chunk);
    const message = JSON.parse(Buffer.concat(chunks));
    if (message.method === 'initialize') {
      response.writeHead(200, { 'Content-Type': 'application/json', 'Mcp-Session-Id': mode === 'initialization-session' ? 'different-session' : 'control-session' })
        .end(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { protocolVersion: '2025-06-18' } }));
      return;
    }
    if (message.method === 'tools/call') {
      reached = true; original = response;
      reply = () => {
        const frame = { jsonrpc: '2.0', id: 7, error: { code: -32800, message: 'Request cancelled', data: { code: 'cancelled' } } };
        if (mode === 'wrong-terminal') frame.error.data.code = 'other';
        if (mode === 'missing-error-message') delete frame.error.message;
        if (mode === 'nonstring-error-message') frame.error.message = 7;
        if (mode === 'result-present') frame.result = {};
        let body = Buffer.from(mode === 'duplicate' ? `data: ${JSON.stringify(frame)}\n\ndata: ${JSON.stringify(frame)}\n\n`
          : mode === 'gzip-sse' ? `data: ${JSON.stringify(frame)}\n\n` : JSON.stringify(frame));
        const headers = { 'Content-Type': ['duplicate', 'gzip-sse'].includes(mode) ? 'text/event-stream' : 'application/json' };
        if (mode === 'gzip-sse') { body = gzipSync(body); headers['Content-Encoding'] = 'gzip'; }
        response.writeHead(200, headers).end(body);
      };
      return;
    }
    if (message.method === 'notifications/cancelled') notificationForwarded++;
    response.writeHead(202).end(); reply();
  });
  await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
  const wire = await cancellationWireObserver(`http://127.0.0.1:${peer.address().port}/mcp`, { dropCancellation: mode === 'dropped' });
  const abort = new AbortController();
  const post = (message, session = 'control-session', signal) => fetch(wire.endpoint, { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Mcp-Session-Id': session }, body: JSON.stringify(message), signal });
  let outcome; let originalSettled = false;
  try {
    if (mode !== 'no-initialization') await post({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {} });
    wire.arm(queryArguments(fixture));
    outcome = post({ jsonrpc: '2.0', id: 7, method: 'tools/call', params: { name: 'honua_query_features', arguments: queryArguments(fixture) } }, undefined, abort.signal)
      .then((response) => response.text()).catch(() => undefined).finally(() => { originalSettled = true; });
    await until(() => reached && wire.capture, 2_000);
    const observed = wire.capture;
    if (mode === 'local-abort') { abort.abort(); await outcome; }
    if (mode !== 'no-notification' && mode !== 'local-abort') {
      const notification = { jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: mode === 'wrong-id-type' ? '7' : 7 } };
      if (mode === 'missing-jsonrpc') delete notification.jsonrpc;
      if (mode === 'wrong-jsonrpc') notification.jsonrpc = '1.0';
      if (mode === 'numeric-jsonrpc') notification.jsonrpc = 2;
      if (mode === 'null-jsonrpc') notification.jsonrpc = null;
      if (mode === 'null-notification-id') notification.id = null;
      if (mode === 'numeric-notification-id') notification.id = 7;
      if (mode === 'string-notification-id') notification.id = '7';
      const response = await post(notification,
        mode === 'wrong-session' ? 'other-session' : 'control-session');
      if (malformedEnvelopes.includes(mode)) {
        assert.equal(response.status, 502, 'malformed cancellation must be refused');
        await new Promise((resolve) => setTimeout(resolve, 25));
        assert.equal(notificationForwarded, 0, 'malformed cancellation must not reach the peer');
        assert.equal(originalSettled, false, 'original request must remain pending until cleanup');
        assert.equal(original.destroyed, false);
        assert.equal(observed.complete, false);
        assert.equal(observed.aborted, false);
        assert.equal(observed.notification, false);
        assert.equal(observed.upstreamOpenAtNotification, false);
        assert.deepEqual(observed.responses, []);
      }
    }
    if (['no-notification', 'dropped'].includes(mode)) reply();
    if (mode !== 'local-abort' && !['wrong-session', 'wrong-id-type', ...malformedEnvelopes].includes(mode)) await outcome;
    if (['json', 'gzip-sse'].includes(mode)) {
      assert.equal(wire.assertTerminal().id, 7);
    } else assert.throws(() => wire.assertTerminal(), /Inspector cancellation proof failed/);
  } finally {
    abort.abort(); original?.destroy(); await wire.close();
    peer.closeAllConnections(); await new Promise((resolve) => peer.close(resolve));
    await outcome;
  }
}

for (const mode of ['json', 'gzip-sse', 'no-initialization', 'initialization-session', 'wrong-terminal', 'missing-error-message', 'nonstring-error-message', 'result-present', 'duplicate', 'no-notification',
  'wrong-id-type', 'wrong-session', 'local-abort', 'dropped', ...malformedEnvelopes]) {
  test(`independent cancellation observer ${mode}`, { timeout: 5_000 }, () => exercise(mode));
}
