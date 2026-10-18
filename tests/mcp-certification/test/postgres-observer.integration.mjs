// Harness-only integration control in an owned disposable database. Cancellation
// is issued by SQL here, not MCP: this proves the independent observer, never a
// candidate's request-cancellation implementation.
import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { promisify } from "node:util";
import { observerConfiguration, PostgresCancellationObserver, until } from "../postgres-cancellation-observer.mjs";
import { fixture } from "./cancellation-fixture.mjs";

test("real PostGIS observer distinguishes blocked work from cessation before blocker release", async () => {
  const database = `mcp_observer_${randomUUID().replaceAll("-", "")}`;
  const config = observerConfiguration({ ...process.env, HONUA_MCP_OBSERVER_PGDATABASE: database });
  const observer = new PostgresCancellationObserver(config);
  const adminEnv = { ...observer.environment, PGDATABASE: "postgres" };
  const command = async (name, args) => {
    try { return await promisify(execFile)(name, args, { env: adminEnv, timeout: 5_000 }); }
    catch { throw new Error(`owned observer database ${name} failed`); }
  };
  let created = false;
  let query;
  let queryExit;
  try {
    await command("createdb", [database]); created = true;
    await observer.sql(`CREATE EXTENSION postgis;
      CREATE SCHEMA honua;
      CREATE TABLE honua.layers (layer_id integer, table_schema text, table_name text, primary_key_column text, geometry_column text);
      CREATE TABLE honua.service_layers (service_name text, layer_id integer);
      INSERT INTO honua.layers VALUES (0,'honua','features','objectid','geometry');
      INSERT INTO honua.service_layers VALUES ('test_service',0);
      CREATE TABLE honua.features (objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
        attributes jsonb NOT NULL, geometry geometry(Point,4326) NOT NULL);
      INSERT INTO honua.features VALUES
        (21,0,'{"name":"alpha","count":1}',ST_GeomFromText('POINT(-122.49 37.71)',4326)),
        (22,0,'{"name":"beta","count":2}',ST_GeomFromText('POINT(-122.475 37.72)',4326));
      CREATE TABLE public.features (LIKE honua.features INCLUDING ALL);
      INSERT INTO public.features VALUES (99,0,'{"name":"decoy","count":0}',ST_GeomFromText('POINT(0 0)',4326));
      SELECT true::text`);
    const before = await observer.snapshot(fixture);
    assert.equal(observer.tableSchema, "honua");
    await observer.hold();
    const startedAt = new Date().toISOString();
    query = spawn("psql", [...observer.arguments, "--command", "SELECT count(*) FROM honua.features"], {
      env: { ...observer.environment, PGAPPNAME: config.SERVER_APP_NAME, PGOPTIONS: "-c statement_timeout=15000" },
      stdio: ["ignore", "ignore", "ignore"],
    });
    queryExit = new Promise((resolve) => { query.on("error", () => resolve(-1)); query.on("close", resolve); });
    const pid = await observer.blocked(startedAt);
    const blocked = await observer.state(pid);
    assert.equal(blocked.waiting, true); assert.equal(blocked.blockerHeld, true); assert.ok(blocked.relationLocks > 0);
    assert.equal(await observer.sql(`SELECT pg_cancel_backend(${pid})::text`), true);
    assert.equal(await queryExit, 1); // PostgreSQL error response, never a local child kill.
    await until(async () => {
      const state = await observer.state(pid);
      assert.equal(state.blockerHeld, true);
      return state.waiting === false && state.relationLocks === 0;
    });
    assert.equal(observer.released, false);
    await observer.release();
    assert.equal(await observer.snapshot(fixture), before);
    await observer.sql("UPDATE honua.features SET attributes = jsonb_set(attributes, '{count}', '99') WHERE objectid = 21; SELECT true::text");
    await assert.rejects(observer.snapshot(fixture), (error) => error.observationCode === "FIXTURE_ROWS");
    await observer.sql("UPDATE honua.layers SET table_schema = 'public'; SELECT true::text");
    const decoy = new PostgresCancellationObserver(config);
    await assert.rejects(decoy.snapshot(fixture), (error) => error.observationCode === "FIXTURE_ROWS");
  } finally {
    const cleanupErrors = [];
    try { await observer.release(); } catch (error) { cleanupErrors.push(error); }
    try {
      if (query && query.exitCode === null && query.signalCode === null) { query.kill("SIGTERM"); await queryExit; }
    } catch (error) { cleanupErrors.push(error); }
    try { if (created) await command("dropdb", ["--force", database]); }
    catch (error) { cleanupErrors.push(error); }
    if (cleanupErrors.length) throw new AggregateError(cleanupErrors, "owned observer integration cleanup failed");
  }
});
