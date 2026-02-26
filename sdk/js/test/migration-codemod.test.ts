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

  it("rewrites FeatureLayer constructors with supported options", () => {
    const root = makeTempProject();
    const file = path.join(root, "supported-options.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, outFields: ['*'], definitionExpression: 'status = 1' });",
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
    expect(nextSource).toContain(
      "new FeatureLayerCompat({ url: serviceUrl, outFields: ['*'], definitionExpression: 'status = 1' })",
    );
  });

  it("rewrites safe MapImageLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "map-image-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "const layer = new MapImageLayer({",
        "  url: serviceUrl,",
        "  sublayers: [{ id: 0 }],",
        "  opacity: 0.8,",
        "  visible: true,",
        "});",
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
    expect(result.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapImageLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new MapImageLayerCompat({");
    expect(nextSource).not.toContain("@arcgis/core/layers/MapImageLayer");
  });

  it("rewrites safe TileLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "tile-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import TileLayer from '@arcgis/core/layers/TileLayer';",
        "const layer = new TileLayer({",
        "  url: serviceUrl,",
        "  opacity: 0.6,",
        "  visible: true,",
        "});",
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
    expect(result.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { TileLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new TileLayerCompat({");
    expect(nextSource).not.toContain("@arcgis/core/layers/TileLayer");
  });

  it("rewrites safe GraphicsLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "graphics-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import GraphicsLayer from '@arcgis/core/layers/GraphicsLayer';",
        "const graphics = new GraphicsLayer({ id: 'graphics', visible: true, opacity: 0.9 });",
        "void graphics;",
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
    expect(result.metrics.byKind["graphics-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { GraphicsLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const graphics = new GraphicsLayerCompat({ id: 'graphics', visible: true, opacity: 0.9 });");
    expect(nextSource).not.toContain("@arcgis/core/layers/GraphicsLayer");
  });

  it("rewrites safe GroupLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "group-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import GroupLayer from '@arcgis/core/layers/GroupLayer';",
        "const group = new GroupLayer({ id: 'group-1', layers: [{ id: 'child' }], visibilityMode: 'independent' });",
        "void group;",
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
    expect(result.metrics.byKind["group-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { GroupLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain(
      "const group = new GroupLayerCompat({ id: 'group-1', layers: [{ id: 'child' }], visibilityMode: 'independent' });",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/GroupLayer");
  });

  it("rewrites safe widget/control constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "widgets.ts");
    fs.writeFileSync(
      file,
      [
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Home from '@arcgis/core/widgets/Home';",
        "import BasemapToggle from '@arcgis/core/widgets/BasemapToggle';",
        "import Locate from '@arcgis/core/widgets/Locate';",
        "import ScaleBar from '@arcgis/core/widgets/ScaleBar';",
        "import Search from '@arcgis/core/widgets/Search';",
        "const view = {};",
        "const layerList = new LayerList({ view, container: 'layer-list-div' });",
        "const legend = new Legend({ view, container: 'legend-div' });",
        "const popup = new Popup({ view, container: 'popup-div', dockEnabled: true });",
        "const home = new Home({ view, container: 'home-div' });",
        "const basemapToggle = new BasemapToggle({ view, container: 'basemap-div', nextBasemap: 'satellite' });",
        "const locate = new Locate({ view, container: 'locate-div' });",
        "const scaleBar = new ScaleBar({ view, container: 'scale-div', unit: 'dual' });",
        "const search = new Search({ view, container: 'search-div', includeDefaultSources: false });",
        "void layerList;",
        "void legend;",
        "void popup;",
        "void home;",
        "void basemapToggle;",
        "void locate;",
        "void scaleBar;",
        "void search;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(8);
    expect(result.metrics.autoMigratedCallSites).toBe(8);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["layer-list"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["legend-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["popup-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["home-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["basemap-toggle-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["locate-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["scale-bar-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["search-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { BasemapToggleCompat, HomeCompat, LayerListCompat, LegendCompat, LocateCompat, PopupCompat, ScaleBarCompat, SearchCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain("const layerList = new LayerListCompat({ view, container: 'layer-list-div' });");
    expect(nextSource).toContain("const legend = new LegendCompat({ view, container: 'legend-div' });");
    expect(nextSource).toContain("const popup = new PopupCompat({ view, container: 'popup-div', dockEnabled: true });");
    expect(nextSource).toContain("const home = new HomeCompat({ view, container: 'home-div' });");
    expect(nextSource).toContain(
      "const basemapToggle = new BasemapToggleCompat({ view, container: 'basemap-div', nextBasemap: 'satellite' });",
    );
    expect(nextSource).toContain("const locate = new LocateCompat({ view, container: 'locate-div' });");
    expect(nextSource).toContain("const scaleBar = new ScaleBarCompat({ view, container: 'scale-div', unit: 'dual' });");
    expect(nextSource).toContain(
      "const search = new SearchCompat({ view, container: 'search-div', includeDefaultSources: false });",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/LayerList");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Legend");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Popup");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Home");
    expect(nextSource).not.toContain("@arcgis/core/widgets/BasemapToggle");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Locate");
    expect(nextSource).not.toContain("@arcgis/core/widgets/ScaleBar");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Search");
  });

  it("rewrites deterministic constructors for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "import TileLayer from '@arcgis/core/layers/TileLayer';",
        "const fl = new FeatureLayer({ url: serviceUrl });",
        "const mil = new MapImageLayer({ url: mapUrl, visible: true });",
        "const tiled = new TileLayer({ url: tileUrl, opacity: 0.4 });",
        "void fl; void mil; void tiled;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(nextSource).toContain("const fl = HonuaEsriLeaflet.featureLayer({ url: serviceUrl });");
    expect(nextSource).toContain(
      "const mil = HonuaEsriLeaflet.dynamicMapLayer({ url: mapUrl, visible: true });",
    );
    expect(nextSource).toContain(
      "const tiled = HonuaEsriLeaflet.tiledMapLayer({ url: tileUrl, opacity: 0.4 });",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).not.toContain("@arcgis/core/layers/MapImageLayer");
    expect(nextSource).not.toContain("@arcgis/core/layers/TileLayer");
  });

  it("keeps unsupported constructors as manual TODOs for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "unsupported-for-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "const map = new Map({ basemap: 'streets' });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
      annotateTodos: true,
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.kind).toBe("map");
    expect(result.manualTodos[0]?.reason).toContain("esri-leaflet mapping");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new Map({ basemap: 'streets' })");
    expect(nextSource).toContain("// TODO(honua-migrate)[map]:");
    expect(nextSource).not.toContain("HonuaEsriLeaflet.");
    expect(nextSource).toContain("@arcgis/core/Map");
  });

  it("rewrites constructors imported via named default alias", () => {
    const root = makeTempProject();
    const file = path.join(root, "default-alias.ts");
    fs.writeFileSync(
      file,
      [
        "import { default as FeatureLayer } from '@arcgis/core/layers/FeatureLayer';",
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

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const layer = new FeatureLayerCompat({ url: serviceUrl });");
  });

  it("rewrites constructors imported via namespace default access", () => {
    const root = makeTempProject();
    const file = path.join(root, "namespace-default.ts");
    fs.writeFileSync(
      file,
      [
        "import * as FeatureLayerModule from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayerModule.default({ url: serviceUrl });",
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

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const layer = new FeatureLayerCompat({ url: serviceUrl });");
  });

  it("rewrites namespace default map constructors and drops unused arcgis import", () => {
    const root = makeTempProject();
    const file = path.join(root, "namespace-map.ts");
    fs.writeFileSync(
      file,
      [
        "import * as MapModule from '@arcgis/core/Map';",
        "const map = new MapModule.default({ basemap: 'streets' });",
        "void map;",
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
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/Map");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
  });

  it("rewrites require-default constructor expressions", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-default.ts");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map').default;",
        "const map = new Map({ basemap: 'streets' });",
        "void map;",
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
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).not.toContain("require('@arcgis/core/Map').default");
    expect(nextSource).not.toContain("const Map = require('@arcgis/core/Map').default;");
  });

  it("rewrites destructured require default constructor expressions", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-destructured.ts");
    fs.writeFileSync(
      file,
      [
        "const { default: MapCtor } = require('@arcgis/core/Map');",
        "const map = new MapCtor({ basemap: 'streets' });",
        "void map;",
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
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).not.toContain("require('@arcgis/core/Map')");
    expect(nextSource).not.toContain("const { default: MapCtor } = require('@arcgis/core/Map');");
  });

  it("keeps require constructor in .cjs and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-map.cjs");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map');",
        "const map = new Map({ basemap: 'streets' });",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.reason).toContain("CommonJS require constructors");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("const Map = require('@arcgis/core/Map');");
    expect(nextSource).toContain("const map = new Map({ basemap: 'streets' });");
    expect(nextSource).not.toContain("@honua/sdk-esri-compat");
  });

  it("keeps require constructor in CommonJS .js modules and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-map.js");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map');",
        "const map = new Map({ basemap: 'streets' });",
        "module.exports = { map };",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.reason).toContain("CommonJS require constructors");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("const Map = require('@arcgis/core/Map');");
    expect(nextSource).toContain("const map = new Map({ basemap: 'streets' });");
    expect(nextSource).toContain("module.exports = { map };");
    expect(nextSource).not.toContain("@honua/sdk-esri-compat");
  });

  it("rewrites safe Map, MapView, SceneView, and WebMap constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "view.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import SceneView from '@arcgis/core/views/SceneView';",
        "import WebMap from '@arcgis/core/WebMap';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, zoom: 4 });",
        "const scene = new SceneView({ map, viewingMode: 'global', qualityProfile: 'high' });",
        "const webMap = new WebMap({ portalItem: { id: 'abc123' } });",
        "void map; void view; void scene; void webMap;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(result.metrics.autoMigratedCallSites).toBe(4);
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
    expect(result.metrics.byKind["scene-view"]).toEqual({
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
      'import { MapCompat, MapViewCompat, SceneViewCompat, WebMapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).not.toContain("@arcgis/core/Map");
    expect(nextSource).not.toContain("@arcgis/core/views/MapView");
    expect(nextSource).not.toContain("@arcgis/core/views/SceneView");
    expect(nextSource).not.toContain("@arcgis/core/WebMap");
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).toContain("const view = new MapViewCompat({ map, zoom: 4 });");
    expect(nextSource).toContain(
      "const scene = new SceneViewCompat({ map, viewingMode: 'global', qualityProfile: 'high' });",
    );
    expect(nextSource).toContain("const webMap = new WebMapCompat({ portalItem: { id: 'abc123' } });");
  });

  it("keeps complex constructor and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "map.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, renderer: customRenderer });",
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
    expect(result.manualTodos[0].reason).toContain("unsupported properties");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayer({ url: serviceUrl, renderer: customRenderer })");
  });

  it("keeps ArcGIS import when mixed auto and manual call sites exist", () => {
    const root = makeTempProject();
    const file = path.join(root, "mixed.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const a = new FeatureLayer({ url: serviceUrl });",
        "const b = new FeatureLayer({ url: serviceUrl, renderer: customRenderer });",
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
    expect(nextSource).toContain(
      "const b = new FeatureLayer({ url: serviceUrl, renderer: customRenderer });",
    );
  });

  it("can annotate manual todos inline without duplicating markers on rerun", () => {
    const root = makeTempProject();
    const file = path.join(root, "annotated.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, renderer: customRenderer });",
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

  it("rewrites supported dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadScene() {",
        "  const module = await import('@arcgis/core/views/SceneView');",
        "  return module.default;",
        "}",
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
    expect(result.metrics.byKind["scene-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.SceneViewCompat }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/views/SceneView");
  });

  it("rewrites map and map-view dynamic imports including .js module paths", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-map.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadMapPieces() {",
        "  const mapModule = await import('@arcgis/core/Map.js');",
        "  const mapViewModule = await import('@arcgis/core/views/MapView');",
        "  return [mapModule.default, mapViewModule.default];",
        "}",
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
    expect(result.metrics.autoMigratedCallSites).toBe(2);
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

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapCompat }))',
    );
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapViewCompat }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/Map.js");
    expect(nextSource).not.toContain("@arcgis/core/views/MapView");
  });

  it("rewrites supported dynamic imports for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadLayerFactory() {",
        "  const module = await import('@arcgis/core/layers/FeatureLayer');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
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
    expect(nextSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(nextSource).toContain(
      "await Promise.resolve({ default: HonuaEsriLeaflet.featureLayer })",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
  });

  it("keeps unsupported dynamic imports as manual TODOs for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-unsupported-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadMapCtor() {",
        "  const module = await import('@arcgis/core/Map');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
      annotateTodos: true,
    });

    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos[0]).toMatchObject({
      kind: "map",
    });
    expect(result.manualTodos[0]?.reason).toContain("Dynamic import");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("@arcgis/core/Map");
    expect(nextSource).toContain("// TODO(honua-migrate)[map]:");
  });
});
