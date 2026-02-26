import { describe, expect, it } from "vitest";

import type { ArcGisScanReport } from "../src/migration/scanner.js";
import { buildJsMigrationReport } from "../src/migration/report.js";
import type { EsriCompatCodemodResult } from "../src/migration/codemod.js";

function createCodemodResult(): EsriCompatCodemodResult {
  return {
    rootDir: "/tmp/app",
    target: "honua-compat",
    filesScanned: 2,
    filesChanged: 1,
    metrics: {
      totalCodemodScopedCallSites: 4,
      autoMigratedCallSites: 3,
      manualCallSites: 1,
      byKind: {
        "feature-layer": { total: 2, autoMigrated: 1, manual: 1 },
        "graphics-layer": { total: 0, autoMigrated: 0, manual: 0 },
        "group-layer": { total: 0, autoMigrated: 0, manual: 0 },
        "map-image-layer": { total: 0, autoMigrated: 0, manual: 0 },
        "tile-layer": { total: 0, autoMigrated: 0, manual: 0 },
        "route-layer": { total: 0, autoMigrated: 0, manual: 0 },
        map: { total: 1, autoMigrated: 1, manual: 0 },
        "map-view": { total: 1, autoMigrated: 1, manual: 0 },
        "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
        "web-map": { total: 0, autoMigrated: 0, manual: 0 },
        "layer-list": { total: 0, autoMigrated: 0, manual: 0 },
        "legend-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "popup-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "home-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "basemap-toggle-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "locate-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "scale-bar-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "search-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "basemap-gallery-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "expand-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "compass-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "bookmarks-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "fullscreen-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "zoom-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "attribution-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "sketch-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "editor-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "track-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "measurement-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "time-slider-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "directions-widget": { total: 0, autoMigrated: 0, manual: 0 },
        "coordinate-conversion-widget": { total: 0, autoMigrated: 0, manual: 0 },
      },
    },
    fileResults: [
      {
        file: "/tmp/app/src/main.ts",
        rewrittenConstructors: 3,
        rewrittenDynamicImports: 0,
        addedCompatImport: true,
        removedArcGisImports: 2,
        annotatedTodoComments: 1,
        manualTodos: [
          {
            kind: "feature-layer",
            file: "/tmp/app/src/main.ts",
            line: 8,
            column: 17,
            reason: "FeatureLayer options include unsupported properties; requires manual migration.",
          },
        ],
      },
    ],
    manualTodos: [
      {
        kind: "feature-layer",
        file: "/tmp/app/src/main.ts",
        line: 8,
        column: 17,
        reason: "FeatureLayer options include unsupported properties; requires manual migration.",
      },
    ],
  };
}

function createScanReport(): ArcGisScanReport {
  return {
    rootDir: "/tmp/app",
    filesScanned: 2,
    filesWithArcGisImports: 2,
    imports: [
      {
        file: "/tmp/app/src/main.ts",
        modulePath: "@arcgis/core/layers/FeatureLayer",
        importClause: "FeatureLayer",
        symbols: ["FeatureLayer"],
      },
      {
        file: "/tmp/app/src/main.ts",
        modulePath: "@arcgis/core/Map",
        importClause: "Map",
        symbols: ["Map"],
      },
      {
        file: "/tmp/app/src/main.ts",
        modulePath: "@arcgis/core/views/MapView",
        importClause: "MapView",
        symbols: ["MapView"],
      },
      {
        file: "/tmp/app/src/main.ts",
        modulePath: "@arcgis/core/WebMap",
        importClause: "WebMap",
        symbols: ["WebMap"],
      },
      {
        file: "/tmp/app/src/lazy.ts",
        modulePath: "@arcgis/core/views/SceneView",
        importClause: "import(...)",
        symbols: [],
      },
    ],
    symbolUsageCounts: {
      FeatureLayer: 3,
      Map: 2,
      MapView: 2,
      WebMap: 2,
    },
    flags: ["dynamic-import-detected", "scene-3d-detected", "webmap-detected"],
  };
}

describe("buildJsMigrationReport", () => {
  it("builds manual summaries and unhandled module inventory", () => {
    const report = buildJsMigrationReport("/tmp/app", createCodemodResult(), createScanReport());
    expect(report.codemodTarget).toBe("honua-compat");

    expect(report.manualRewriteMetric).toMatchObject({
      numerator: 1,
      denominator: 4,
      ratio: 0.25,
    });
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 1,
      denominator: 4,
      ratio: 0.25,
      manualCodemodCallSites: 1,
      unhandledUsageHits: 0,
    });
    expect(report.manualTodosByKind).toEqual({
      "feature-layer": 1,
      "graphics-layer": 0,
      "group-layer": 0,
      "map-image-layer": 0,
      "tile-layer": 0,
      "route-layer": 0,
      map: 0,
      "map-view": 0,
      "scene-view": 0,
      "web-map": 0,
      "layer-list": 0,
      "legend-widget": 0,
      "popup-widget": 0,
      "home-widget": 0,
      "basemap-toggle-widget": 0,
      "locate-widget": 0,
      "scale-bar-widget": 0,
      "search-widget": 0,
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
      "measurement-widget": 0,
      "time-slider-widget": 0,
      "directions-widget": 0,
      "coordinate-conversion-widget": 0,
    });
    expect(report.manualTodoReasons).toEqual([
      {
        reason: "FeatureLayer options include unsupported properties; requires manual migration.",
        count: 1,
        kinds: ["feature-layer"],
      },
    ]);
    expect(report.unhandledArcGisModules).toHaveLength(0);
    expect(report.readiness).toBe("blocked");
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
        passed: false,
        detail: "blocking flags: scene-3d-detected",
      },
    ]);
  });

  it("treats require usage of supported modules as handled when codemod covered the kind", () => {
    const codemodResult = createCodemodResult();
    const scanReport: ArcGisScanReport = {
      rootDir: "/tmp/app",
      filesScanned: 1,
      filesWithArcGisImports: 1,
      imports: [
        {
          file: "/tmp/app/src/main.cjs",
          modulePath: "@arcgis/core/Map",
          importClause: "require(...)",
          symbols: ["Map"],
        },
      ],
      symbolUsageCounts: {
        Map: 1,
      },
      flags: [],
    };

    const report = buildJsMigrationReport("/tmp/app", codemodResult, scanReport);
    expect(report.unhandledArcGisModules).toEqual([]);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")).toMatchObject({
      passed: true,
    });
  });

  it("keeps unsupported require modules in unhandled inventory", () => {
    const codemodResult = createCodemodResult();
    const scanReport: ArcGisScanReport = {
      rootDir: "/tmp/app",
      filesScanned: 1,
      filesWithArcGisImports: 1,
      imports: [
        {
          file: "/tmp/app/src/main.cjs",
          modulePath: "@arcgis/core/identity/IdentityManager",
          importClause: "require(...)",
          symbols: [],
        },
      ],
      symbolUsageCounts: {},
      flags: [],
    };

    const report = buildJsMigrationReport("/tmp/app", codemodResult, scanReport);
    expect(report.unhandledArcGisModules).toEqual([
      {
        modulePath: "@arcgis/core/identity/IdentityManager",
        usageStyle: "require",
        count: 1,
      },
    ]);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")).toMatchObject({
      passed: false,
    });
  });

  it("treats target-unsupported modules as unhandled for esri-leaflet mode", () => {
    const codemodResult = {
      ...createCodemodResult(),
      target: "esri-leaflet" as const,
    };
    const scanReport: ArcGisScanReport = {
      rootDir: "/tmp/app",
      filesScanned: 1,
      filesWithArcGisImports: 1,
      imports: [
        {
          file: "/tmp/app/src/main.ts",
          modulePath: "@arcgis/core/Map",
          importClause: "Map",
          symbols: ["Map"],
        },
        {
          file: "/tmp/app/src/main.ts",
          modulePath: "@arcgis/core/layers/MapImageLayer",
          importClause: "MapImageLayer",
          symbols: ["MapImageLayer"],
        },
      ],
      symbolUsageCounts: {
        Map: 1,
        MapImageLayer: 1,
      },
      flags: [],
    };

    const report = buildJsMigrationReport("/tmp/app", codemodResult, scanReport);
    expect(report.unhandledArcGisModules).toEqual([
      {
        modulePath: "@arcgis/core/Map",
        usageStyle: "static-import",
        count: 1,
      },
    ]);
  });
});
