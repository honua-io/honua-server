import { describe, expect, it } from "vitest";

import type { JsMigrationReport } from "../src/migration/report.js";
import { evaluateMigrationGates } from "../src/migration/gating.js";

function createReport(): JsMigrationReport {
  return {
    rootDir: "/tmp/app",
    codemodTarget: "honua-compat",
    scanSummary: "",
    scanReport: {
      rootDir: "/tmp/app",
      filesScanned: 1,
      filesWithArcGisImports: 1,
      imports: [],
      symbolUsageCounts: {},
      flags: [],
    },
    codemodResult: {
      rootDir: "/tmp/app",
      target: "honua-compat",
      filesScanned: 1,
      filesChanged: 1,
      metrics: {
        totalCodemodScopedCallSites: 5,
        autoMigratedCallSites: 4,
        manualCallSites: 1,
        byKind: {
          "feature-layer": { total: 2, autoMigrated: 1, manual: 1 },
          "graphics-layer": { total: 0, autoMigrated: 0, manual: 0 },
          "group-layer": { total: 0, autoMigrated: 0, manual: 0 },
          "map-image-layer": { total: 0, autoMigrated: 0, manual: 0 },
          "tile-layer": { total: 0, autoMigrated: 0, manual: 0 },
          map: { total: 1, autoMigrated: 1, manual: 0 },
          "map-view": { total: 1, autoMigrated: 1, manual: 0 },
          "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
          "web-map": { total: 1, autoMigrated: 1, manual: 0 },
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
        },
      },
      fileResults: [],
      manualTodos: [],
    },
    manualRewriteMetric: {
      numerator: 1,
      denominator: 5,
      ratio: 0.2,
      scope: "scope",
    },
    manualInterventionMetric: {
      numerator: 2,
      denominator: 6,
      ratio: 0.3333333333333333,
      scope: "scope",
      manualCodemodCallSites: 1,
      unhandledUsageHits: 1,
    },
    readiness: "blocked",
    gates: [
      {
        gate: "no-manual-todos",
        passed: false,
        detail: "1 manual codemod-scoped call sites remain",
      },
      {
        gate: "no-unhandled-modules",
        passed: false,
        detail: "1 ArcGIS modules remain outside codemod scope",
      },
      {
        gate: "no-blocking-flags",
        passed: false,
        detail: "blocking flags: scene-3d-detected",
      },
    ],
    manualTodosByKind: {
      "feature-layer": 1,
      "graphics-layer": 0,
      "group-layer": 0,
      "map-image-layer": 0,
      "tile-layer": 0,
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
    },
    manualTodoReasons: [],
    unhandledArcGisModules: [
      {
        modulePath: "@arcgis/core/views/SceneView",
        usageStyle: "dynamic-import",
        count: 1,
      },
    ],
    manualTodos: [],
  };
}

describe("evaluateMigrationGates", () => {
  it("returns failures when configured thresholds are exceeded", () => {
    const report = createReport();
    const result = evaluateMigrationGates(report, {
      failOnManual: true,
      failOnUnhandled: true,
      failOnBlocked: true,
      maxManualRatio: 0.1,
      maxManualInterventionRatio: 0.3,
    });

    expect(result.failed).toBe(true);
    expect(result.failures).toHaveLength(5);
    expect(result.failures[0]).toContain("manual rewrite required");
    expect(result.failures[1]).toContain("outside codemod scope");
    expect(result.failures[2]).toContain("exceeds max");
    expect(result.failures[3]).toContain("manual intervention ratio");
    expect(result.failures[4]).toContain("readiness is blocked");
  });

  it("passes when gates are disabled or thresholds are met", () => {
    const report = createReport();
    const result = evaluateMigrationGates(report, {
      failOnManual: false,
      failOnUnhandled: false,
      failOnBlocked: false,
      maxManualRatio: 0.25,
      maxManualInterventionRatio: 0.4,
    });

    expect(result.failed).toBe(false);
    expect(result.failures).toEqual([]);
  });
});
