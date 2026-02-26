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
      graphic: 0,
      "point-geometry": 0,
      color: 0,
      "simple-line-symbol": 0,
      "simple-marker-symbol": 0,
      "simple-fill-symbol": 0,
      "class-breaks-renderer": 0,
      "simple-renderer": 0,
      "unique-value-renderer": 0,
      "graphics-layer": 0,
      "group-layer": 0,
      "map-image-layer": 0,
      "tile-layer": 0,
      "route-layer": 0,
      "route-task": 0,
      basemap: 0,
      map: 0,
      "map-view": 0,
      "scene-view": 0,
      "web-map": 0,
      "layer-list": 0,
      "table-list-widget": 0,
      "feature-widget": 0,
      "feature-templates-widget": 0,
      "feature-form-widget": 0,
      "feature-table-widget": 0,
      "feature-set": 0,
      "legend-widget": 0,
      "popup-widget": 0,
      "popup-template": 0,
      "swipe-widget": 0,
      "print-widget": 0,
      "home-widget": 0,
      "basemap-toggle-widget": 0,
      "locate-widget": 0,
      "scale-bar-widget": 0,
      "search-widget": 0,
      "basemap-layer-list-widget": 0,
      "basemap-gallery-widget": 0,
      "expand-widget": 0,
      "compass-widget": 0,
      "bookmarks-widget": 0,
      "fullscreen-widget": 0,
      "zoom-widget": 0,
      "attribution-widget": 0,
      "sketch-widget": 0,
      "editor-widget": 0,
      "track-widget": 0,
      "distance-measurement-2d-widget": 0,
      "area-measurement-2d-widget": 0,
      "measurement-widget": 0,
      "time-slider-widget": 0,
      "directions-widget": 0,
      "coordinate-conversion-widget": 0,
      query: 0,
      "oauth-info": 0,
      "identity-manager": 0,
      "esri-request": 0,
      "esri-config": 0,
      "reactive-utils": 0,
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

  it("migrates basemap constructor fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-basemap-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind.basemap).toEqual({
      total: 1,
      autoMigrated: 1,
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
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { BasemapCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const basemap = new BasemapCompat({");
    expect(migratedMain).toContain("const map = new MapCompat({");
    expect(migratedMain).toContain("const view = new MapViewCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/Basemap");
  });

  it("migrates route task fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-route-task-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["route-task"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { RouteTaskCompat } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("const routeTask = new RouteTaskCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/rest/route/RouteTask");
  });

  it("migrates reactive-utils fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-reactive-utils-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["reactive-utils"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { reactiveUtils } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("reactiveUtils.whenOnce(() => ready);");
    expect(migratedMain).not.toContain("@arcgis/core/core/reactiveUtils");
  });

  it("migrates graphic fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-graphic-app");

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind.graphic).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { GraphicCompat } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("const parcelGraphic = new GraphicCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/Graphic");
  });

  it("migrates query fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-query-app");

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind.query).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, QueryCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const parcels = new FeatureLayerCompat({");
    expect(migratedMain).toContain("const query = new QueryCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/rest/support/Query");
  });

  it("migrates geometry/symbol fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-graphic-symbols-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(4);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind.graphic).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["point-geometry"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["simple-line-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["simple-marker-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { GraphicCompat, PointCompat, SimpleLineSymbolCompat, SimpleMarkerSymbolCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const geometry = new PointCompat({");
    expect(migratedMain).toContain("const outline = new SimpleLineSymbolCompat({");
    expect(migratedMain).toContain("const symbol = new SimpleMarkerSymbolCompat({");
    expect(migratedMain).toContain("const graphic = new GraphicCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/geometry/Point");
  });

  it("migrates color/renderer fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-renderers-app");

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(5);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(5);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind.color).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["simple-fill-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["class-breaks-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["simple-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["unique-value-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("ColorCompat");
    expect(migratedMain).toContain("SimpleFillSymbolCompat");
    expect(migratedMain).toContain("ClassBreaksRendererCompat");
    expect(migratedMain).toContain("SimpleRendererCompat");
    expect(migratedMain).toContain("UniqueValueRendererCompat");
    expect(migratedMain).toContain("const baseColor = new ColorCompat([255, 102, 0, 0.8]);");
    expect(migratedMain).toContain("const fill = new SimpleFillSymbolCompat({");
    expect(migratedMain).toContain("const simple = new SimpleRendererCompat({");
    expect(migratedMain).toContain("const classBreaks = new ClassBreaksRendererCompat({");
    expect(migratedMain).toContain("const unique = new UniqueValueRendererCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/renderers/SimpleRenderer");
  });

  it("migrates feature-set fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-feature-set-app");

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-set"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { FeatureSetCompat } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("const set = new FeatureSetCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/rest/support/FeatureSet");
  });

  it("migrates esri-config fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-config-app");

    expect(scanReport.flags).toEqual(["auth-or-request-customization-detected"]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["esri-config"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
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
    expect(migratedMain).toContain('import { esriConfig } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain("esriConfig.request.interceptors.push({");
    expect(migratedMain).not.toContain("@arcgis/core/config");
  });

  it("migrates esri-request fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-request-app");

    expect(scanReport.flags).toEqual(["auth-or-request-customization-detected"]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["esri-request"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { esriRequest as request } from "@honua/sdk-esri-compat";');
    expect(migratedMain).not.toContain("@arcgis/core/request");
  });

  it("migrates oauth bootstrap fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration("esri-oauth-app");

    expect(scanReport.flags).toEqual(["auth-or-request-customization-detected"]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["oauth-info"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["identity-manager"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["esri-config"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.readiness).toBe("ready");

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain('import { esriConfig, OAuthInfoCompat } from "@honua/sdk-esri-compat";');
    expect(migratedMain).toContain(
      'import { identityManager as IdentityManager } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const info = new OAuthInfoCompat({");
    expect(migratedMain).toContain("IdentityManager.registerOAuthInfos([info]);");
    expect(migratedMain).not.toContain("@arcgis/core/identity/OAuthInfo");
    expect(migratedMain).not.toContain("@arcgis/core/identity/IdentityManager");
    expect(migratedMain).not.toContain("@arcgis/core/config");
  });

  it("migrates feature table fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-feature-table-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["feature-table-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, FeatureTableCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const parcels = new FeatureLayerCompat({");
    expect(migratedMain).toContain("const table = new FeatureTableCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/FeatureTable");
  });

  it("migrates advanced feature table fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-feature-table-relates-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(4);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
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
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["feature-table-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, FeatureTableCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const table = new FeatureTableCompat({");
    expect(migratedMain).toContain("relatedRecordsEnabled: true");
    expect(migratedMain).toContain("attachmentsEnabled: true");
    expect(migratedMain).toContain("table.highlightIds.push(1);");
    expect(migratedMain).toContain("table.highlightIds.on(\"change\", (event) => {");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/FeatureTable");
  });

  it("migrates feature widget fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-feature-widget-app",
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
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["feature-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const featureWidget = new FeatureCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Feature");
  });

  it("migrates feature form fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-feature-form-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["feature-form-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureFormCompat, FeatureLayerCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const form = new FeatureFormCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/FeatureForm");
  });

  it("migrates table list fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-table-list-app",
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
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["table-list-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapCompat, MapViewCompat, TableListCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const tableList = new TableListCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/TableList");
  });

  it("migrates feature templates fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-feature-templates-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["feature-templates-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, FeatureTemplatesCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const templates = new FeatureTemplatesCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/FeatureTemplates");
  });

  it("migrates basemap layer list fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-basemap-layer-list-app",
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
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["basemap-layer-list-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { BasemapLayerListCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const basemapLayerList = new BasemapLayerListCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/BasemapLayerList");
  });

  it("migrates print widget fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-print-app",
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
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["print-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapCompat, MapViewCompat, PrintCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const printer = new PrintCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Print");
  });

  it("migrates swipe widget fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-swipe-app",
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
    expect(codemodResult.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["swipe-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { MapCompat, MapViewCompat, SwipeCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const swipe = new SwipeCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Swipe");
  });

  it("migrates distance/area measurement 2d fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-measurement-2d-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(4);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
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
    expect(codemodResult.metrics.byKind["distance-measurement-2d-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["area-measurement-2d-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { AreaMeasurement2DCompat, DistanceMeasurement2DCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const distance = new DistanceMeasurement2DCompat({");
    expect(migratedMain).toContain("const area = new AreaMeasurement2DCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/DistanceMeasurement2D");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/AreaMeasurement2D");
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

  it("migrates layer-list actions fixture with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-layer-list-actions-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(5);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(5);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
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
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["layer-list"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["popup-template"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, LayerListCompat, MapCompat, MapViewCompat, PopupTemplateCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const layerList = new LayerListCompat({");
    expect(migratedMain).toContain("listItemCreatedFunction: (event) => {");
    expect(migratedMain).toContain('layerList.on("trigger-action", (event) => {');
    expect(migratedMain).toContain("const popupTemplate = new PopupTemplateCompat({");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/LayerList");
    expect(migratedMain).not.toContain("@arcgis/core/PopupTemplate");
  });

  it("migrates map widgets and controls with ready gating", () => {
    const { workingCopy, scanReport, report, codemodResult } = runFixtureMigration(
      "esri-widget-controls-app",
    );

    expect(scanReport.flags).toEqual([]);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(25);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(25);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
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
    expect(codemodResult.metrics.byKind["route-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["layer-list"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["legend-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["popup-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["home-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["basemap-toggle-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["locate-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["scale-bar-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["search-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["basemap-gallery-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["expand-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["compass-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["bookmarks-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["fullscreen-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["zoom-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["attribution-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["sketch-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["editor-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["track-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["measurement-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["time-slider-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["directions-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(codemodResult.metrics.byKind["coordinate-conversion-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(report.readiness).toBe("ready");
    expect(report.unhandledArcGisModules).toEqual([]);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { AttributionCompat, BasemapGalleryCompat, BasemapToggleCompat, BookmarksCompat, CompassCompat, CoordinateConversionCompat, DirectionsCompat, EditorCompat, ExpandCompat, FullscreenCompat, HomeCompat, LayerListCompat, LegendCompat, LocateCompat, MapCompat, MapViewCompat, MeasurementCompat, PopupCompat, RouteLayerCompat, ScaleBarCompat, SearchCompat, SketchCompat, TimeSliderCompat, TrackCompat, ZoomCompat } from "@honua/sdk-esri-compat";',
    );
    expect(migratedMain).toContain("const layerList = new LayerListCompat({ view });");
    expect(migratedMain).toContain("const legend = new LegendCompat({ view });");
    expect(migratedMain).toContain("const popup = new PopupCompat({ view, dockEnabled: true });");
    expect(migratedMain).toContain("const home = new HomeCompat({ view });");
    expect(migratedMain).toContain('const basemapToggle = new BasemapToggleCompat({ view, nextBasemap: "satellite" });');
    expect(migratedMain).toContain("const locate = new LocateCompat({ view });");
    expect(migratedMain).toContain('const scaleBar = new ScaleBarCompat({ view, unit: "dual" });');
    expect(migratedMain).toContain('const search = new SearchCompat({ view, container: "search-div", includeDefaultSources: false });');
    expect(migratedMain).toContain('const basemapGallery = new BasemapGalleryCompat({ view, container: "gallery-div" });');
    expect(migratedMain).toContain("const compass = new CompassCompat({ view });");
    expect(migratedMain).toContain("const expand = new ExpandCompat({ view, content: legend, expanded: false });");
    expect(migratedMain).toContain("const bookmarks = new BookmarksCompat({");
    expect(migratedMain).toContain("const fullscreen = new FullscreenCompat({ view });");
    expect(migratedMain).toContain('const zoom = new ZoomCompat({ view, layout: "vertical" });');
    expect(migratedMain).toContain("const attribution = new AttributionCompat({");
    expect(migratedMain).toContain('itemDelimiter: " | "');
    expect(migratedMain).toContain('attributions: ["Source A"]');
    expect(migratedMain).toContain(
      'const sketch = new SketchCompat({ view, layer: undefined, creationMode: "update" });',
    );
    expect(migratedMain).toContain(
      'const editor = new EditorCompat({ view, layerInfos: [], allowedWorkflows: ["create", "update"] });',
    );
    expect(migratedMain).toContain("const track = new TrackCompat({");
    expect(migratedMain).toContain('goToLocationEnabled: true');
    expect(migratedMain).toContain('useHeadingEnabled: true');
    expect(migratedMain).toContain('rotationEnabled: true');
    expect(migratedMain).toContain("const routeLayer = new RouteLayerCompat({");
    expect(migratedMain).toContain("const directions = new DirectionsCompat({");
    expect(migratedMain).toContain("const coordinateConversion = new CoordinateConversionCompat({");
    expect(migratedMain).toContain("const measurement = new MeasurementCompat({");
    expect(migratedMain).toContain('activeTool: "distance"');
    expect(migratedMain).toContain('linearUnit: "kilometers"');
    expect(migratedMain).toContain('areaUnit: "square-kilometers"');
    expect(migratedMain).toContain("const timeSlider = new TimeSliderCompat({");
    expect(migratedMain).toContain('mode: "instant"');
    expect(migratedMain).toContain(
      'values: ["2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"]',
    );
    expect(migratedMain).toContain('view.ui.add(layerList, "top-right");');
    expect(migratedMain).toContain('view.ui.add([legend, home], "top-left");');
    expect(migratedMain).toContain('view.ui.add(popup, { position: "manual", index: 0 });');
    expect(migratedMain).toContain('view.ui.add([basemapToggle, locate, scaleBar], "bottom-right");');
    expect(migratedMain).toContain('view.ui.add(search, "top-left");');
    expect(migratedMain).toContain('view.ui.add(basemapGallery, "top-right");');
    expect(migratedMain).toContain('view.ui.add(compass, "top-left");');
    expect(migratedMain).toContain('view.ui.add(expand, "top-right");');
    expect(migratedMain).toContain('view.ui.add(bookmarks, "top-right");');
    expect(migratedMain).toContain('view.ui.add(fullscreen, "top-left");');
    expect(migratedMain).toContain('view.ui.add(zoom, "top-left");');
    expect(migratedMain).toContain('view.ui.add(attribution, "bottom-left");');
    expect(migratedMain).toContain('view.ui.add(sketch, "top-right");');
    expect(migratedMain).toContain('view.ui.add(editor, "top-right");');
    expect(migratedMain).toContain('view.ui.add(track, "top-left");');
    expect(migratedMain).toContain('view.ui.add(measurement, "bottom-right");');
    expect(migratedMain).toContain('view.ui.add(timeSlider, "bottom-left");');
    expect(migratedMain).toContain('view.ui.add(directions, "top-right");');
    expect(migratedMain).toContain('view.ui.add(coordinateConversion, "bottom-left");');
    expect(migratedMain).not.toContain("@arcgis/core/widgets/LayerList");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Legend");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Popup");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Home");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/BasemapToggle");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Locate");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/ScaleBar");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Search");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/BasemapGallery");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Compass");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Expand");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Bookmarks");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Fullscreen");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Zoom");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Attribution");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Sketch");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Editor");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Track");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Measurement");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/TimeSlider");
    expect(migratedMain).not.toContain("@arcgis/core/layers/RouteLayer");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/Directions");
    expect(migratedMain).not.toContain("@arcgis/core/widgets/CoordinateConversion");
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

    expect(scanReport.flags).toEqual(["auth-or-request-customization-detected"]);
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
