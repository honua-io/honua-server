// Observe the public installed Inspector web UI. A local UI abort is never
// server-cessation evidence: require its genuine notification, an open upstream
// request, the typed server terminal response and independent PostgreSQL state.
import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { createHash } from 'node:crypto';
import { gunzipSync, inflateSync, brotliDecompressSync } from 'node:zlib';
import { until } from './postgres-cancellation-observer.mjs';

export const INSPECTOR_CANCELLATION_CONTRACT = 'honua.inspector-browser-cancellation-proof/v1';
const LIMIT = 4 * 1024 * 1024;
const hash = (value) => 'sha256:' + createHash('sha256').update(value).digest('hex');
const require = (condition, stage) => { if (!condition) throw new Error(`Inspector cancellation proof failed: ${stage}`); };
export const queryArguments = (fixture) => ({ serviceId: fixture.serviceName, layerId: fixture.layerId,
  where: "name IN ('alpha','beta') AND count >= 1", outFields: ['name', 'count'], outSrid: 4326,
  returnGeometry: true, limit: 3 });

function frames(bytes, headers) {
  const encoding = (headers['content-encoding'] ?? '').trim().toLowerCase();
  const decode = { gzip: gunzipSync, deflate: inflateSync, br: brotliDecompressSync }[encoding];
  require(!encoding || encoding === 'identity' || decode, 'response encoding');
  if (decode) bytes = decode(bytes, { maxOutputLength: LIMIT });
  require(bytes.length <= LIMIT, 'decoded response limit');
  const contentType = headers['content-type'] ?? '';
  if (contentType.startsWith('application/json')) return [JSON.parse(bytes)];
  require(contentType.startsWith('text/event-stream'), 'response type');
  return bytes.toString().split(/\r?\n\r?\n/).map((event) => event.split(/\r?\n/)
    .filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trimStart()).join('\n'))
    .filter(Boolean).map((data) => JSON.parse(data));
}

// Fixed target; unchanged bytes/headers; no redirects, generated cancellation,
// replacement terminal, or abort shielding. Fault injection is negative-only:
// a dropped notification can NEVER produce a positive proof.
export async function cancellationWireObserver(endpoint, { dropCancellation = false } = {}) {
  const target = new URL(endpoint);
  require(['http:', 'https:'].includes(target.protocol) && !target.username && !target.password
    && !target.search && !target.hash && target.pathname === '/mcp', 'target');
  const active = new Set();
  const calls = [];
  let armed = false;
  let invalid = false;
  let total = 0;
  let notifications = 0;
  let expected;
  let capture;
  let recovery;
  let initialization;
  const proxy = createServer(async (incoming, outgoing) => {
    let upstream;
    try {
      require(incoming.url === '/mcp' && ['GET', 'POST', 'DELETE'].includes(incoming.method), 'request target');
      const chunks = []; let size = 0;
      for await (const chunk of incoming) { require((size += chunk.length) <= LIMIT, 'request limit'); chunks.push(chunk); }
      const body = Buffer.concat(chunks);
      const message = incoming.method === 'POST' ? JSON.parse(body) : undefined;
      let initialize;
      if (message?.method === 'initialize') {
        require(!initialization, 'one actual UI initialization');
        initialize = initialization = { id: message.id, complete: false, aborted: false, responses: [] };
      }
      let initial = false;
      if (armed && message?.method === 'tools/call') {
        require(!capture && message.params?.name === 'honua_query_features'
          && JSON.stringify(message.params.arguments) === JSON.stringify(expected), 'authored query arguments');
        require(Number.isSafeInteger(message.id) && message.id >= 0
          && typeof incoming.headers['mcp-session-id'] === 'string' && incoming.headers['mcp-session-id'].length > 0, 'stateful typed request');
        capture = { id: message.id, session: incoming.headers['mcp-session-id'], startedAt: new Date().toISOString(),
          complete: false, aborted: false, responses: [], notification: false, upstreamOpenAtNotification: false };
        calls.push(capture); armed = false; initial = true;
      }
      let call;
      if (message?.method === 'tools/call' && capture) {
        if (message.id === capture.id) { require(initial, 'unique original request'); call = capture; }
        else {
          require(capture.complete && !recovery && message.params?.name === 'honua_query_features'
            && Number.isSafeInteger(message.id) && message.id >= 0
            && incoming.headers['mcp-session-id'] === capture.session
            && JSON.stringify(message.params.arguments) === JSON.stringify(expected), 'correlated recovery request');
          recovery = { id: message.id, complete: false, aborted: false, responses: [] };
          calls.push(recovery); call = recovery;
        }
      }
      const tracked = call ?? initialize;
      if (message?.method === 'notifications/cancelled') {
        notifications++;
        require(capture && message.params?.requestId === capture.id
          && incoming.headers['mcp-session-id'] === capture.session, 'genuine correlated notification');
        require(!capture.complete && !capture.aborted, 'original upstream still open');
        capture.notification = true; capture.upstreamOpenAtNotification = true;
        if (dropCancellation) { outgoing.writeHead(202).end(); return; }
      }
      const headers = { ...incoming.headers, host: target.host }; delete headers.connection;
      upstream = (target.protocol === 'https:' ? httpsRequest : httpRequest)(target, {
        method: incoming.method, headers, timeout: 25_000,
      }, (response) => {
        if (response.statusCode >= 300 && response.statusCode < 400) {
          invalid = true; response.destroy(); outgoing.writeHead(502).end(); return;
        }
        const bytes = []; let length = 0;
        outgoing.writeHead(response.statusCode, response.headers);
        response.on('data', (chunk) => {
          if (!tracked) return;
          if ((length += chunk.length) > LIMIT || (total += chunk.length) > 4 * LIMIT) {
            invalid = true; response.destroy(); return;
          }
          bytes.push(chunk);
        });
        response.on('end', () => {
          if (!tracked) return;
          try {
            require(response.statusCode === 200, 'terminal HTTP status');
            tracked.responses = frames(Buffer.concat(bytes), response.headers).filter((frame) => frame.id === tracked.id);
            if (initialize) {
              require(tracked.responses.length === 1 && tracked.responses[0].jsonrpc === '2.0'
                && !Object.hasOwn(tracked.responses[0], 'error')
                && /^\d{4}-\d{2}-\d{2}$/.test(tracked.responses[0].result?.protocolVersion)
                && typeof response.headers['mcp-session-id'] === 'string', 'actual UI protocol negotiation');
              initialize.protocolVersion = tracked.responses[0].result.protocolVersion;
              initialize.session = response.headers['mcp-session-id'];
            }
            tracked.complete = true;
          } catch { invalid = true; }
        });
        response.on('error', () => { invalid = true; if (tracked) tracked.aborted = true; outgoing.destroy(); });
        response.pipe(outgoing);
      });
      active.add(upstream);
      upstream.on('close', () => active.delete(upstream));
      upstream.on('timeout', () => upstream.destroy());
      upstream.on('error', () => { invalid = true; if (tracked) tracked.aborted = true; outgoing.destroy(); });
      outgoing.on('close', () => {
        if (tracked && !outgoing.writableFinished) tracked.aborted = true;
        upstream.destroy();
      });
      upstream.end(body);
    } catch {
      invalid = true; upstream?.destroy();
      if (!outgoing.headersSent) outgoing.writeHead(502).end(); else outgoing.destroy();
    }
  });
  await new Promise((resolve, reject) => { proxy.once('error', reject); proxy.listen(0, '127.0.0.1', resolve); });
  const deadline = setTimeout(() => { invalid = true; for (const request of active) request.destroy(); }, 40_000);
  return {
    endpoint: `http://127.0.0.1:${proxy.address().port}/mcp`,
    arm(arguments_) { require(!armed && !capture, 'single bounded query'); expected = arguments_; armed = true; },
    get capture() { require(!invalid, 'wire observation'); return capture; },
    assertTerminal() {
      require(!invalid && !dropCancellation && calls.length >= 1 && calls.length <= 2 && notifications === 1, 'complete notification history');
      const call = capture;
      require(initialization?.complete && !initialization.aborted && initialization.session === call?.session,
        'initialized session and negotiated protocol');
      require(call?.notification && call.upstreamOpenAtNotification && call.complete && !call.aborted
        && call.responses.length === 1, 'open original and exactly one terminal');
      const response = call.responses[0];
      require(response.jsonrpc === '2.0' && response.error?.code === -32800
        && response.error?.data?.code === 'cancelled' && !Object.hasOwn(response, 'result'), 'typed server cancellation');
      return { ...call, session: undefined, sessionSha256: hash(call.session), protocolVersion: initialization.protocolVersion };
    },
    async recoveryResult() {
      await until(() => { require(!invalid, 'recovery wire observation'); return recovery?.complete; }, 5_000);
      require(calls.length === 2 && !recovery.aborted && recovery.responses.length === 1, 'one actual recovery response');
      const response = recovery.responses[0];
      require(response.jsonrpc === '2.0' && !Object.hasOwn(response, 'error') && response.result
        && (response.result.isError === undefined || response.result.isError === false), 'successful recovery response');
      return response.result;
    },
    async close() {
      clearTimeout(deadline);
      for (const request of active) request.destroy();
      proxy.closeAllConnections();
      await new Promise((resolve) => proxy.close(resolve));
    },
  };
}

export async function inspectorBrowserCancellation({ endpoint, inspectorVersion, sourceSha, fixture, observer,
  openUi, verifyRecovery, dropCancellation = false }) {
  require(/^2\.\d+\.\d+$/.test(inspectorVersion) && /^[a-f0-9]{40}$/.test(sourceSha)
    && fixture.sourceSha === sourceSha && typeof openUi === 'function' && typeof verifyRecovery === 'function', 'source and client binding');
  const wire = await cancellationWireObserver(endpoint, { dropCancellation });
  let ui; let proof; let failure;
  try {
    const before = await observer.snapshot(fixture);
    ui = await openUi(wire.endpoint);
    const blockerPid = await observer.hold();
    wire.arm(queryArguments(fixture));
    await ui.execute(queryArguments(fixture));
    const call = await until(() => wire.capture, 3_000);
    const queryPid = await observer.blocked(call.startedAt);
    require(!call.complete && !call.aborted, 'original blocked request');
    await ui.cancel();
    await until(() => wire.capture?.notification, 2_000);
    await ui.localCancellationSettled();
    if (dropCancellation) {
      // A public Cancel click may finish locally. Observe the provider after
      // three monitor periods; that must still fail rather than earn cessation.
      await new Promise((resolve) => setTimeout(resolve, 650));
      const state = await observer.state(queryPid);
      const observed = wire.capture; // Also fails on malformed/failed wire activity.
      require(state.waiting && state.blockerHeld && observed.notification && observed.upstreamOpenAtNotification
        && !observed.complete && !observed.aborted, 'dropped genuine notification control remained blocked');
      throw new Error('Inspector cancellation proof rejected dropped notification with blocked provider');
    }
    await until(() => wire.capture?.complete, 5_000);
    const terminal = wire.assertTerminal();
    await until(async () => {
      const state = await observer.state(queryPid);
      require(state.blockerHeld, 'blocker held until independent cessation');
      return !state.waiting && state.relationLocks === 0;
    }, 5_000);
    await observer.release();
    const after = await observer.snapshot(fixture);
    require(before === after, 'unchanged authored rows');
    await ui.recover();
    await verifyRecovery(await wire.recoveryResult());
    wire.assertTerminal();
    proof = { schema: INSPECTOR_CANCELLATION_CONTRACT, performedBy: 'MCP Inspector', inspectorVersion,
      protocolVersion: terminal.protocolVersion, transport: 'streamable-http', protocolEra: 'legacy',
      fixtureSourceSha: sourceSha, seedSqlSha256: fixture.seedSqlSha256, allocatedIdsSha256: fixture.allocatedIdsSha256,
      requestMethod: 'tools/call', toolName: 'honua_query_features', serviceName: fixture.serviceName, layerId: fixture.layerId,
      sessionSha256: terminal.sessionSha256, argumentsSha256: hash(JSON.stringify(queryArguments(fixture))),
      requestId: terminal.id, notificationRequestId: terminal.id, responseId: terminal.id, responseCount: 1,
      notificationMethod: 'notifications/cancelled', protocolCode: -32800, code: 'cancelled', hasResult: false,
      publicUiAction: 'Execute Tool / Cancel', clientAbortUsed: true, genuineNotificationObserved: true,
      localUiCancellationObserved: true,
      originalUpstreamKeptOpen: true, recoveryReadMatchesFixture: true,
      provider: { kind: 'postgres-relation-lock', tableSchema: observer.tableSchema, tableName: 'features',
        blockerPid, queryPid, observedBlocked: true, ceasedBeforeBlockerRelease: true, relationLocksReleased: true,
        beforeRowsSha256: before, afterRowsSha256: after } };
  } catch (error) { failure = error; }
  finally {
    const errors = [];
    try { await observer.release(); } catch (error) { errors.push(error); }
    try { await ui?.close(); } catch (error) { errors.push(error); }
    try { await wire.close(); } catch (error) { errors.push(error); }
    if (errors.length) failure = new AggregateError([...(failure ? [failure] : []), ...errors], 'Inspector cancellation cleanup failed');
  }
  if (failure) throw failure;
  proof.cleanup = { blockerReleased: observer.released === true, uiClosed: true, wireClosed: true };
  require(proof.cleanup.blockerReleased, 'blocker cleanup');
  return proof;
}
