import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";

const tempDirs: string[] = [];

function makeTempProject(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-arcgis-codemod-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("runEsriCompatCodemod", () => {
  it("rewrites safe FeatureLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "app.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const serviceUrl = 'https://example.test/rest/services/default/FeatureServer/0';",
        "const layer = new FeatureLayer({ url: serviceUrl });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new FeatureLayerCompat({ url: serviceUrl })");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
  });

  it("rewrites FeatureLayer shorthand url options", () => {
    const root = makeTempProject();
    const file = path.join(root, "shorthand.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const url = 'https://example.test/rest/services/default/FeatureServer/0';",
        "const layer = new FeatureLayer({ url });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayerCompat({ url })");
  });

  it("rewrites safe Map, MapView, and WebMap constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "view.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import WebMap from '@arcgis/core/WebMap';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, zoom: 4 });",
        "const webMap = new WebMap({ portalItem: { id: 'abc123' } });",
        "void map; void view; void webMap;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["web-map"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { MapCompat, MapViewCompat, WebMapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).not.toContain("@arcgis/core/Map");
    expect(nextSource).not.toContain("@arcgis/core/views/MapView");
    expect(nextSource).not.toContain("@arcgis/core/WebMap");
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).toContain("const view = new MapViewCompat({ map, zoom: 4 });");
    expect(nextSource).toContain("const webMap = new WebMapCompat({ portalItem: { id: 'abc123' } });");
  });

  it("keeps complex constructor and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "map.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, outFields: ['*'] });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: false,
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0].kind).toBe("feature-layer");
    expect(result.manualTodos[0].reason).toContain("non-url properties");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayer({ url: serviceUrl, outFields: ['*'] })");
  });

  it("keeps ArcGIS import when mixed auto and manual call sites exist", () => {
    const root = makeTempProject();
    const file = path.join(root, "mixed.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const a = new FeatureLayer({ url: serviceUrl });",
        "const b = new FeatureLayer({ url: serviceUrl, outFields: ['*'] });",
        "void a; void b;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 2,
      autoMigrated: 1,
      manual: 1,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const a = new FeatureLayerCompat({ url: serviceUrl });");
    expect(nextSource).toContain("const b = new FeatureLayer({ url: serviceUrl, outFields: ['*'] });");
  });

  it("can annotate manual todos inline without duplicating markers on rerun", () => {
    const root = makeTempProject();
    const file = path.join(root, "annotated.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, outFields: ['*'] });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    runEsriCompatCodemod({
      rootDir: root,
      write: true,
      annotateTodos: true,
    });
    runEsriCompatCodemod({
      rootDir: root,
      write: true,
      annotateTodos: true,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    const marker = "// TODO(honua-migrate)[feature-layer]:";
    expect(nextSource.includes(marker)).toBe(true);
    expect(nextSource.split(marker)).toHaveLength(2);
  });
});
