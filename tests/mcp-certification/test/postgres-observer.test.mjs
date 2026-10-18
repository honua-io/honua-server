import assert from "node:assert/strict";
import test from "node:test";
import { observerConfiguration, PostgresCancellationObserver, PostgresObservationError } from "../postgres-cancellation-observer.mjs";
import { fixture } from "./cancellation-fixture.mjs";

test("physical binding accepts only the exact authored relation and refuses ambiguous or injected metadata", async () => {
  const expected = { schema: "honua", table: "features", primaryKey: "objectid", geometry: "geometry" };
  for (const bindings of [[], [expected, expected], [{ ...expected, schema: 'public";DROP TABLE x;--' }],
    [{ ...expected, schema: "other_fixture" }], [{ ...expected, table: "unrelated" }],
    [{ ...expected, primaryKey: "other_id" }], [{ ...expected, geometry: "other_geom" }]]) {
    const observer = new PostgresCancellationObserver({});
    observer.sql = async () => bindings;
    await assert.rejects(observer.bind(fixture), (error) => error.observationCode === "FIXTURE_BINDING");
    assert.equal(observer.relation, undefined);
  }
  const observer = new PostgresCancellationObserver({}); observer.sql = async () => [expected];
  await observer.bind(fixture);
  assert.equal(observer.relation, '"honua"."features"');
});

test("observer is restricted to an explicit owned loopback database and a safe application binding", () => {
  const environment = Object.fromEntries(Object.entries({ HOST: "127.0.0.1", PORT: "5432", DATABASE: "control",
    USER: "control", PASSWORD: "synthetic-secret", SERVER_APP_NAME: "honua-cert-control" })
    .map(([key, value]) => [`HONUA_MCP_OBSERVER_PG${key}`, value]));
  assert.equal(observerConfiguration(environment).HOST, "127.0.0.1");
  for (const [name, value] of [["HOST", "production.example.com"], ["PORT", "0"], ["PORT", "65536"],
    ["PORT", "5432;select"], ["SERVER_APP_NAME", "x' OR true--"], ["PASSWORD", ""]]) {
    assert.throws(() => observerConfiguration({ ...environment, [`HONUA_MCP_OBSERVER_PG${name}`]: value }));
  }
});

test("observer diagnostics preserve only bounded SQLSTATE or fixed fixture codes", () => {
  assert.equal(new PostgresObservationError("42P01").observationCode, "42P01");
  assert.equal(new PostgresObservationError("FIXTURE_ROWS").observationCode, "FIXTURE_ROWS");
  const error = new PostgresObservationError("password=synthetic-secret");
  assert.equal(error.observationCode, "COMMAND_FAILED");
  assert.equal(JSON.stringify(error).includes("synthetic-secret"), false);
  assert.equal(error.message.includes("synthetic-secret"), false);
});
