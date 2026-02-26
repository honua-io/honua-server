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
  AttributionCompat,
  BasemapToggleCompat,
  BasemapGalleryCompat,
  BookmarksCompat,
  CompassCompat,
  CompatEventBus,
  CoordinateConversionCompat,
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
  DirectionsCompat,
  EsriRequestInterceptorRegistry,
  EditorCompat,
  FeatureCompat,
  FeatureFormCompat,
  FeatureTemplatesCompat,
  ExpandCompat,
  FeatureLayerCompat,
  FeatureTableCompat,
  FullscreenCompat,
  GraphicsLayerCompat,
  GroupLayerCompat,
  HomeCompat,
  IdentifyCompat,
  LayerListCompat,
  LegendCompat,
  LocateCompat,
  MeasurementCompat,
  MapImageLayerCompat,
  MapViewCompat,
  MapViewUiCompat,
  PrintCompat,
  PopupCompat,
  RouteLayerCompat,
  RouteTaskCompat,
  SearchCompat,
  SketchCompat,
  ScaleBarCompat,
  SwipeCompat,
  TableListCompat,
  TrackCompat,
  TimeSliderCompat,
  TileLayerCompat,
  ZoomCompat,
} from "@honua/sdk-esri-compat";
import {
  buildJsMigrationReport,
  getJsParityMatrix,
  runEsriCompatCodemod,
  runLayerReconciliation,
  scanArcGisUsage,
  summarizeJsParityMatrix,
} from "@honua/honua-migrate";

if (typeof HonuaClient !== "function") throw new Error("HonuaClient export missing");
if (typeof CompatEventBus !== "function") throw new Error("CompatEventBus export missing");
if (typeof CoordinateConversionCompat !== "function") throw new Error("CoordinateConversionCompat export missing");
if (typeof createEsriRequestInterceptors !== "function") throw new Error("createEsriRequestInterceptors export missing");
if (typeof createArcGisTokenInterceptor !== "function") throw new Error("createArcGisTokenInterceptor export missing");
if (typeof EsriRequestInterceptorRegistry !== "function") throw new Error("EsriRequestInterceptorRegistry export missing");
if (typeof DirectionsCompat !== "function") throw new Error("DirectionsCompat export missing");
if (typeof EditorCompat !== "function") throw new Error("EditorCompat export missing");
if (typeof FeatureCompat !== "function") throw new Error("FeatureCompat export missing");
if (typeof FeatureFormCompat !== "function") throw new Error("FeatureFormCompat export missing");
if (typeof FeatureTemplatesCompat !== "function") throw new Error("FeatureTemplatesCompat export missing");
if (typeof FeatureLayerCompat !== "function") throw new Error("FeatureLayerCompat export missing");
if (typeof FeatureTableCompat !== "function") throw new Error("FeatureTableCompat export missing");
if (typeof HomeCompat !== "function") throw new Error("HomeCompat export missing");
if (typeof BasemapToggleCompat !== "function") throw new Error("BasemapToggleCompat export missing");
if (typeof BasemapGalleryCompat !== "function") throw new Error("BasemapGalleryCompat export missing");
if (typeof BookmarksCompat !== "function") throw new Error("BookmarksCompat export missing");
if (typeof CompassCompat !== "function") throw new Error("CompassCompat export missing");
if (typeof AttributionCompat !== "function") throw new Error("AttributionCompat export missing");
if (typeof LocateCompat !== "function") throw new Error("LocateCompat export missing");
if (typeof MeasurementCompat !== "function") throw new Error("MeasurementCompat export missing");
if (typeof ScaleBarCompat !== "function") throw new Error("ScaleBarCompat export missing");
if (typeof ExpandCompat !== "function") throw new Error("ExpandCompat export missing");
if (typeof FullscreenCompat !== "function") throw new Error("FullscreenCompat export missing");
if (typeof ZoomCompat !== "function") throw new Error("ZoomCompat export missing");
if (typeof GraphicsLayerCompat !== "function") throw new Error("GraphicsLayerCompat export missing");
if (typeof GroupLayerCompat !== "function") throw new Error("GroupLayerCompat export missing");
if (typeof IdentifyCompat !== "function") throw new Error("IdentifyCompat export missing");
if (typeof LayerListCompat !== "function") throw new Error("LayerListCompat export missing");
if (typeof LegendCompat !== "function") throw new Error("LegendCompat export missing");
if (typeof MapImageLayerCompat !== "function") throw new Error("MapImageLayerCompat export missing");
if (typeof MapViewCompat !== "function") throw new Error("MapViewCompat export missing");
if (typeof MapViewUiCompat !== "function") throw new Error("MapViewUiCompat export missing");
if (typeof PrintCompat !== "function") throw new Error("PrintCompat export missing");
if (typeof PopupCompat !== "function") throw new Error("PopupCompat export missing");
if (typeof RouteLayerCompat !== "function") throw new Error("RouteLayerCompat export missing");
if (typeof RouteTaskCompat !== "function") throw new Error("RouteTaskCompat export missing");
if (typeof SearchCompat !== "function") throw new Error("SearchCompat export missing");
if (typeof SketchCompat !== "function") throw new Error("SketchCompat export missing");
if (typeof TileLayerCompat !== "function") throw new Error("TileLayerCompat export missing");
if (typeof SwipeCompat !== "function") throw new Error("SwipeCompat export missing");
if (typeof TableListCompat !== "function") throw new Error("TableListCompat export missing");
if (typeof TrackCompat !== "function") throw new Error("TrackCompat export missing");
if (typeof TimeSliderCompat !== "function") throw new Error("TimeSliderCompat export missing");
if (typeof scanArcGisUsage !== "function") throw new Error("scanArcGisUsage export missing");
if (typeof runEsriCompatCodemod !== "function") throw new Error("runEsriCompatCodemod export missing");
if (typeof buildJsMigrationReport !== "function") throw new Error("buildJsMigrationReport export missing");
if (typeof getJsParityMatrix !== "function") throw new Error("getJsParityMatrix export missing");
if (typeof runLayerReconciliation !== "function") throw new Error("runLayerReconciliation export missing");
if (typeof summarizeJsParityMatrix !== "function") throw new Error("summarizeJsParityMatrix export missing");

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
