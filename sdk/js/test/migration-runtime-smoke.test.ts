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
  it("executes migrated constructor flow with compat runtime imports", { timeout: 60_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");
    const compatEntryPath = getCompatDistEntry();

    fs.writeFileSync(
      file,
      [
        "import Basemap from '@arcgis/core/Basemap';",
        "import Map from '@arcgis/core/Map';",
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "import RouteTask from '@arcgis/core/rest/route/RouteTask';",
        "const basemap = new Basemap({ id: 'streets' });",
        "const featureLayer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const mapImage = new MapImageLayer({ url: 'https://example.test/rest/services/default/MapServer' });",
        "const routeTask = new RouteTask({ url: 'https://example.test/rest/services/network/RouteServer' });",
        "const routeResult = await routeTask.solve({ stops: [{ location: [-157.0, 21.3] }, { location: [-157.01, 21.31] }] });",
        "const map = new Map({ basemap, layers: [featureLayer, mapImage] });",
        "export default {",
        "  basemapCtor: basemap.constructor.name,",
        "  mapCtor: map.constructor.name,",
        "  featureLayerCtor: featureLayer.constructor.name,",
        "  mapImageCtor: mapImage.constructor.name,",
        "  routeTaskCtor: routeTask.constructor.name,",
        "  routeResultCount: routeResult.routeResults.length,",
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
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(5);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(5);
    expect(codemodResult.metrics.manualCallSites).toBe(0);

    const migrated = await import(pathToFileURL(file).href);
    expect(migrated.default).toEqual({
      basemapCtor: "BasemapCompat",
      mapCtor: "MapCompat",
      featureLayerCtor: "FeatureLayerCompat",
      mapImageCtor: "MapImageLayerCompat",
      routeTaskCtor: "RouteTaskCompat",
      routeResultCount: 1,
      layerCount: 2,
    });
  });

  it("executes migrated widget/control flow with shared mapview ui", { timeout: 60_000 }, async () => {
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
        "import Feature from '@arcgis/core/widgets/Feature';",
        "import FeatureForm from '@arcgis/core/widgets/FeatureForm';",
        "import FeatureTemplates from '@arcgis/core/widgets/FeatureTemplates';",
        "import TableList from '@arcgis/core/widgets/TableList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Home from '@arcgis/core/widgets/Home';",
        "import BasemapToggle from '@arcgis/core/widgets/BasemapToggle';",
        "import Locate from '@arcgis/core/widgets/Locate';",
        "import ScaleBar from '@arcgis/core/widgets/ScaleBar';",
        "import Search from '@arcgis/core/widgets/Search';",
        "import BasemapLayerList from '@arcgis/core/widgets/BasemapLayerList';",
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
        "import DistanceMeasurement2D from '@arcgis/core/widgets/DistanceMeasurement2D';",
        "import AreaMeasurement2D from '@arcgis/core/widgets/AreaMeasurement2D';",
        "import Measurement from '@arcgis/core/widgets/Measurement';",
        "import TimeSlider from '@arcgis/core/widgets/TimeSlider';",
        "import RouteLayer from '@arcgis/core/layers/RouteLayer';",
        "import Directions from '@arcgis/core/widgets/Directions';",
        "import CoordinateConversion from '@arcgis/core/widgets/CoordinateConversion';",
        "import Print from '@arcgis/core/widgets/Print';",
        "import Swipe from '@arcgis/core/widgets/Swipe';",
        "const map = new Map({ basemap: { id: 'streets', baseLayers: [{ id: 'base-layer' }], referenceLayers: [{ id: 'ref-layer' }] }, tables: [{ id: 'parcel-table' }] });",
        "const routeLayer = new RouteLayer({ stops: [{ name: 'Start', location: [-157.0, 21.3] }, { name: 'End', location: [-157.01, 21.31] }] });",
        "map.add(routeLayer);",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const layerList = new LayerList({ view, container: 'layer-list' });",
        "const tableList = new TableList({ view, container: 'table-list', tables: [{ id: 'parcels' }] });",
        "const featureWidget = new Feature({ view, container: 'feature-widget', title: 'Selected', graphic: { attributes: { OBJECTID: 1 } } });",
        "const featureForm = new FeatureForm({ layer: routeLayer, container: 'feature-form', feature: { attributes: { OBJECTID: 1 } }, fieldConfig: [{ name: 'status' }] });",
        "const featureTemplates = new FeatureTemplates({ layerInfos: [{ layer: routeLayer }], container: 'feature-templates', filterFunction: (item) => item.name !== 'Restricted' });",
        "const legend = new Legend({ view, container: 'legend' });",
        "const popup = new Popup({ view, container: 'popup', dockEnabled: true });",
        "const home = new Home({ view, container: 'home-div', viewpoint: { center: [0, 0], zoom: 3 } });",
        "const basemapToggle = new BasemapToggle({ view, container: 'basemap-toggle-div', nextBasemap: { id: 'satellite', baseLayers: [{ id: 'sat-base' }] } });",
        "const locate = new Locate({ view, container: 'locate-div', zoom: 12, locateProvider: async () => ({ coords: { latitude: 21.3069, longitude: -157.8583 } }) });",
        "const scaleBar = new ScaleBar({ view, container: 'scalebar-div', unit: 'dual' });",
        "const search = new Search({ view, container: 'search', includeDefaultSources: false, autoNavigate: false, sources: [",
        "  { search: async ({ searchTerm }) => [{ name: `Primary ${searchTerm}`, location: { x: 1, y: 2 } }] },",
        "  { search: async ({ searchTerm }) => [{ name: `Secondary ${searchTerm}`, location: { x: 3, y: 4 } }] },",
        "] });",
        "const basemapLayerList = new BasemapLayerList({ view, container: 'basemap-layer-list' });",
        "const basemapGallery = new BasemapGallery({ view, container: 'gallery' });",
        "const compass = new Compass({ view });",
        "const expand = new Expand({ view, content: legend, expanded: false });",
        "const bookmarks = new Bookmarks({ view, bookmarks: [{ name: 'Home', target: { center: [0, 0], zoom: 2 } }] });",
        "const fullscreen = new Fullscreen({ view });",
        "const zoom = new Zoom({ view, layout: 'vertical' });",
        "const attribution = new Attribution({ view, itemDelimiter: ' | ', attributions: ['Source A'] });",
        "const sketch = new Sketch({ view, layer: undefined, creationMode: 'update' });",
        "const editor = new Editor({ view, layerInfos: [], allowedWorkflows: ['create', 'update'] });",
        "const track = new Track({ view, goToLocationEnabled: true, useHeadingEnabled: true, rotationEnabled: true, trackProvider: async () => ({ coords: { latitude: 21.3069, longitude: -157.8583 } }) });",
        "const distanceMeasurement2d = new DistanceMeasurement2D({ view, container: 'distance-2d', unit: 'kilometers' });",
        "const areaMeasurement2d = new AreaMeasurement2D({ view, container: 'area-2d', unit: 'square-kilometers' });",
        "const measurement = new Measurement({ view, activeTool: 'distance', linearUnit: 'kilometers', areaUnit: 'square-kilometers' });",
        "const timeSlider = new TimeSlider({ view, mode: 'instant', stops: { values: ['2024-01-01T00:00:00.000Z', '2024-02-01T00:00:00.000Z'] } });",
        "const directions = new Directions({ view, layer: routeLayer, useDefaultRouteLayer: false, showSaveAsButton: false });",
        "const coordinateConversion = new CoordinateConversion({ view, mode: 'live', multipleConversionsEnabled: true, formats: ['lonlat', 'dms'] });",
        "const printer = new Print({ view, container: 'print', printServiceUrl: 'https://example.test/print', templateOptions: { format: 'pdf', layout: 'a4-landscape' } });",
        "const swipe = new Swipe({ view, container: 'swipe', position: 45, leadingLayers: [], trailingLayers: [] });",
        "featureTemplates.setTemplates([{ id: 'open', name: 'Open' }, { id: 'restricted', name: 'Restricted' }]);",
        "const selectedTemplate = featureTemplates.selectTemplate('open');",
        "const featureFormSubmit = await featureForm.submit({ status: 'approved' });",
        "popup.open({ title: 'Runtime', content: 'Smoke', features: [{ id: 'a' }, { id: 'b' }] });",
        "const popupBefore = popup.selectedFeature;",
        "popup.next();",
        "const popupAfterNext = popup.selectedFeature;",
        "await home.go();",
        "const toggledBasemap = basemapToggle.toggle();",
        "const locateResult = await locate.locate();",
        "const scaleText = scaleBar.refresh();",
        "map.setTables([{ id: 'roads-table' }, { id: 'zoning-table' }]);",
        "const mapTableCount = tableList.useMapTables().length;",
        "const searchResponse = await search.search('honua');",
        "await search.nextResult();",
        "const tracked = await track.start();",
        "const solvedRoute = await directions.solve();",
        "const directionsSummary = directions.getSummary();",
        "view.ui.add([layerList, tableList, featureWidget, featureForm, featureTemplates, legend, popup, home, basemapToggle, locate, scaleBar, search, basemapLayerList, basemapGallery, compass, expand, bookmarks, fullscreen, zoom, attribution, sketch, editor, track, distanceMeasurement2d, areaMeasurement2d, measurement, timeSlider, directions, coordinateConversion, printer, swipe], 'top-right');",
        "export default {",
        "  mapCtor: map.constructor.name,",
        "  viewCtor: view.constructor.name,",
        "  uiCount: view.ui.getComponents().length,",
        "  widgetCtors: [",
        "    layerList.constructor.name,",
        "    tableList.constructor.name,",
        "    featureWidget.constructor.name,",
        "    featureForm.constructor.name,",
        "    featureTemplates.constructor.name,",
        "    legend.constructor.name,",
        "    popup.constructor.name,",
        "    home.constructor.name,",
        "    basemapToggle.constructor.name,",
        "    locate.constructor.name,",
        "    scaleBar.constructor.name,",
        "    search.constructor.name,",
        "    basemapLayerList.constructor.name,",
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
        "    distanceMeasurement2d.constructor.name,",
        "    areaMeasurement2d.constructor.name,",
        "    measurement.constructor.name,",
        "    timeSlider.constructor.name,",
        "    directions.constructor.name,",
        "    coordinateConversion.constructor.name,",
        "    printer.constructor.name,",
        "    swipe.constructor.name,",
        "  ],",
        "  routeLayerCtor: routeLayer.constructor.name,",
        "  bookmarkCount: bookmarks.bookmarks.length,",
        "  popupBefore,",
        "  popupAfterNext,",
        "  toggledBasemapId: toggledBasemap?.id ?? toggledBasemap,",
        "  locateLongitude: locateResult.coords.longitude,",
        "  locateLatitude: locateResult.coords.latitude,",
        "  scaleText,",
        "  mapTableCount,",
        "  searchResultCount: searchResponse.results.length,",
        "  searchSelectedResult: search.selectedResult?.name,",
        "  templateCount: featureTemplates.templates.length,",
        "  selectedTemplateName: selectedTemplate?.name,",
        "  featureFormStatus: featureFormSubmit.values.status,",
        "  trackedLongitude: tracked.coords.longitude,",
        "  trackedLatitude: tracked.coords.latitude,",
        "  solvedRoutePathPoints: solvedRoute?.path?.length ?? 0,",
        "  directionsStopCount: directionsSummary?.stopCount ?? 0,",
        "  directionsDistanceMeters: directionsSummary?.distanceMeters ?? 0,",
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
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(34);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(34);
    expect(codemodResult.metrics.manualCallSites).toBe(0);

    const migrated = await import(pathToFileURL(file).href);
    expect(migrated.default).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      uiCount: 31,
      routeLayerCtor: "RouteLayerCompat",
      bookmarkCount: 1,
      popupBefore: { id: "a" },
      popupAfterNext: { id: "b" },
      toggledBasemapId: "satellite",
      locateLongitude: -157.8583,
      locateLatitude: 21.3069,
      mapTableCount: 2,
      searchResultCount: 2,
      searchSelectedResult: "Secondary honua",
      templateCount: 1,
      selectedTemplateName: "Open",
      featureFormStatus: "approved",
      trackedLongitude: -157.8583,
      trackedLatitude: 21.3069,
      solvedRoutePathPoints: 2,
      directionsStopCount: 2,
      scaleText: expect.stringContaining("1:"),
      directionsDistanceMeters: expect.any(Number),
      widgetCtors: [
        "LayerListCompat",
        "TableListCompat",
        "FeatureCompat",
        "FeatureFormCompat",
        "FeatureTemplatesCompat",
        "LegendCompat",
        "PopupCompat",
        "HomeCompat",
        "BasemapToggleCompat",
        "LocateCompat",
        "ScaleBarCompat",
        "SearchCompat",
        "BasemapLayerListCompat",
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
        "DistanceMeasurement2DCompat",
        "AreaMeasurement2DCompat",
        "MeasurementCompat",
        "TimeSliderCompat",
        "DirectionsCompat",
        "CoordinateConversionCompat",
        "PrintCompat",
        "SwipeCompat",
      ],
    });
    expect(migrated.default.scaleText).toContain(" / ");
    expect(migrated.default.directionsDistanceMeters).toBeGreaterThan(0);
  });
});
