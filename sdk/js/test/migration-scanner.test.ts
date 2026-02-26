import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { scanArcGisUsage, summarizeArcGisScan } from "../src/migration/scanner.js";

const tempDirs: string[] = [];

function makeTempProject(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-arcgis-scan-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("scanArcGisUsage", () => {
  it("detects arcgis imports and symbol usage", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "map.ts"),
      [
        "import MapView from '@arcgis/core/views/MapView';",
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const view = new MapView({});",
        "const layer = new FeatureLayer({});",
        "void view; void layer;",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.filesScanned).toBe(1);
    expect(report.filesWithArcGisImports).toBe(1);
    expect(report.imports.length).toBe(2);
    expect(report.symbolUsageCounts.MapView).toBeGreaterThan(0);
    expect(report.symbolUsageCounts.FeatureLayer).toBeGreaterThan(0);
  });

  it("flags advanced migration risk patterns", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "app.ts"),
      [
        "import WebMap from '@arcgis/core/WebMap';",
        "const scene = import('@arcgis/core/views/SceneView');",
        "void scene; void WebMap;",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.imports.some((item) => item.importClause === "import(...)")).toBe(true);
    expect(report.imports.some((item) => item.modulePath === "@arcgis/core/views/SceneView")).toBe(true);
    expect(report.flags).toContain("webmap-detected");
    expect(report.flags).toContain("dynamic-import-detected");
  });

  it("captures side-effect ArcGIS imports", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "side-effects.ts"),
      [
        "import '@arcgis/core/identity/IdentityManager';",
        "export const ready = true;",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.imports).toEqual([
      {
        file: path.join(root, "side-effects.ts"),
        modulePath: "@arcgis/core/identity/IdentityManager",
        importClause: "side-effect-import",
        symbols: [],
      },
    ]);
    expect(report.filesWithArcGisImports).toBe(1);
    expect(report.flags).toEqual(["auth-or-request-customization-detected"]);
  });

  it("captures require imports with local symbol usage", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "require-map.cjs"),
      [
        "const Map = require('@arcgis/core/Map').default;",
        "const map = new Map({ basemap: 'streets' });",
        "module.exports = { map };",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.imports).toEqual([
      {
        file: path.join(root, "require-map.cjs"),
        modulePath: "@arcgis/core/Map",
        importClause: "require(...)",
        symbols: ["Map"],
      },
    ]);
    expect(report.symbolUsageCounts.Map).toBeGreaterThan(0);
    expect(report.filesWithArcGisImports).toBe(1);
    expect(report.flags).toEqual(["commonjs-detected"]);
  });

  it("captures destructured require default imports with local symbol usage", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "require-destructured.cjs"),
      [
        "const { default: MapCtor } = require('@arcgis/core/Map');",
        "const map = new MapCtor({ basemap: 'streets' });",
        "module.exports = { map };",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.imports).toEqual([
      {
        file: path.join(root, "require-destructured.cjs"),
        modulePath: "@arcgis/core/Map",
        importClause: "require(...)",
        symbols: ["MapCtor"],
      },
    ]);
    expect(report.symbolUsageCounts.MapCtor).toBeGreaterThan(0);
    expect(report.filesWithArcGisImports).toBe(1);
    expect(report.flags).toEqual(["commonjs-detected"]);
  });

  it("captures arcgis re-export declarations", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "exports.ts"),
      [
        "export { default as FeatureLayer } from '@arcgis/core/layers/FeatureLayer';",
        "export * from '@arcgis/core/views/MapView';",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.imports).toEqual([
      {
        file: path.join(root, "exports.ts"),
        modulePath: "@arcgis/core/layers/FeatureLayer",
        importClause: "export { default as FeatureLayer }",
        symbols: ["FeatureLayer"],
      },
      {
        file: path.join(root, "exports.ts"),
        modulePath: "@arcgis/core/views/MapView",
        importClause: "export *",
        symbols: [],
      },
    ]);
    expect(report.filesWithArcGisImports).toBe(1);
  });

  it("produces a stable summary string", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "index.ts"),
      "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';\nvoid new FeatureLayer({});\n",
      "utf8",
    );

    const report = scanArcGisUsage(root);
    const summary = summarizeArcGisScan(report);

    expect(summary).toContain("filesScanned=1");
    expect(summary).toContain("filesWithArcGisImports=1");
    expect(summary).toContain("importCount=1");
    expect(summary).toContain("FeatureLayer");
  });

  it("flags auth/request customization patterns", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "auth.ts"),
      [
        "import esriConfig from '@arcgis/core/config';",
        "import IdentityManager from '@arcgis/core/identity/IdentityManager';",
        "esriConfig.request.interceptors.push({ before() {} });",
        "void IdentityManager;",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.flags).toContain("auth-or-request-customization-detected");
  });

  it("flags unsupported advanced networking widget patterns", () => {
    const root = makeTempProject();
    fs.writeFileSync(
      path.join(root, "advanced.ts"),
      [
        "import Geoprocessor from '@arcgis/core/rest/geoprocessor/Geoprocessor';",
        "void Geoprocessor;",
      ].join("\n"),
      "utf8",
    );

    const report = scanArcGisUsage(root);
    expect(report.flags).toContain("advanced-widget-or-networking-detected");
  });
});
