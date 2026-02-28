import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-esri-leaflet-runtime-"));
  tempDirs.push(dir);
  return dir;
}

function projectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function compatDistEntry(): string {
  return path.join(projectRoot(), "dist", "src", "esri-compat-entry.js");
}

function ensureBuiltCompatArtifacts(): void {
  if (builtOnce && fs.existsSync(compatDistEntry())) {
    return;
  }

  execSync("npm run build --silent", {
    cwd: projectRoot(),
    stdio: "pipe",
  });
  builtOnce = true;
}

function writeEsriLeafletStub(moduleRoot: string): void {
  fs.mkdirSync(moduleRoot, { recursive: true });
  fs.writeFileSync(
    path.join(moduleRoot, "package.json"),
    JSON.stringify(
      {
        name: "esri-leaflet",
        version: "0.0.0-test",
        type: "module",
        exports: "./index.js",
      },
      null,
      2,
    ),
    "utf8",
  );
  fs.writeFileSync(
    path.join(moduleRoot, "index.js"),
    [
      "class EsriLeafletFeatureLayerStub {",
      "  constructor(options = {}) {",
      "    this.options = options;",
      "    this.url = options.url ?? null;",
      "    this.source = 'esri-leaflet';",
      "    this.kind = 'feature';",
      "  }",
      "}",
      "class EsriLeafletDynamicMapLayerStub {",
      "  constructor(options = {}) {",
      "    this.options = options;",
      "    this.url = options.url ?? null;",
      "    this.source = 'esri-leaflet';",
      "    this.kind = 'dynamic-map';",
      "  }",
      "}",
      "class EsriLeafletTiledMapLayerStub {",
      "  constructor(options = {}) {",
      "    this.options = options;",
      "    this.url = options.url ?? null;",
      "    this.source = 'esri-leaflet';",
      "    this.kind = 'tiled-map';",
      "  }",
      "}",
      "export function featureLayer(options) { return new EsriLeafletFeatureLayerStub(options); }",
      "export function dynamicMapLayer(options) { return new EsriLeafletDynamicMapLayerStub(options); }",
      "export function tiledMapLayer(options) { return new EsriLeafletTiledMapLayerStub(options); }",
    ].join("\n"),
    "utf8",
  );
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration esri-leaflet runtime", () => {
  it("executes migrated map/view/widget flow with esri-leaflet target and compat fallbacks", { timeout: 60_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");

    writeEsriLeafletStub(path.join(tempRoot, "node_modules", "esri-leaflet"));

    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import SceneView from '@arcgis/core/views/SceneView';",
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Search from '@arcgis/core/widgets/Search';",
        "const layer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const map = new Map({ basemap: 'streets', layers: [layer] });",
        "const view = new MapView({ map, container: 'viewDiv', center: [-157.85, 21.3], zoom: 12 });",
        "const scene = new SceneView({ map, container: 'sceneDiv', viewingMode: 'global' });",
        "const layerList = new LayerList({ view });",
        "const legend = new Legend({ view });",
        "const popup = new Popup({ view });",
        "const search = new Search({",
        "  view,",
        "  includeDefaultSources: false,",
        "  autoNavigate: false,",
        "  sources: [",
        "    { search: async ({ searchTerm }) => [{ name: `Result ${searchTerm}`, location: { x: 1, y: 2 } }] },",
        "  ],",
        "});",
        "popup.open({ title: 'Runtime', content: 'Smoke', features: [{ id: 1 }] });",
        "const searchResult = await search.search('honua');",
        "export default {",
        "  layerCtor: layer.constructor.name,",
        "  layerSource: layer.source,",
        "  mapCtor: map.constructor.name,",
        "  viewCtor: view.constructor.name,",
        "  sceneViewCtor: scene.constructor.name,",
        "  layerListCtor: layerList.constructor.name,",
        "  legendCtor: legend.constructor.name,",
        "  popupCtor: popup.constructor.name,",
        "  searchCtor: search.constructor.name,",
        "  searchResultCount: searchResult.results.length,",
        "  mapLayerCount: map.layers.length,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      target: "esri-leaflet",
      write: true,
      compatImportPath: compatDistEntry(),
    });

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(8);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(8);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind.map).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["map-view"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["scene-view"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["layer-list"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["legend-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["popup-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["search-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });

    const migratedSource = fs.readFileSync(file, "utf8");
    expect(migratedSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(migratedSource).toContain("const layer = HonuaEsriLeaflet.featureLayer({");
    expect(migratedSource).toContain("new MapCompat({");
    expect(migratedSource).toContain("new MapViewCompat({");
    expect(migratedSource).toContain("new SceneViewCompat({");
    expect(migratedSource).toContain("new LayerListCompat({");
    expect(migratedSource).toContain("new LegendCompat({");
    expect(migratedSource).toContain("new PopupCompat({");
    expect(migratedSource).toContain("new SearchCompat({");

    const moduleUrl = `${pathToFileURL(file).href}?cachebust=${Date.now()}`;
    const migrated = await import(moduleUrl);
    expect(migrated.default).toEqual(
      expect.objectContaining({
        layerCtor: "EsriLeafletFeatureLayerStub",
        layerSource: "esri-leaflet",
        mapCtor: "MapCompat",
        viewCtor: "MapViewCompat",
        sceneViewCtor: "SceneViewCompat",
        layerListCtor: "LayerListCompat",
        legendCtor: "LegendCompat",
        popupCtor: "PopupCompat",
        searchCtor: "SearchCompat",
        searchResultCount: 1,
        mapLayerCount: 1,
      }),
    );
  });

  it("uses compat layer fallbacks when advanced layer methods are present", { timeout: 60_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");

    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "globalThis.fetch = async (input) => {",
        "  const url = String(input);",
        "  if (url.includes('/FeatureServer/0/query')) {",
        "    return new Response(JSON.stringify({ objectIds: [7, 8, 9] }), { status: 200 });",
        "  }",
        "  if (url.includes('/MapServer/identify')) {",
        "    return new Response(JSON.stringify({ results: [{ layerId: 0, value: 'match' }] }), { status: 200 });",
        "  }",
        "  return new Response(JSON.stringify({ ok: true }), { status: 200 });",
        "};",
        "const layer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const mapImage = new MapImageLayer({ url: 'https://example.test/rest/services/default/MapServer' });",
        "const ids = await layer.queryObjectIds({ where: '1=1' });",
        "const identify = await mapImage.identify({",
        "  geometry: { x: 1, y: 2 },",
        "  mapExtent: '0,0,10,10',",
        "  imageDisplay: '800,600,96',",
        "});",
        "export default {",
        "  layerCtor: layer.constructor.name,",
        "  mapImageCtor: mapImage.constructor.name,",
        "  ids,",
        "  identifyCount: Array.isArray(identify.results) ? identify.results.length : 0,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      target: "esri-leaflet",
      write: true,
      compatImportPath: compatDistEntry(),
    });

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["map-image-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });

    const migratedSource = fs.readFileSync(file, "utf8");
    expect(migratedSource).toContain("new FeatureLayerCompat({");
    expect(migratedSource).toContain("new MapImageLayerCompat({");
    expect(migratedSource).not.toContain("HonuaEsriLeaflet.featureLayer");
    expect(migratedSource).not.toContain("HonuaEsriLeaflet.dynamicMapLayer");

    const moduleUrl = `${pathToFileURL(file).href}?cachebust=${Date.now()}`;
    const migrated = await import(moduleUrl);
    expect(migrated.default).toEqual(
      expect.objectContaining({
        layerCtor: "FeatureLayerCompat",
        mapImageCtor: "MapImageLayerCompat",
        ids: [7, 8, 9],
        identifyCount: 1,
      }),
    );
  });

  it("uses compat fallbacks when query and sublayer helpers are present", { timeout: 60_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");

    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "globalThis.fetch = async (input) => {",
        "  const url = String(input);",
        "  if (url.includes('/FeatureServer/0/query')) {",
        "    return new Response(JSON.stringify({ features: [{ attributes: { OBJECTID: 1 } }] }), { status: 200 });",
        "  }",
        "  if (url.includes('/MapServer/0/query')) {",
        "    return new Response(JSON.stringify({ features: [{ attributes: { OBJECTID: 2 } }] }), { status: 200 });",
        "  }",
        "  return new Response(JSON.stringify({ ok: true }), { status: 200 });",
        "};",
        "const layer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const mapImage = new MapImageLayer({ url: 'https://example.test/rest/services/default/MapServer', sublayers: [{ id: 0, title: 'Roads' }] });",
        "const features = await layer.queryFeatures({ where: '1=1' });",
        "const sublayer = mapImage.sublayer(0);",
        "const subFeatures = await sublayer?.queryFeatures({ where: '1=1' });",
        "export default {",
        "  layerCtor: layer.constructor.name,",
        "  mapImageCtor: mapImage.constructor.name,",
        "  featureCount: Array.isArray(features.features) ? features.features.length : 0,",
        "  sublayerCtor: sublayer?.constructor?.name,",
        "  subFeatureCount: subFeatures && Array.isArray(subFeatures.features) ? subFeatures.features.length : 0,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      target: "esri-leaflet",
      write: true,
      compatImportPath: compatDistEntry(),
    });

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(2);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["map-image-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });

    const migratedSource = fs.readFileSync(file, "utf8");
    expect(migratedSource).toContain("new FeatureLayerCompat({");
    expect(migratedSource).toContain("new MapImageLayerCompat({");
    expect(migratedSource).not.toContain("HonuaEsriLeaflet.featureLayer");
    expect(migratedSource).not.toContain("HonuaEsriLeaflet.dynamicMapLayer");

    const moduleUrl = `${pathToFileURL(file).href}?cachebust=${Date.now()}`;
    const migrated = await import(moduleUrl);
    expect(migrated.default).toEqual(
      expect.objectContaining({
        layerCtor: "FeatureLayerCompat",
        mapImageCtor: "MapImageLayerCompat",
        featureCount: 1,
        sublayerCtor: "MapImageSublayerCompat",
        subFeatureCount: 1,
      }),
    );
  });
});
