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
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Search from '@arcgis/core/widgets/Search';",
        "const layer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const map = new Map({ basemap: 'streets', layers: [layer] });",
        "const view = new MapView({ map, container: 'viewDiv', center: [-157.85, 21.3], zoom: 12 });",
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
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(7);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(7);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.metrics.byKind["feature-layer"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind.map).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["map-view"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["layer-list"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["legend-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["popup-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });
    expect(codemodResult.metrics.byKind["search-widget"]).toMatchObject({ autoMigrated: 1, manual: 0 });

    const migratedSource = fs.readFileSync(file, "utf8");
    expect(migratedSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(migratedSource).toContain("const layer = HonuaEsriLeaflet.featureLayer({");
    expect(migratedSource).toContain("new MapCompat({");
    expect(migratedSource).toContain("new MapViewCompat({");
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
        layerListCtor: "LayerListCompat",
        legendCtor: "LegendCompat",
        popupCtor: "PopupCompat",
        searchCtor: "SearchCompat",
        searchResultCount: 1,
        mapLayerCount: 1,
      }),
    );
  });
});
