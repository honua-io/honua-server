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

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("arcgis migration integration", () => {
  it("runs scanner+codemod+report on an esri-style sample app fixture", () => {
    const tempRoot = makeTempDir();
    const sampleSource = fixturePath("esri-sample-app");
    const workingCopy = path.join(tempRoot, "sample-app");
    fs.cpSync(sampleSource, workingCopy, { recursive: true });
    const scanReport = scanArcGisUsage(workingCopy);

    const codemodResult = runEsriCompatCodemod({
      rootDir: workingCopy,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });
    const report = buildJsMigrationReport(workingCopy, codemodResult, scanReport);

    expect(codemodResult.filesScanned).toBeGreaterThanOrEqual(2);
    expect(codemodResult.filesChanged).toBe(2);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(6);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(5);
    expect(codemodResult.metrics.manualCallSites).toBe(1);
    expect(codemodResult.metrics.byKind["feature-layer"]).toEqual({
      total: 2,
      autoMigrated: 1,
      manual: 1,
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
    expect(codemodResult.manualTodos[0].kind).toBe("feature-layer");

    expect(report.scanReport.flags).toContain("dynamic-import-detected");
    expect(report.scanReport.flags).toContain("scene-3d-detected");
    expect(report.scanReport.flags).toContain("webmap-detected");
    expect(report.manualRewriteMetric.numerator).toBe(1);
    expect(report.manualRewriteMetric.denominator).toBe(6);
    expect(report.manualTodosByKind).toEqual({
      "feature-layer": 1,
      map: 0,
      "map-view": 0,
      "scene-view": 0,
      "web-map": 0,
    });
    expect(report.manualTodoReasons).toHaveLength(1);
    expect(report.manualTodoReasons[0].kinds).toEqual(["feature-layer"]);
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
      'const complex = new FeatureLayer({ url: layerUrl, outFields: ["*"] });',
    );
    expect(migratedMain).toContain('import FeatureLayer from "@arcgis/core/layers/FeatureLayer";');
    expect(migratedMain).not.toContain('import WebMap from "@arcgis/core/WebMap";');
    expect(migratedMain).not.toContain('import Map from "@arcgis/core/Map";');
    expect(migratedMain).not.toContain('import MapView from "@arcgis/core/views/MapView";');

    const migratedLazy = fs.readFileSync(path.join(workingCopy, "src", "lazy.ts"), "utf8");
    expect(migratedLazy).toContain(
      'import("@honua/sdk-esri-compat").then((m) => ({ default: m.SceneViewCompat }))',
    );
    expect(migratedLazy).not.toContain("@arcgis/core/views/SceneView");
  });
});
