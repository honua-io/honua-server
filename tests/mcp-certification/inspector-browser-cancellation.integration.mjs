// Real public installed UI plus real PostGIS, against an AUTHORED CONTROL PEER.
// The peer issues pg_cancel_backend only after receiving the client's genuine
// notification. This validates the observer/UI path, never a Honua candidate.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createServer } from 'node:http';
import { execFile, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { promisify } from 'node:util';
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from './browser/node_modules/@playwright/test/index.mjs';
import { inspectorBrowserCancellation } from './inspector-browser-cancellation.mjs';
import { openInspectorPublicUi } from './inspector-public-ui.mjs';
import { observerConfiguration, PostgresCancellationObserver, until } from './postgres-cancellation-observer.mjs';
import { fixture, queryResult } from './test/cancellation-fixture.mjs';

const root = new URL('./node_modules/@modelcontextprotocol/inspector/', import.meta.url);
const installed = JSON.parse(await readFile(new URL('package.json', root)));
assert.equal(installed.version, '2.10.1');

for (const dropCancellation of [false, true]) {
  test(`public Inspector UI ${dropCancellation ? 'cannot earn cessation when its notification is dropped' : 'sends cancellation and independent observer proves PostgreSQL cessation'}`,
    { timeout: 60_000 }, async () => {
    const database = `mcp_ui_${randomUUID().replaceAll('-', '')}`;
    const config = observerConfiguration({ ...process.env, HONUA_MCP_OBSERVER_PGDATABASE: database });
    const observer = new PostgresCancellationObserver(config);
    const adminEnv = { ...observer.environment, PGDATABASE: 'postgres' };
    const admin = async (command, args) => {
      try { await promisify(execFile)(command, args, { env: adminEnv, timeout: 5_000 }); }
      catch { throw new Error('owned browser-control database command failed'); }
    };
    let created = false; let query; let queryExit; let queryPid; let cancellationReceived = false;
    let recoveryResult; let callCount = 0; let initialResponse;
    const peer = createServer(async (request, response) => {
      try {
        assert.equal(request.url, '/mcp');
        assert.equal(request.headers['x-api-key'], 'control-only');
        if (request.method !== 'POST') { response.writeHead(request.method === 'DELETE' ? 200 : 405).end(); return; }
        const chunks = []; for await (const chunk of request) chunks.push(chunk);
        const message = JSON.parse(Buffer.concat(chunks));
        if (message.method === 'notifications/cancelled') {
          // Any actual installed-client integer is valid; correlate to original.
          assert.equal(message.params.requestId, initialResponse.id);
          assert.equal(request.headers['mcp-session-id'], 'ui-control-session');
          cancellationReceived = true;
          await until(() => queryPid, 2_000);
          assert.equal(await observer.sql(`SELECT pg_cancel_backend(${queryPid})::text`), true);
          response.writeHead(202).end(); return;
        }
        if (!Object.hasOwn(message, 'id')) { response.writeHead(202).end(); return; }
        const reply = (result, error) => {
          response.writeHead(200, { 'Content-Type': 'application/json', 'Mcp-Session-Id': 'ui-control-session' });
          response.end(JSON.stringify({ jsonrpc: '2.0', id: message.id, ...(error ? { error } : { result }) }));
        };
        if (message.method === 'initialize') {
          reply({ protocolVersion: '2025-06-18', serverInfo: { name: 'authored-ui-control-peer', version: '1' },
            capabilities: { tools: {} } });
        } else if (message.method === 'tools/list') {
          reply({ tools: [{ name: 'honua_query_features', description: 'Authored control query', inputSchema: {
            type: 'object', properties: { serviceId: { type: 'string' }, layerId: { type: 'integer' },
              where: { type: 'string' }, outFields: { type: 'array', items: { type: 'string' } },
              outSrid: { type: 'integer' }, returnGeometry: { type: 'boolean' }, limit: { type: 'integer' } },
            required: ['serviceId', 'layerId', 'where'] } }] });
        } else if (message.method === 'tools/call') {
          assert.equal(message.params.name, 'honua_query_features'); callCount++;
          if (callCount > 1) { recoveryResult = queryResult(); reply(recoveryResult); return; }
          initialResponse = { id: message.id };
          const startedAt = new Date().toISOString();
          query = spawn('psql', [...observer.arguments, '--command', 'SELECT count(*) FROM honua.features'], {
            env: { ...observer.environment, PGAPPNAME: config.SERVER_APP_NAME, PGOPTIONS: '-c statement_timeout=20000' },
            stdio: ['ignore', 'ignore', 'ignore'],
          });
          queryExit = new Promise((resolve) => { query.once('error', () => resolve(-1)); query.once('close', resolve); });
          queryPid = await observer.blocked(startedAt);
          const exit = await queryExit;
          if (cancellationReceived && exit === 1) reply(undefined, { code: -32800, message: 'Request cancelled', data: { code: 'cancelled' } });
          else reply(queryResult());
        } else reply({});
      } catch { response.writeHead(500).end(); }
    });
    try {
      await admin('createdb', [database]); created = true;
      await observer.sql(`CREATE EXTENSION postgis; CREATE SCHEMA honua;
        CREATE TABLE honua.layers (layer_id integer, table_schema text, table_name text, primary_key_column text, geometry_column text);
        CREATE TABLE honua.service_layers (service_name text, layer_id integer);
        INSERT INTO honua.layers VALUES (0,'honua','features','objectid','geometry');
        INSERT INTO honua.service_layers VALUES ('test_service',0);
        CREATE TABLE honua.features (objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
          attributes jsonb NOT NULL, geometry geometry(Point,4326) NOT NULL);
        INSERT INTO honua.features VALUES
          (21,0,'{"name":"alpha","count":1}',ST_GeomFromText('POINT(-122.49 37.71)',4326)),
          (22,0,'{"name":"beta","count":2}',ST_GeomFromText('POINT(-122.475 37.72)',4326)); SELECT true::text`);
      await new Promise((resolve) => peer.listen(0, '127.0.0.1', resolve));
      const operation = inspectorBrowserCancellation({ endpoint: `http://127.0.0.1:${peer.address().port}/mcp`,
        inspectorVersion: installed.version, sourceSha: fixture.sourceSha, fixture, observer, dropCancellation,
        openUi: (endpoint) => openInspectorPublicUi({ endpoint, chromium,
          inspectorEntry: new URL('clients/launcher/build/index.js', root).pathname, headers: { 'X-API-Key': 'control-only' },
          onControlFailure: async (stage, page, error) => {
            const snapshot = await page.locator('body').ariaSnapshot();
            await writeFile(`artifacts/inspector-browser-controls/ui-${dropCancellation ? 'drop' : 'positive'}.txt`,
              `${stage}\n${String(error).slice(0, 8_000)}\n${snapshot.slice(0, 32_000)}`);
          } }),
        verifyRecovery: async (actualResult) => { assert.deepEqual(actualResult, queryResult()); assert.deepEqual(recoveryResult, queryResult()); },
      });
      if (dropCancellation) {
        await assert.rejects(operation, /rejected dropped notification with blocked provider/);
        assert.equal(cancellationReceived, false);
        assert.ok(queryPid > 0, 'negative must reach actual PostgreSQL blocked work');
      } else {
        const proof = await operation;
        assert.equal(proof.protocolVersion, '2025-06-18');
        assert.equal(proof.protocolEra, 'legacy');
        assert.equal(proof.genuineNotificationObserved, true);
        assert.equal(proof.localUiCancellationObserved, true);
        assert.equal(proof.originalUpstreamKeptOpen, true);
        assert.equal(proof.provider.ceasedBeforeBlockerRelease, true);
        assert.equal(proof.clientAbortUsed, true);
        assert.equal(cancellationReceived, true);
        assert.equal(callCount, 2);
        assert.ok(Object.values(proof.cleanup).every((value) => value === true));
      }
    } finally {
      const errors = [];
      try { await observer.release(); } catch (error) { errors.push(error); }
      try {
        if (query && query.exitCode === null && query.signalCode === null) { query.kill('SIGTERM'); await queryExit; }
      } catch (error) { errors.push(error); }
      peer.closeAllConnections(); await new Promise((resolve) => peer.close(resolve));
      try { if (created) await admin('dropdb', ['--force', database]); } catch (error) { errors.push(error); }
      if (errors.length) throw new AggregateError(errors, 'owned browser-control cleanup failed');
    }
  });
}
