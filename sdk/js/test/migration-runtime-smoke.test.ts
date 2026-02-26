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
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-runtime-smoke-"));
  tempDirs.push(dir);
  return dir;
}

function getProjectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function getCompatDistEntry(): string {
  return path.join(getProjectRoot(), "dist", "src", "esri-compat-entry.js");
}

function ensureBuiltCompatArtifacts(): void {
  if (builtOnce && fs.existsSync(getCompatDistEntry())) {
    return;
  }

  execSync("npm run build --silent", {
    cwd: getProjectRoot(),
    stdio: "pipe",
  });
  builtOnce = true;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration runtime smoke", () => {
  it("executes migrated constructor flow with compat runtime imports", { timeout: 20_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");
    const compatEntryPath = getCompatDistEntry();

    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "const featureLayer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const mapImage = new MapImageLayer({ url: 'https://example.test/rest/services/default/MapServer' });",
        "const map = new Map({ basemap: 'streets', layers: [featureLayer, mapImage] });",
        "export default {",
        "  mapCtor: map.constructor.name,",
        "  featureLayerCtor: featureLayer.constructor.name,",
        "  mapImageCtor: mapImage.constructor.name,",
        "  layerCount: map.layers.length,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      write: true,
      compatImportPath: compatEntryPath,
    });
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);

    const migrated = await import(pathToFileURL(file).href);
    expect(migrated.default).toEqual({
      mapCtor: "MapCompat",
      featureLayerCtor: "FeatureLayerCompat",
      mapImageCtor: "MapImageLayerCompat",
      layerCount: 2,
    });
  });

  it("executes migrated widget/control flow with shared mapview ui", { timeout: 20_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "widgets.js");
    const compatEntryPath = getCompatDistEntry();

    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Search from '@arcgis/core/widgets/Search';",
        "import BasemapGallery from '@arcgis/core/widgets/BasemapGallery';",
        "import Compass from '@arcgis/core/widgets/Compass';",
        "import Expand from '@arcgis/core/widgets/Expand';",
        "import Bookmarks from '@arcgis/core/widgets/Bookmarks';",
        "import Fullscreen from '@arcgis/core/widgets/Fullscreen';",
        "import Zoom from '@arcgis/core/widgets/Zoom';",
        "import Attribution from '@arcgis/core/widgets/Attribution';",
        "import Sketch from '@arcgis/core/widgets/Sketch';",
        "import Editor from '@arcgis/core/widgets/Editor';",
        "import Track from '@arcgis/core/widgets/Track';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const layerList = new LayerList({ view, container: 'layer-list' });",
        "const legend = new Legend({ view, container: 'legend' });",
        "const popup = new Popup({ view, container: 'popup', dockEnabled: true });",
        "const search = new Search({ view, container: 'search', includeDefaultSources: false });",
        "const basemapGallery = new BasemapGallery({ view, container: 'gallery' });",
        "const compass = new Compass({ view });",
        "const expand = new Expand({ view, content: legend, expanded: false });",
        "const bookmarks = new Bookmarks({ view, bookmarks: [{ name: 'Home', target: { center: [0, 0], zoom: 2 } }] });",
        "const fullscreen = new Fullscreen({ view });",
        "const zoom = new Zoom({ view, layout: 'vertical' });",
        "const attribution = new Attribution({ view, itemDelimiter: ' | ', attributions: ['Source A'] });",
        "const sketch = new Sketch({ view, layer: undefined, creationMode: 'update' });",
        "const editor = new Editor({ view, layerInfos: [], allowedWorkflows: ['create', 'update'] });",
        "const track = new Track({ view, goToLocationEnabled: true, useHeadingEnabled: true, rotationEnabled: true });",
        "view.ui.add([layerList, legend, popup, search, basemapGallery, compass, expand, bookmarks, fullscreen, zoom, attribution, sketch, editor, track], 'top-right');",
        "export default {",
        "  mapCtor: map.constructor.name,",
        "  viewCtor: view.constructor.name,",
        "  uiCount: view.ui.getComponents().length,",
        "  widgetCtors: [",
        "    layerList.constructor.name,",
        "    legend.constructor.name,",
        "    popup.constructor.name,",
        "    search.constructor.name,",
        "    basemapGallery.constructor.name,",
        "    compass.constructor.name,",
        "    expand.constructor.name,",
        "    bookmarks.constructor.name,",
        "    fullscreen.constructor.name,",
        "    zoom.constructor.name,",
        "    attribution.constructor.name,",
        "    sketch.constructor.name,",
        "    editor.constructor.name,",
        "    track.constructor.name,",
        "  ],",
        "  bookmarkCount: bookmarks.bookmarks.length,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      write: true,
      compatImportPath: compatEntryPath,
    });
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(16);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(16);
    expect(codemodResult.metrics.manualCallSites).toBe(0);

    const migrated = await import(pathToFileURL(file).href);
    expect(migrated.default).toEqual({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      uiCount: 14,
      widgetCtors: [
        "LayerListCompat",
        "LegendCompat",
        "PopupCompat",
        "SearchCompat",
        "BasemapGalleryCompat",
        "CompassCompat",
        "ExpandCompat",
        "BookmarksCompat",
        "FullscreenCompat",
        "ZoomCompat",
        "AttributionCompat",
        "SketchCompat",
        "EditorCompat",
        "TrackCompat",
      ],
      bookmarkCount: 1,
    });
  });
});
