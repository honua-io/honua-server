import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";
import { buildJsMigrationReport } from "../src/migration/report.js";

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

    const codemodResult = runEsriCompatCodemod({
      rootDir: workingCopy,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });
    const report = buildJsMigrationReport(workingCopy, codemodResult);

    expect(codemodResult.filesScanned).toBeGreaterThanOrEqual(2);
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(1);

    expect(report.scanReport.flags).toContain("dynamic-import-detected");
    expect(report.scanReport.flags).toContain("scene-3d-detected");
    expect(report.scanReport.flags).toContain("webmap-detected");
    expect(report.manualRewriteMetric.numerator).toBe(1);
    expect(report.manualRewriteMetric.denominator).toBe(4);

    const migratedMain = fs.readFileSync(path.join(workingCopy, "src", "main.ts"), "utf8");
    expect(migratedMain).toContain(
      'import { FeatureLayerCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
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
    expect(migratedMain).not.toContain('import Map from "@arcgis/core/Map";');
    expect(migratedMain).not.toContain('import MapView from "@arcgis/core/views/MapView";');
  });
});
