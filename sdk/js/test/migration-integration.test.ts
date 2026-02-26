import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";
import { buildJsMigrationReport } from "../src/migration/report.js";
import { scanArcGisUsage } from "../src/migration/scanner.js";

const tempDirs: string[] = [];

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-arcgis-integration-"));
  tempDirs.push(dir);
  return dir;
}

function fixturePath(...parts: string[]): string {
  const fixturesDir = fileURLToPath(new URL("./fixtures/", import.meta.url));
  return path.join(fixturesDir, ...parts);
}

function runFixtureMigration(
  fixtureName: string,
  codemodOptions: Partial<Parameters<typeof runEsriCompatCodemod>[0]> = {},
): {
  workingCopy: string;
  scanReport: ReturnType<typeof scanArcGisUsage>;
  codemodResult: ReturnType<typeof runEsriCompatCodemod>;
  report: ReturnType<typeof buildJsMigrationReport>;
} {
  const tempRoot = makeTempDir();
  const sampleSource = fixturePath(fixtureName);
  const workingCopy = path.join(tempRoot, fixtureName);
  fs.cpSync(sampleSource, workingCopy, { recursive: true });

  const scanReport = scanArcGisUsage(workingCopy);
  const codemodResult = runEsriCompatCodemod({
    rootDir: workingCopy,
    write: true,
    compatImportPath: "@honua/sdk-esri-compat",
    ...codemodOptions,
  });
  const report = buildJsMigrationReport(workingCopy, codemodResult, scanReport);

  return { workingCopy, scanReport, codemodResult, report };
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("arcgis migration integration", () => {
  it("runs scanner+codemod+report on an esri-style sample app fixture", () => {
    const { workingCopy, report, codemodResult } = runFixtureMigration("esri-sample-app");

    expect(codemodResult.filesScanned).toBeGreaterThanOrEqual(2);
    expect(codemodResult.filesChanged).toBe(2);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(6);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(6);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 2,
      autoMigrated: 2,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["scene-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["web-map"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.manualTodos).toEqual([]);

    expect(report.scanReport.flags).toContain("dynamic-import-detected");
    expect(report.scanReport.flags).toContain("scene-3d-detected");
    expect(report.scanReport.flags).toContain("webmap-detected");
    expect(report.manualRewriteMetric.numerator).toBe(0);
    expect(report.manualRewriteMetric.denominator).toBe(6);
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 6,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.manualTodosByKind).toEqual({
      "feature-layer": 0,
      "graphics-layer": 0,
      "group-layer": 0,
      "map-image-layer": 0,
      "tile-layer": 0,
      map: 0,
      "map-view": 0,
      "scene-view": 0,
      "web-map": 0,
    });
    expect(report.manualTodoReasons).toHaveLength(0);
    expect(report.unhandledArcGisModules).toHaveLength(0);
    expect(report.readiness).toBe("blocked");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: false,
        detail: "blocking flags: scene-3d-detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, MapCompat, MapViewCompat, WebMapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain(
      'const simple = new FeatureLayerCompat({ url: "https://example.test/rest/services/default/FeatureServer/0" });',
    );
    expect(migratedMain).toContain(
      'const map = new MapCompat({ basemap: "streets-vector", layers: [simple] });',
    );
    expect(migratedMain).toContain("const mapView = new MapViewCompat({");
    expect(migratedMain).toContain(
      'const complex = new FeatureLayerCompat({ url: layerUrl, outFields: ["*"] });',
    );
    expect(migratedMain).not.toContain('import FeatureLayer from "@arcgis/core/layers/FeatureLayer";');
    expect(migratedMain).not.toContain('import WebMap from "@arcgis/core/WebMap";');
    expect(migratedMain).not.toContain('import Map from "@arcgis/core/Map";');
    expect(migratedMain).not.toContain('import MapView from "@arcgis/core/views/MapView";');

    const migratedLazy = fs.readFileSync(path.join(workingCopy, "src", "lazy.ts"), "utf8");
    expect(migratedLazy).toContain(
      'import("@honua/sdk-esri-compat").then((m) => ({ default: m.SceneViewCompat }))',
    );
    expect(migratedLazy).not.toContain("@arcgis/core/views/SceneView");
  });

  it("reports ready when a fixture fully auto-migrates with no blocking flags", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-ready-app");

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 3,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 3,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("new FeatureLayerCompat({");
    expect(migratedMain).toContain("new MapCompat({");
    expect(migratedMain).toContain("new MapViewCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(migratedMain).not.toContain("@arcgis/core/Map");
    expect(migratedMain).not.toContain("@arcgis/core/views/MapView");
  });

  it("migrates a hit-test sample app with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-hit-test-sample-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 3,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 3,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const trails = new FeatureLayerCompat({");
    expect(migratedMain).toContain("const map = new MapCompat({");
    expect(migratedMain).toContain("const view = new MapViewCompat({");
    expect(migratedMain).toContain("const hit = await view.hitTest(event);");
    expect(migratedMain).toContain("view.popup.open({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(migratedMain).not.toContain("@arcgis/core/Map");
    expect(migratedMain).not.toContain("@arcgis/core/views/MapView");
  });

  it("migrates map image layer app flow with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-map-image-layer-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 2,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 2,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapCompat, MapImageLayerCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const parcels = new MapImageLayerCompat({");
    expect(migratedMain).toContain("const map = new MapCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/MapImageLayer");
    expect(migratedMain).not.toContain("@arcgis/core/Map");
  });

  it("migrates tile layer app flow with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-tile-layer-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapCompat, TileLayerCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const tiled = new TileLayerCompat({");
    expect(migratedMain).toContain("const map = new MapCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/TileLayer");
    expect(migratedMain).not.toContain("@arcgis/core/Map");
  });

  it("supports esri-leaflet codemod target for deterministic subset", () => {
    const { workingCopy, report, codemodResult } = runFixtureMigration(
      "esri-map-image-layer-app",
      { target: "esri-leaflet" },
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(1);
    expect(codemodResult.metrics.byKind["map-image-layer"]).toMatchObject({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind.map).toMatchObject({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(report.codemodTarget).toBe("esri-leaflet");
    expect(report.readiness).toBe("assisted");
    expect(report.manualTodos.some((todo) => todo.kind === "map")).toBe(true);
    expect(report.unhandledArcGisModules).toEqual(
      expect.arrayContaining([
        {
          modulePath: "@arcgis/core/Map",
          usageStyle: "static-import",
          count: 1,
        },
      ]),
    );

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(migratedMain).toContain("const parcels = HonuaEsriLeaflet.dynamicMapLayer({");
    expect(migratedMain).toContain("new Map({");
  });

  it("migrates map + group-layer + graphics-layer fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-layer-tree-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["graphics-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["group-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { GraphicsLayerCompat, GroupLayerCompat, MapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const graphics = new GraphicsLayerCompat({");
    expect(migratedMain).toContain("const group = new GroupLayerCompat({");
    expect(migratedMain).toContain("const map = new MapCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/GraphicsLayer");
    expect(migratedMain).not.toContain("@arcgis/core/layers/GroupLayer");
    expect(migratedMain).not.toContain("@arcgis/core/Map");
  });

  it("migrates supported dynamic import usage with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-dynamic-map-app",
    );

    expect(scanReport.flags).toContain("dynamic-import-detected");
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapCompat }))',
    );
    expect(migratedMain).toContain("return new MapCtor({");
    expect(migratedMain).not.toContain('@arcgis/core/Map');
  });

  it("migrates webmap constructor flow with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-webmap-app");

    expect(scanReport.flags).toContain("webmap-detected");
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 2,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 2,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapViewCompat, WebMapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const map = new WebMapCompat({");
    expect(migratedMain).toContain("const view = new MapViewCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/WebMap");
    expect(migratedMain).not.toContain("@arcgis/core/views/MapView");
  });

  it("migrates await import default flow with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-await-import-default-app",
    );

    expect(scanReport.flags).toContain("dynamic-import-detected");
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      '(await import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapCompat }))).default',
    );
    expect(migratedMain).toContain("return new MapCtor({");
    expect(migratedMain).not.toContain('@arcgis/core/Map');
  });

  it("migrates related-feature query app flow with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-related-features-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 1,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("const layer = new FeatureLayerCompat({");
    expect(migratedMain).toContain("return layer.queryRelatedFeatures({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/FeatureLayer");
  });

  it("reports assisted when .cjs require-style usage requires manual migration", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-assisted-require-app",
    );

    expect(scanReport.flags).toEqual(["commonjs-detected"]);
    expect(codemodResult.filesChanged).toBe(0);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(0);
    expect(codemodResult.metrics.manualCallSites).toBe(1);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 1,
      denominator: 1,
      ratio: 1,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 1,
      denominator: 1,
      ratio: 1,
      manualCodemodCallSites: 1,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("assisted");
    expect(report.manualTodos).toHaveLength(1);
    expect(report.manualTodos[0]?.reason).toContain("CommonJS require constructors");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: false,
        detail: "1 manual codemod-scoped call sites remain",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const source = fs.readFileSync(path.join(workingCopy, "src", "main.cjs"), "utf8");
    expect(source).toContain('require("@arcgis/core/Map")');
    expect(source).toContain("new Map({");
    expect(source).toContain('basemap: "streets"');
    expect(source).toContain("module.exports = { map };");
    expect(source).not.toContain("@honua/sdk-esri-compat");
  });

  it("reports assisted when .js CommonJS require usage requires manual migration", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-assisted-require-js-cjs-app",
    );

    expect(scanReport.flags).toEqual(["commonjs-detected"]);
    expect(codemodResult.filesChanged).toBe(0);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(0);
    expect(codemodResult.metrics.manualCallSites).toBe(1);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 1,
      denominator: 1,
      ratio: 1,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 1,
      denominator: 1,
      ratio: 1,
      manualCodemodCallSites: 1,
      unhandledUsageHits: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("assisted");
    expect(report.manualTodos).toHaveLength(1);
    expect(report.manualTodos[0]?.reason).toContain("CommonJS require constructors");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: false,
        detail: "1 manual codemod-scoped call sites remain",
      },
      {
        gate: "no-unhandled-modules",
        passed: true,
        detail: "all discovered ArcGIS modules are in codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const source = fs.readFileSync(path.join(workingCopy, "src", "main.js"), "utf8");
    expect(source).toContain('require("@arcgis/core/Map")');
    expect(source).toContain("new Map({");
    expect(source).toContain('basemap: "streets"');
    expect(source).toContain("module.exports = { map };");
    expect(source).not.toContain("@honua/sdk-esri-compat");
  });

  it("reports assisted for side-effect ArcGIS imports outside codemod scope", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-assisted-side-effect-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(scanReport.imports).toEqual([
      {
        file: path.join(workingCopy, "src", "main.ts"),
        modulePath: "@arcgis/core/identity/IdentityManager",
        importClause: "side-effect-import",
        symbols: [],
      },
    ]);
    expect(codemodResult.filesChanged).toBe(0);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(0);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(0);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 0,
      denominator: 0,
      ratio: 0,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 1,
      denominator: 1,
      ratio: 1,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 1,
    });
    expect(report.unhandledArcGisModules).toEqual([
      {
        modulePath: "@arcgis/core/identity/IdentityManager",
        usageStyle: "static-import",
        count: 1,
      },
    ]);
    expect(report.readiness).toBe("assisted");
    expect(report.gates).toEqual([
      {
        gate: "no-manual-todos",
        passed: true,
        detail: "all codemod-scoped call sites auto-migrated",
      },
      {
        gate: "no-unhandled-modules",
        passed: false,
        detail: "1 ArcGIS modules remain outside codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: true,
        detail: "no blocking migration flags detected",
      },
    ]);

    const source = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(source).toContain('import "@arcgis/core/identity/IdentityManager";');
  });
});
