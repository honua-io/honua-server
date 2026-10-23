import { spawn } from "node:child_process";
import { createHash } from "node:crypto";

const require = (condition, message) => { if (!condition) throw new Error(message); };
const hash = (value) => `sha256:${createHash("sha256").update(value).digest("hex")}`;
const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

export class PostgresObservationError extends Error {
  constructor(code) {
    const safe = /^[0-9A-Z]{5}$/.test(code) || ["COMMAND_FAILED", "INVALID_JSON", "FIXTURE_ROWS", "FIXTURE_BINDING"].includes(code);
    super("Postgres cancellation observation failed");
    this.observationCode = safe ? code : "COMMAND_FAILED";
  }
}

export async function until(check, milliseconds = 8_000) {
  const deadline = performance.now() + milliseconds;
  do {
    const value = await check();
    if (value) return value;
    await delay(50);
  } while (performance.now() < deadline);
  throw new Error("Postgres cancellation observation exceeded its bounded budget");
}

export function observerConfiguration(environment) {
  const config = Object.fromEntries(["HOST", "PORT", "DATABASE", "USER", "PASSWORD", "SERVER_APP_NAME"]
    .map((name) => [name, environment[`HONUA_MCP_OBSERVER_PG${name}`]]));
  require(Object.values(config).every((value) => typeof value === "string" && value.length > 0), "Postgres observer inputs are incomplete");
  require(["localhost", "127.0.0.1", "::1"].includes(config.HOST), "cancellation observer requires the owned loopback database fixture");
  require(/^\d+$/.test(config.PORT) && Number(config.PORT) > 0 && Number(config.PORT) <= 65535, "invalid observer port");
  require(/^[A-Za-z0-9_-]{1,63}$/.test(config.SERVER_APP_NAME), "invalid observer server application binding");
  return config;
}

// A separate psql connection owns the blocker. Neither SDK transport nor a
// server-reported acknowledgement supplies the provider-cessation observation.
export class PostgresCancellationObserver {
  constructor(config) {
    this.config = config;
    this.arguments = ["--no-psqlrc", "--quiet", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1",
      "--set", "VERBOSITY=sqlstate"];
    this.environment = { PATH: process.env.PATH, LANG: "C.UTF-8", PGHOST: config.HOST, PGPORT: config.PORT,
      PGDATABASE: config.DATABASE, PGUSER: config.USER, PGPASSWORD: config.PASSWORD, PGCONNECT_TIMEOUT: "3",
      PGAPPNAME: "honua-mcp-cert-observer", PGOPTIONS: "-c statement_timeout=2500" };
    this.blocker = null;
    this.released = false;
  }

  async sql(statement) {
    const child = spawn("psql", [...this.arguments, "--command", statement], { env: this.environment });
    let output = "";
    let exceeded = false;
    let sqlstate;
    const stop = () => {
      child.kill("SIGTERM");
      setTimeout(() => { if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL"); }, 250).unref();
    };
    const timer = setTimeout(stop, 3_000);
    try {
      return await new Promise((resolve, reject) => {
        child.stdout.on("data", (bytes) => {
          output += bytes;
          if (output.length > 131_072) { exceeded = true; stop(); }
        });
        child.stderr.on("data", (bytes) => {
          // Retain only a PostgreSQL SQLSTATE, never raw connection/credential diagnostics.
          sqlstate ??= bytes.toString().match(/ERROR:\s+([0-9A-Z]{5})\b/)?.[1];
        });
        child.on("error", () => reject(new Error("Postgres observer could not start")));
        child.on("close", (code) => {
          if (code !== 0 || exceeded) { reject(new PostgresObservationError(sqlstate ?? "COMMAND_FAILED")); return; }
          try { resolve(JSON.parse(output.trim())); }
          catch { reject(new PostgresObservationError("INVALID_JSON")); }
        });
      });
    } finally { clearTimeout(timer); }
  }

  async snapshot(fixture) {
    if (!this.relation) await this.bind(fixture);
    const rows = await this.sql(`SELECT COALESCE(jsonb_agg(jsonb_build_object(
      'id', objectid, 'attributes', attributes, 'geometry', ST_AsGeoJSON(geometry)::jsonb,
      'srid', ST_SRID(geometry)) ORDER BY objectid), '[]'::jsonb)::text FROM ${this.relation} WHERE layer_id = 0`);
    require(Array.isArray(rows), "observer fixture snapshot is malformed");
    for (const expected of fixture.expected) {
      const found = rows.filter((row) => row.id === expected.id && row.attributes?.name === expected.name);
      const matches = found.length === 1 && found[0].attributes.count === expected.count && found[0].srid === 4326
        && found[0].geometry?.type === "Point" && found[0].geometry.coordinates?.length === 2
        && found[0].geometry.coordinates.every((value, index) => Math.abs(value - expected.coordinates[index]) < 1e-8);
      if (!matches) throw new PostgresObservationError("FIXTURE_ROWS");
    }
    return hash(JSON.stringify(rows));
  }

  async bind(fixture) {
    if (fixture.serviceName !== "test_service" || fixture.layerId !== 0) throw new PostgresObservationError("FIXTURE_BINDING");
    const bindings = await this.sql(`SELECT COALESCE(json_agg(json_build_object(
      'schema', COALESCE(NULLIF(l.table_schema,''),'public'), 'table', l.table_name,
      'primaryKey', l.primary_key_column, 'geometry', l.geometry_column)), '[]'::json)
      FROM honua.layers l JOIN honua.service_layers sl ON sl.layer_id = l.layer_id
      WHERE sl.service_name = 'test_service' AND l.layer_id = 0`);
    const binding = bindings?.[0];
    if (!Array.isArray(bindings) || bindings.length !== 1 || !["public", "honua"].includes(binding?.schema)
      || binding.table !== "features" || binding.primaryKey !== "objectid" || binding.geometry !== "geometry") {
      throw new PostgresObservationError("FIXTURE_BINDING");
    }
    this.tableSchema = binding.schema;
    this.relation = `"${binding.schema}"."features"`;
  }

  async hold() {
    require(this.relation, "observer requires an independently bound fixture relation");
    require(!this.blocker, "observer blocker is already allocated");
    const child = spawn("psql", this.arguments, { env: this.environment });
    this.blocker = child;
    let output = "";
    this.blockerExit = new Promise((resolve) => {
      child.on("error", () => resolve(-1));
      child.on("close", (code) => resolve(code));
    });
    child.stderr.resume();
    child.stdout.on("data", (bytes) => { output += bytes; });
    child.stdin.on("error", () => {}); // Exit/error is checked independently below and on release.
    child.stdin.write(`SET idle_in_transaction_session_timeout = '30s';\nBEGIN;\nLOCK TABLE ${this.relation} IN ACCESS EXCLUSIVE MODE;\nSELECT json_build_object('held', true, 'pid', pg_backend_pid());\n`);
    const held = await until(() => {
      require(child.exitCode === null && child.signalCode === null, "observer blocker exited before holding its lock");
      require(output.length <= 4096, "observer blocker returned excessive output");
      if (!output.includes("\n")) return false;
      return JSON.parse(output.trim());
    }, 5_000);
    require(held.held === true && Number.isSafeInteger(held.pid) && held.pid > 0, "observer blocker identity is invalid");
    this.blockerPid = held.pid;
    return held.pid;
  }

  async blocked(startedAt) {
    const timestamp = Date.parse(startedAt);
    require(Number.isFinite(timestamp), "request start is invalid");
    return until(async () => {
      const pids = await this.sql(`SELECT COALESCE(json_agg(pid), '[]'::json) FROM pg_stat_activity
        WHERE application_name = '${this.config.SERVER_APP_NAME}' AND query_start >= to_timestamp(${timestamp} / 1000.0)
          AND wait_event_type = 'Lock' AND ${this.blockerPid} = ANY(pg_blocking_pids(pid)) AND query ILIKE '%features%'`);
      require(Array.isArray(pids) && pids.length <= 1, "blocked provider work is ambiguous");
      if (!pids.length) return false;
      require(Number.isSafeInteger(pids[0]) && pids[0] > 0 && pids[0] !== this.blockerPid, "blocked provider PID is invalid");
      return pids[0];
    });
  }

  async state(pid) {
    require(Number.isSafeInteger(pid) && pid > 0 && Number.isSafeInteger(this.blockerPid), "observer provider identity is invalid");
    require(this.relation, "observer fixture relation is not bound");
    return this.sql(`SELECT json_build_object(
      'waiting', EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid = ${pid} AND ${this.blockerPid} = ANY(pg_blocking_pids(pid))),
      'relationLocks', (SELECT count(*) FROM pg_locks WHERE pid = ${pid} AND locktype = 'relation' AND relation = '${this.relation}'::regclass),
      'blockerHeld', EXISTS(SELECT 1 FROM pg_locks WHERE pid = ${this.blockerPid} AND relation = '${this.relation}'::regclass
        AND mode = 'AccessExclusiveLock' AND granted))`);
  }

  async release() {
    if (!this.blocker || this.released) return;
    const child = this.blocker;
    child.stdin.end("ROLLBACK;\n\\q\n");
    let timer;
    try {
      const exit = await Promise.race([this.blockerExit, new Promise((_, reject) => {
        timer = setTimeout(() => { child.kill("SIGTERM");
          setTimeout(() => { if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL"); }, 250).unref();
          reject(new Error("observer blocker cleanup exceeded its budget")); }, 3_000);
      })]);
      require(exit === 0, "observer blocker cleanup failed");
      this.released = true;
    } finally { clearTimeout(timer); }
  }
}
