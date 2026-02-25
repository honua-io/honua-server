import { describe, expect, it } from "vitest";

import type { ArcGisScanReport } from "../src/migration/scanner.js";
import { buildJsMigrationReport } from "../src/migration/report.js";
import type { EsriCompatCodemodResult } from "../src/migration/codemod.js";

function createCodemodResult(): EsriCompatCodemodResult {
  return {
    rootDir: "/tmp/app",
    filesScanned: 2,
    filesChanged: 1,
    metrics: {
      totalCodemodScopedCallSites: 4,
      autoMigratedCallSites: 3,
      manualCallSites: 1,
      byKind: {
        "feature-layer": { total: 2, autoMigrated: 1, manual: 1 },
        map: { total: 1, autoMigrated: 1, manual: 0 },
        "map-view": { total: 1, autoMigrated: 1, manual: 0 },
        "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
        "web-map": { total: 0, autoMigrated: 0, manual: 0 },
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
            reason: "FeatureLayer options include non-url properties; requires manual migration.",
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
        reason: "FeatureLayer options include non-url properties; requires manual migration.",
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
      map: 0,
      "map-view": 0,
      "scene-view": 0,
      "web-map": 0,
    });
    expect(report.manualTodoReasons).toEqual([
      {
        reason: "FeatureLayer options include non-url properties; requires manual migration.",
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
});
