#!/usr/bin/env node

import { spawnSync } from "node:child_process";

function env(name, fallback = "") {
  const value = process.env[name];
  if (typeof value !== "string") {
    return fallback;
  }
  return value.trim();
}

const requiredEnvVars = [
  "HONUA_DEMO_TARGET_BASE_URL",
  "HONUA_DEMO_ADMIN_API_KEY",
  "HONUA_DEMO_SOURCE_SERVICE_URL",
  "HONUA_DEMO_TABLE_NAME",
];

const missing = requiredEnvVars.filter((name) => env(name) === "");
if (missing.length > 0) {
  process.stdout.write(`liveDemoSkipped=yes missing=${missing.join(",")}\n`);
  process.exit(0);
}

const fixture = env("HONUA_DEMO_FIXTURE", "esri-real-sample-incident-command-app");
const fixturesRoot = env("HONUA_DEMO_FIXTURES_ROOT", "test/fixtures");
const outputDir = env("HONUA_DEMO_OUTPUT_DIR", ".tmp/migration-demo-live");
const codemodTarget = env("HONUA_DEMO_CODEMOD_TARGET", "honua-compat");
const targetBaseUrl = env("HONUA_DEMO_TARGET_BASE_URL");
const sourceServiceUrl = env("HONUA_DEMO_SOURCE_SERVICE_URL");
const tableName = env("HONUA_DEMO_TABLE_NAME");
const reportPath = env("HONUA_DEMO_REPORT_PATH", "reports/demo-incident-live-report.json");
const sampleSize = env("HONUA_DEMO_SAMPLE_SIZE", "200");
const timeoutSeconds = env("HONUA_DEMO_TIMEOUT_SECONDS", "900");

const args = [
  "dist/src/migration/cli.js",
  "demo",
  "--fixture",
  fixture,
  "--fixtures-root",
  fixturesRoot,
  "--output-dir",
  outputDir,
  "--target",
  codemodTarget,
  "--admin-base-url",
  targetBaseUrl,
  "--admin-api-key",
  env("HONUA_DEMO_ADMIN_API_KEY"),
  "--source-service-url",
  sourceServiceUrl,
  "--table-name",
  tableName,
  "--target-base-url",
  targetBaseUrl,
  "--sample-size",
  sampleSize,
  "--timeout-seconds",
  timeoutSeconds,
  "--report",
  reportPath,
];

const maybeLayerId = env("HONUA_DEMO_LAYER_ID");
if (maybeLayerId !== "") {
  args.push("--layer-id", maybeLayerId);
}

const maybeSourceBaseUrl = env("HONUA_DEMO_SOURCE_BASE_URL");
if (maybeSourceBaseUrl !== "") {
  args.push("--source-base-url", maybeSourceBaseUrl);
}

const maybeSourceServiceId = env("HONUA_DEMO_SOURCE_SERVICE_ID");
if (maybeSourceServiceId !== "") {
  args.push("--source-service-id", maybeSourceServiceId);
}

const maybeTargetServiceId = env("HONUA_DEMO_TARGET_SERVICE_ID");
if (maybeTargetServiceId !== "") {
  args.push("--target-service-id", maybeTargetServiceId);
}

process.stdout.write(`liveDemoRunning fixture=${fixture} target=${codemodTarget}\n`);

const result = spawnSync(process.execPath, args, {
  stdio: "inherit",
  env: process.env,
});

if (result.error) {
  process.stderr.write(
    `liveDemoError=${result.error instanceof Error ? result.error.message : String(result.error)}\n`,
  );
  process.exit(1);
}

process.exit(typeof result.status === "number" ? result.status : 1);
