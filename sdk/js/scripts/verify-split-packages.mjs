#!/usr/bin/env node

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(SCRIPT_DIR, "..");
const PACKAGES_ROOT = path.join(PROJECT_ROOT, "dist", "packages");

const packageDirs = {
  "@honua/sdk": path.join(PACKAGES_ROOT, "honua-sdk"),
  "@honua/sdk-esri-compat": path.join(PACKAGES_ROOT, "honua-sdk-esri-compat"),
  "@honua/honua-migrate": path.join(PACKAGES_ROOT, "honua-migrate"),
};

for (const [name, directory] of Object.entries(packageDirs)) {
  if (!fs.existsSync(directory)) {
    process.stderr.write(`Missing split package output for ${name}: ${directory}\n`);
    process.stderr.write('Run "npm run build:split-packages" first.\n');
    process.exit(1);
  }
}

const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "honua-split-verify-"));

try {
  const packageJson = {
    name: "honua-split-verify",
    private: true,
    type: "module",
    dependencies: {
      "@honua/sdk": `file:${packageDirs["@honua/sdk"]}`,
      "@honua/sdk-esri-compat": `file:${packageDirs["@honua/sdk-esri-compat"]}`,
      "@honua/honua-migrate": `file:${packageDirs["@honua/honua-migrate"]}`,
    },
  };
  fs.writeFileSync(
    path.join(tempRoot, "package.json"),
    `${JSON.stringify(packageJson, null, 2)}\n`,
    "utf8",
  );

  const smokeScript = `
import { HonuaClient } from "@honua/sdk";
import {
  CompatEventBus,
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
  EsriRequestInterceptorRegistry,
  FeatureLayerCompat,
  GraphicsLayerCompat,
  GroupLayerCompat,
  LayerListCompat,
  LegendCompat,
  MapImageLayerCompat,
  MapViewCompat,
  PopupCompat,
  TileLayerCompat,
} from "@honua/sdk-esri-compat";
import {
  buildJsMigrationReport,
  runEsriCompatCodemod,
  runLayerReconciliation,
  scanArcGisUsage,
} from "@honua/honua-migrate";

if (typeof HonuaClient !== "function") throw new Error("HonuaClient export missing");
if (typeof CompatEventBus !== "function") throw new Error("CompatEventBus export missing");
if (typeof createEsriRequestInterceptors !== "function") throw new Error("createEsriRequestInterceptors export missing");
if (typeof createArcGisTokenInterceptor !== "function") throw new Error("createArcGisTokenInterceptor export missing");
if (typeof EsriRequestInterceptorRegistry !== "function") throw new Error("EsriRequestInterceptorRegistry export missing");
if (typeof FeatureLayerCompat !== "function") throw new Error("FeatureLayerCompat export missing");
if (typeof GraphicsLayerCompat !== "function") throw new Error("GraphicsLayerCompat export missing");
if (typeof GroupLayerCompat !== "function") throw new Error("GroupLayerCompat export missing");
if (typeof LayerListCompat !== "function") throw new Error("LayerListCompat export missing");
if (typeof LegendCompat !== "function") throw new Error("LegendCompat export missing");
if (typeof MapImageLayerCompat !== "function") throw new Error("MapImageLayerCompat export missing");
if (typeof MapViewCompat !== "function") throw new Error("MapViewCompat export missing");
if (typeof PopupCompat !== "function") throw new Error("PopupCompat export missing");
if (typeof TileLayerCompat !== "function") throw new Error("TileLayerCompat export missing");
if (typeof scanArcGisUsage !== "function") throw new Error("scanArcGisUsage export missing");
if (typeof runEsriCompatCodemod !== "function") throw new Error("runEsriCompatCodemod export missing");
if (typeof buildJsMigrationReport !== "function") throw new Error("buildJsMigrationReport export missing");
if (typeof runLayerReconciliation !== "function") throw new Error("runLayerReconciliation export missing");

console.log("splitPackageSmoke=ok");
`.trimStart();
  fs.writeFileSync(path.join(tempRoot, "smoke.mjs"), smokeScript, "utf8");

  runCommand("npm", ["install", "--ignore-scripts", "--no-package-lock", "--silent"], tempRoot);
  const smokeResult = runCommand("node", ["smoke.mjs"], tempRoot);
  process.stdout.write(smokeResult.stdout);
} finally {
  fs.rmSync(tempRoot, { recursive: true, force: true });
}

function runCommand(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: "utf8",
    env: process.env,
  });

  if (result.status !== 0) {
    if (result.stdout) {
      process.stdout.write(result.stdout);
    }
    if (result.stderr) {
      process.stderr.write(result.stderr);
    }
    process.exit(result.status ?? 1);
  }

  return result;
}
