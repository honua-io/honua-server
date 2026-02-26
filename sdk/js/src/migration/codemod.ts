import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const SOURCE_EXTENSIONS = new Set([".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
const SKIP_DIRS = new Set(["node_modules", "dist", ".git"]);
const DEFAULT_COMPAT_IMPORT_PATH = "@honua/sdk-esri-compat";
const ESRI_LEAFLET_IMPORT_PATH = "esri-leaflet";
const ESRI_LEAFLET_NAMESPACE = "HonuaEsriLeaflet";
const TODO_MARKER = "TODO(honua-migrate)";
const CJS_REQUIRE_MANUAL_REASON =
  "CommonJS require constructors are not auto-migrated; convert the module to ESM and rerun.";
const ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON =
  "No deterministic esri-leaflet mapping for this constructor; requires manual migration.";
const ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON =
  "Dynamic import has no deterministic esri-leaflet mapping; requires manual migration.";
const REACTIVE_UTILS_IMPORT_UNSUPPORTED_REASON =
  "ReactiveUtils import shape is unsupported for automatic migration.";
const ESRI_CONFIG_IMPORT_UNSUPPORTED_REASON =
  "esriConfig import shape is unsupported for automatic migration.";
const IDENTITY_MANAGER_IMPORT_UNSUPPORTED_REASON =
  "IdentityManager import shape is unsupported for automatic migration.";
const ESRI_REQUEST_IMPORT_UNSUPPORTED_REASON =
  "esriRequest import shape is unsupported for automatic migration.";

export type CodemodTarget = "honua-compat" | "esri-leaflet";

export type CodemodConstructorKind =
  | "feature-layer"
  | "graphic"
  | "point-geometry"
  | "polyline-geometry"
  | "polygon-geometry"
  | "extent-geometry"
  | "spatial-reference"
  | "color"
  | "simple-line-symbol"
  | "simple-marker-symbol"
  | "simple-fill-symbol"
  | "class-breaks-renderer"
  | "simple-renderer"
  | "unique-value-renderer"
  | "graphics-layer"
  | "group-layer"
  | "map-image-layer"
  | "tile-layer"
  | "route-layer"
  | "route-task"
  | "basemap"
  | "map"
  | "map-view"
  | "scene-view"
  | "web-map"
  | "layer-list"
  | "table-list-widget"
  | "feature-widget"
  | "feature-templates-widget"
  | "feature-form-widget"
  | "feature-table-widget"
  | "feature-set"
  | "legend-widget"
  | "popup-widget"
  | "popup-template"
  | "swipe-widget"
  | "print-widget"
  | "home-widget"
  | "basemap-toggle-widget"
  | "locate-widget"
  | "scale-bar-widget"
  | "search-widget"
  | "basemap-layer-list-widget"
  | "basemap-gallery-widget"
  | "expand-widget"
  | "compass-widget"
  | "bookmarks-widget"
  | "fullscreen-widget"
  | "zoom-widget"
  | "attribution-widget"
  | "sketch-widget"
  | "editor-widget"
  | "track-widget"
  | "distance-measurement-2d-widget"
  | "area-measurement-2d-widget"
  | "measurement-widget"
  | "time-slider-widget"
  | "directions-widget"
  | "coordinate-conversion-widget"
  | "query"
  | "oauth-info"
  | "identity-manager"
  | "esri-request"
  | "esri-config"
  | "reactive-utils";

interface ConstructorRewriteSpec {
  kind: CodemodConstructorKind;
  compatSymbol: string;
  arcGisModules: ReadonlySet<string>;
}

const REWRITE_SPECS: readonly ConstructorRewriteSpec[] = [
  {
    kind: "feature-layer",
    compatSymbol: "FeatureLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/FeatureLayer",
      "@arcgis/core/layers/FeatureLayer.js",
    ]),
  },
  {
    kind: "graphic",
    compatSymbol: "GraphicCompat",
    arcGisModules: new Set(["@arcgis/core/Graphic", "@arcgis/core/Graphic.js"]),
  },
  {
    kind: "point-geometry",
    compatSymbol: "PointCompat",
    arcGisModules: new Set(["@arcgis/core/geometry/Point", "@arcgis/core/geometry/Point.js"]),
  },
  {
    kind: "polyline-geometry",
    compatSymbol: "PolylineCompat",
    arcGisModules: new Set(["@arcgis/core/geometry/Polyline", "@arcgis/core/geometry/Polyline.js"]),
  },
  {
    kind: "polygon-geometry",
    compatSymbol: "PolygonCompat",
    arcGisModules: new Set(["@arcgis/core/geometry/Polygon", "@arcgis/core/geometry/Polygon.js"]),
  },
  {
    kind: "extent-geometry",
    compatSymbol: "ExtentCompat",
    arcGisModules: new Set(["@arcgis/core/geometry/Extent", "@arcgis/core/geometry/Extent.js"]),
  },
  {
    kind: "spatial-reference",
    compatSymbol: "SpatialReferenceCompat",
    arcGisModules: new Set([
      "@arcgis/core/geometry/SpatialReference",
      "@arcgis/core/geometry/SpatialReference.js",
    ]),
  },
  {
    kind: "color",
    compatSymbol: "ColorCompat",
    arcGisModules: new Set(["@arcgis/core/Color", "@arcgis/core/Color.js"]),
  },
  {
    kind: "simple-line-symbol",
    compatSymbol: "SimpleLineSymbolCompat",
    arcGisModules: new Set([
      "@arcgis/core/symbols/SimpleLineSymbol",
      "@arcgis/core/symbols/SimpleLineSymbol.js",
    ]),
  },
  {
    kind: "simple-marker-symbol",
    compatSymbol: "SimpleMarkerSymbolCompat",
    arcGisModules: new Set([
      "@arcgis/core/symbols/SimpleMarkerSymbol",
      "@arcgis/core/symbols/SimpleMarkerSymbol.js",
    ]),
  },
  {
    kind: "simple-fill-symbol",
    compatSymbol: "SimpleFillSymbolCompat",
    arcGisModules: new Set([
      "@arcgis/core/symbols/SimpleFillSymbol",
      "@arcgis/core/symbols/SimpleFillSymbol.js",
    ]),
  },
  {
    kind: "class-breaks-renderer",
    compatSymbol: "ClassBreaksRendererCompat",
    arcGisModules: new Set([
      "@arcgis/core/renderers/ClassBreaksRenderer",
      "@arcgis/core/renderers/ClassBreaksRenderer.js",
    ]),
  },
  {
    kind: "simple-renderer",
    compatSymbol: "SimpleRendererCompat",
    arcGisModules: new Set([
      "@arcgis/core/renderers/SimpleRenderer",
      "@arcgis/core/renderers/SimpleRenderer.js",
    ]),
  },
  {
    kind: "unique-value-renderer",
    compatSymbol: "UniqueValueRendererCompat",
    arcGisModules: new Set([
      "@arcgis/core/renderers/UniqueValueRenderer",
      "@arcgis/core/renderers/UniqueValueRenderer.js",
    ]),
  },
  {
    kind: "graphics-layer",
    compatSymbol: "GraphicsLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/GraphicsLayer",
      "@arcgis/core/layers/GraphicsLayer.js",
    ]),
  },
  {
    kind: "group-layer",
    compatSymbol: "GroupLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/GroupLayer",
      "@arcgis/core/layers/GroupLayer.js",
    ]),
  },
  {
    kind: "map-image-layer",
    compatSymbol: "MapImageLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/MapImageLayer",
      "@arcgis/core/layers/MapImageLayer.js",
    ]),
  },
  {
    kind: "tile-layer",
    compatSymbol: "TileLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/TileLayer",
      "@arcgis/core/layers/TileLayer.js",
    ]),
  },
  {
    kind: "route-layer",
    compatSymbol: "RouteLayerCompat",
    arcGisModules: new Set([
      "@arcgis/core/layers/RouteLayer",
      "@arcgis/core/layers/RouteLayer.js",
    ]),
  },
  {
    kind: "route-task",
    compatSymbol: "RouteTaskCompat",
    arcGisModules: new Set([
      "@arcgis/core/rest/route/RouteTask",
      "@arcgis/core/rest/route/RouteTask.js",
    ]),
  },
  {
    kind: "basemap",
    compatSymbol: "BasemapCompat",
    arcGisModules: new Set(["@arcgis/core/Basemap", "@arcgis/core/Basemap.js"]),
  },
  {
    kind: "map",
    compatSymbol: "MapCompat",
    arcGisModules: new Set(["@arcgis/core/Map", "@arcgis/core/Map.js"]),
  },
  {
    kind: "map-view",
    compatSymbol: "MapViewCompat",
    arcGisModules: new Set([
      "@arcgis/core/views/MapView",
      "@arcgis/core/views/MapView.js",
    ]),
  },
  {
    kind: "web-map",
    compatSymbol: "WebMapCompat",
    arcGisModules: new Set(["@arcgis/core/WebMap", "@arcgis/core/WebMap.js"]),
  },
  {
    kind: "scene-view",
    compatSymbol: "SceneViewCompat",
    arcGisModules: new Set([
      "@arcgis/core/views/SceneView",
      "@arcgis/core/views/SceneView.js",
    ]),
  },
  {
    kind: "layer-list",
    compatSymbol: "LayerListCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/LayerList",
      "@arcgis/core/widgets/LayerList.js",
    ]),
  },
  {
    kind: "table-list-widget",
    compatSymbol: "TableListCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/TableList",
      "@arcgis/core/widgets/TableList.js",
    ]),
  },
  {
    kind: "feature-widget",
    compatSymbol: "FeatureCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Feature",
      "@arcgis/core/widgets/Feature.js",
    ]),
  },
  {
    kind: "feature-templates-widget",
    compatSymbol: "FeatureTemplatesCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/FeatureTemplates",
      "@arcgis/core/widgets/FeatureTemplates.js",
    ]),
  },
  {
    kind: "feature-form-widget",
    compatSymbol: "FeatureFormCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/FeatureForm",
      "@arcgis/core/widgets/FeatureForm.js",
    ]),
  },
  {
    kind: "feature-table-widget",
    compatSymbol: "FeatureTableCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/FeatureTable",
      "@arcgis/core/widgets/FeatureTable.js",
    ]),
  },
  {
    kind: "feature-set",
    compatSymbol: "FeatureSetCompat",
    arcGisModules: new Set([
      "@arcgis/core/rest/support/FeatureSet",
      "@arcgis/core/rest/support/FeatureSet.js",
    ]),
  },
  {
    kind: "legend-widget",
    compatSymbol: "LegendCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Legend",
      "@arcgis/core/widgets/Legend.js",
    ]),
  },
  {
    kind: "popup-widget",
    compatSymbol: "PopupCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Popup",
      "@arcgis/core/widgets/Popup.js",
    ]),
  },
  {
    kind: "popup-template",
    compatSymbol: "PopupTemplateCompat",
    arcGisModules: new Set([
      "@arcgis/core/PopupTemplate",
      "@arcgis/core/PopupTemplate.js",
    ]),
  },
  {
    kind: "swipe-widget",
    compatSymbol: "SwipeCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Swipe",
      "@arcgis/core/widgets/Swipe.js",
    ]),
  },
  {
    kind: "print-widget",
    compatSymbol: "PrintCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Print",
      "@arcgis/core/widgets/Print.js",
    ]),
  },
  {
    kind: "home-widget",
    compatSymbol: "HomeCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Home",
      "@arcgis/core/widgets/Home.js",
    ]),
  },
  {
    kind: "basemap-toggle-widget",
    compatSymbol: "BasemapToggleCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/BasemapToggle",
      "@arcgis/core/widgets/BasemapToggle.js",
    ]),
  },
  {
    kind: "locate-widget",
    compatSymbol: "LocateCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Locate",
      "@arcgis/core/widgets/Locate.js",
    ]),
  },
  {
    kind: "scale-bar-widget",
    compatSymbol: "ScaleBarCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/ScaleBar",
      "@arcgis/core/widgets/ScaleBar.js",
    ]),
  },
  {
    kind: "search-widget",
    compatSymbol: "SearchCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Search",
      "@arcgis/core/widgets/Search.js",
    ]),
  },
  {
    kind: "basemap-layer-list-widget",
    compatSymbol: "BasemapLayerListCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/BasemapLayerList",
      "@arcgis/core/widgets/BasemapLayerList.js",
    ]),
  },
  {
    kind: "basemap-gallery-widget",
    compatSymbol: "BasemapGalleryCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/BasemapGallery",
      "@arcgis/core/widgets/BasemapGallery.js",
    ]),
  },
  {
    kind: "expand-widget",
    compatSymbol: "ExpandCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Expand",
      "@arcgis/core/widgets/Expand.js",
    ]),
  },
  {
    kind: "compass-widget",
    compatSymbol: "CompassCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Compass",
      "@arcgis/core/widgets/Compass.js",
    ]),
  },
  {
    kind: "bookmarks-widget",
    compatSymbol: "BookmarksCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Bookmarks",
      "@arcgis/core/widgets/Bookmarks.js",
    ]),
  },
  {
    kind: "fullscreen-widget",
    compatSymbol: "FullscreenCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Fullscreen",
      "@arcgis/core/widgets/Fullscreen.js",
    ]),
  },
  {
    kind: "zoom-widget",
    compatSymbol: "ZoomCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Zoom",
      "@arcgis/core/widgets/Zoom.js",
    ]),
  },
  {
    kind: "attribution-widget",
    compatSymbol: "AttributionCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Attribution",
      "@arcgis/core/widgets/Attribution.js",
    ]),
  },
  {
    kind: "sketch-widget",
    compatSymbol: "SketchCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Sketch",
      "@arcgis/core/widgets/Sketch.js",
    ]),
  },
  {
    kind: "editor-widget",
    compatSymbol: "EditorCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Editor",
      "@arcgis/core/widgets/Editor.js",
    ]),
  },
  {
    kind: "track-widget",
    compatSymbol: "TrackCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Track",
      "@arcgis/core/widgets/Track.js",
    ]),
  },
  {
    kind: "distance-measurement-2d-widget",
    compatSymbol: "DistanceMeasurement2DCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/DistanceMeasurement2D",
      "@arcgis/core/widgets/DistanceMeasurement2D.js",
    ]),
  },
  {
    kind: "area-measurement-2d-widget",
    compatSymbol: "AreaMeasurement2DCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/AreaMeasurement2D",
      "@arcgis/core/widgets/AreaMeasurement2D.js",
    ]),
  },
  {
    kind: "measurement-widget",
    compatSymbol: "MeasurementCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Measurement",
      "@arcgis/core/widgets/Measurement.js",
    ]),
  },
  {
    kind: "time-slider-widget",
    compatSymbol: "TimeSliderCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/TimeSlider",
      "@arcgis/core/widgets/TimeSlider.js",
    ]),
  },
  {
    kind: "directions-widget",
    compatSymbol: "DirectionsCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/Directions",
      "@arcgis/core/widgets/Directions.js",
    ]),
  },
  {
    kind: "coordinate-conversion-widget",
    compatSymbol: "CoordinateConversionCompat",
    arcGisModules: new Set([
      "@arcgis/core/widgets/CoordinateConversion",
      "@arcgis/core/widgets/CoordinateConversion.js",
    ]),
  },
  {
    kind: "query",
    compatSymbol: "QueryCompat",
    arcGisModules: new Set([
      "@arcgis/core/rest/support/Query",
      "@arcgis/core/rest/support/Query.js",
    ]),
  },
  {
    kind: "oauth-info",
    compatSymbol: "OAuthInfoCompat",
    arcGisModules: new Set([
      "@arcgis/core/identity/OAuthInfo",
      "@arcgis/core/identity/OAuthInfo.js",
    ]),
  },
  {
    kind: "identity-manager",
    compatSymbol: "identityManager",
    arcGisModules: new Set([
      "@arcgis/core/identity/IdentityManager",
      "@arcgis/core/identity/IdentityManager.js",
    ]),
  },
  {
    kind: "esri-request",
    compatSymbol: "esriRequest",
    arcGisModules: new Set(["@arcgis/core/request", "@arcgis/core/request.js"]),
  },
  {
    kind: "esri-config",
    compatSymbol: "esriConfig",
    arcGisModules: new Set(["@arcgis/core/config", "@arcgis/core/config.js"]),
  },
  {
    kind: "reactive-utils",
    compatSymbol: "reactiveUtils",
    arcGisModules: new Set([
      "@arcgis/core/core/reactiveUtils",
      "@arcgis/core/core/reactiveUtils.js",
    ]),
  },
];

const TARGET_SUPPORTED_KINDS: Readonly<Record<CodemodTarget, ReadonlySet<CodemodConstructorKind>>> =
  Object.freeze({
    "honua-compat": new Set(REWRITE_SPECS.map((spec) => spec.kind)),
    "esri-leaflet": new Set(["feature-layer", "map-image-layer", "tile-layer"] as const),
  });

export const SUPPORTED_ARCGIS_MODULES: readonly string[] = REWRITE_SPECS.flatMap((spec) =>
  Array.from(spec.arcGisModules),
);
export const SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH: Readonly<Record<string, CodemodConstructorKind>> =
  Object.freeze(buildModuleToKindLookup(REWRITE_SPECS));

const MODULE_TO_SPEC = buildModuleToSpecLookup(REWRITE_SPECS);

export function isKindSupportedForTarget(kind: CodemodConstructorKind, target: CodemodTarget): boolean {
  return TARGET_SUPPORTED_KINDS[target].has(kind);
}

interface TextEdit {
  start: number;
  end: number;
  text: string;
}

interface ArcGisImportBinding {
  kind: CodemodConstructorKind;
  localName: string;
  importStyle: "identifier" | "namespace-default";
  sourceKind: "import" | "require";
}

interface RequireBinding {
  modulePath: string;
  localName: string;
}

export interface MigrationTodo {
  kind: CodemodConstructorKind;
  file: string;
  line: number;
  column: number;
  reason: string;
}

export interface CodemodKindMetrics {
  total: number;
  autoMigrated: number;
  manual: number;
}

export type CodemodMetricsByKind = Record<CodemodConstructorKind, CodemodKindMetrics>;

export interface CodemodMetrics {
  totalCodemodScopedCallSites: number;
  autoMigratedCallSites: number;
  manualCallSites: number;
  byKind: CodemodMetricsByKind;
}

export interface CodemodFileResult {
  file: string;
  rewrittenImports: number;
  rewrittenConstructors: number;
  rewrittenDynamicImports: number;
  addedCompatImport: boolean;
  removedArcGisImports: number;
  annotatedTodoComments: number;
  manualTodos: MigrationTodo[];
}

export interface EsriCompatCodemodResult {
  rootDir: string;
  target: CodemodTarget;
  filesScanned: number;
  filesChanged: number;
  metrics: CodemodMetrics;
  fileResults: CodemodFileResult[];
  manualTodos: MigrationTodo[];
}

export interface EsriCompatCodemodOptions {
  rootDir: string;
  write?: boolean;
  compatImportPath?: string;
  annotateTodos?: boolean;
  target?: CodemodTarget;
}

export function runEsriCompatCodemod(options: EsriCompatCodemodOptions): EsriCompatCodemodResult {
  const rootDir = path.resolve(options.rootDir);
  const files = collectSourceFiles(rootDir);
  const compatImportPath = options.compatImportPath ?? DEFAULT_COMPAT_IMPORT_PATH;
  const annotateTodos = options.annotateTodos ?? false;
  const target = options.target ?? "honua-compat";

  const metrics: CodemodMetrics = {
    totalCodemodScopedCallSites: 0,
    autoMigratedCallSites: 0,
    manualCallSites: 0,
    byKind: createEmptyByKindMetrics(),
  };
  const fileResults: CodemodFileResult[] = [];
  const manualTodos: MigrationTodo[] = [];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    const fileResult = codemodFile(file, source, compatImportPath, annotateTodos, target);

    for (const kind of fileResult.rewrittenKinds) {
      metrics.byKind[kind].autoMigrated += 1;
      metrics.byKind[kind].total += 1;
      metrics.autoMigratedCallSites += 1;
      metrics.totalCodemodScopedCallSites += 1;
    }
    for (const todo of fileResult.manualTodos) {
      metrics.byKind[todo.kind].manual += 1;
      metrics.byKind[todo.kind].total += 1;
      metrics.manualCallSites += 1;
      metrics.totalCodemodScopedCallSites += 1;
    }
    manualTodos.push(...fileResult.manualTodos);

    const hasChanges =
      fileResult.rewrittenImports > 0 ||
      fileResult.rewrittenConstructors > 0 ||
      fileResult.rewrittenDynamicImports > 0 ||
      fileResult.addedCompatImport ||
      fileResult.removedArcGisImports > 0 ||
      fileResult.annotatedTodoComments > 0;
    if (hasChanges) {
      if (options.write) {
        fs.writeFileSync(file, fileResult.nextSource, "utf8");
      }
      fileResults.push({
        file,
        rewrittenImports: fileResult.rewrittenImports,
        rewrittenConstructors: fileResult.rewrittenConstructors,
        rewrittenDynamicImports: fileResult.rewrittenDynamicImports,
        addedCompatImport: fileResult.addedCompatImport,
        removedArcGisImports: fileResult.removedArcGisImports,
        annotatedTodoComments: fileResult.annotatedTodoComments,
        manualTodos: fileResult.manualTodos,
      });
    } else if (fileResult.manualTodos.length > 0) {
      fileResults.push({
        file,
        rewrittenImports: 0,
        rewrittenConstructors: 0,
        rewrittenDynamicImports: 0,
        addedCompatImport: false,
        removedArcGisImports: 0,
        annotatedTodoComments: 0,
        manualTodos: fileResult.manualTodos,
      });
    }
  }

  return {
    rootDir,
    target,
    filesScanned: files.length,
    filesChanged: fileResults.filter(
      (item) =>
        item.rewrittenConstructors > 0 ||
        item.rewrittenImports > 0 ||
        item.rewrittenDynamicImports > 0 ||
        item.addedCompatImport ||
        item.removedArcGisImports > 0 ||
        item.annotatedTodoComments > 0,
    ).length,
    metrics,
    fileResults: fileResults.sort((a, b) => a.file.localeCompare(b.file)),
    manualTodos: manualTodos.sort(compareTodos),
  };
}

function codemodFile(
  file: string,
  source: string,
  compatImportPath: string,
  annotateTodos: boolean,
  target: CodemodTarget,
): {
  nextSource: string;
  rewrittenImports: number;
  rewrittenConstructors: number;
  rewrittenDynamicImports: number;
  rewrittenKinds: CodemodConstructorKind[];
  addedCompatImport: boolean;
  removedArcGisImports: number;
  annotatedTodoComments: number;
  manualTodos: MigrationTodo[];
} {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const imports = collectSupportedImports(sourceFile);

  const importsByLocalName = new Map<string, ArcGisImportBinding>();
  for (const importBinding of imports) {
    if (!importsByLocalName.has(importBinding.localName)) {
      importsByLocalName.set(importBinding.localName, importBinding);
    }
  }

  const constructorEdits: TextEdit[] = [];
  const dynamicImportEdits: TextEdit[] = [];
  const importEdits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];
  const requiredCompatSymbols = new Set<string>();
  const requiresEsriLeafletImport = { value: false };
  const esriLeafletNamespaceAlias =
    findNamespaceImportAlias(sourceFile, ESRI_LEAFLET_IMPORT_PATH) ?? ESRI_LEAFLET_NAMESPACE;
  const fileExtension = path.extname(file).toLowerCase();
  const isCommonJsModule = fileExtension === ".cjs" || hasCommonJsExportMarkers(source);

  const esriRequestImportRewrite = rewriteEsriRequestImports({
    source,
    sourceFile,
    file,
    compatImportPath,
    annotateTodos,
  });
  importEdits.push(...esriRequestImportRewrite.edits);
  rewrittenKinds.push(...esriRequestImportRewrite.rewrittenKinds);
  manualTodos.push(...esriRequestImportRewrite.manualTodos);
  todoCommentEdits.push(...esriRequestImportRewrite.todoCommentEdits);

  const identityManagerImportRewrite = rewriteIdentityManagerImports({
    source,
    sourceFile,
    file,
    compatImportPath,
    annotateTodos,
  });
  importEdits.push(...identityManagerImportRewrite.edits);
  rewrittenKinds.push(...identityManagerImportRewrite.rewrittenKinds);
  manualTodos.push(...identityManagerImportRewrite.manualTodos);
  todoCommentEdits.push(...identityManagerImportRewrite.todoCommentEdits);

  const esriConfigImportRewrite = rewriteEsriConfigImports({
    source,
    sourceFile,
    file,
    compatImportPath,
    annotateTodos,
  });
  importEdits.push(...esriConfigImportRewrite.edits);
  rewrittenKinds.push(...esriConfigImportRewrite.rewrittenKinds);
  manualTodos.push(...esriConfigImportRewrite.manualTodos);
  todoCommentEdits.push(...esriConfigImportRewrite.todoCommentEdits);

  const reactiveUtilsImportRewrite = rewriteReactiveUtilsImports({
    source,
    sourceFile,
    file,
    compatImportPath,
    annotateTodos,
  });
  importEdits.push(...reactiveUtilsImportRewrite.edits);
  rewrittenKinds.push(...reactiveUtilsImportRewrite.rewrittenKinds);
  manualTodos.push(...reactiveUtilsImportRewrite.manualTodos);
  todoCommentEdits.push(...reactiveUtilsImportRewrite.todoCommentEdits);

  walk(sourceFile, (node) => {
    if (isArcGisDynamicImportCall(node)) {
      const firstArg = node.arguments[0];
      if (!ts.isStringLiteral(firstArg)) {
        return;
      }

      const modulePath = firstArg.text;
      const spec = MODULE_TO_SPEC.get(modulePath);
      if (spec) {
        if (target === "honua-compat") {
          dynamicImportEdits.push({
            start: node.getStart(sourceFile),
            end: node.getEnd(),
            text: buildCompatDynamicImportExpression(compatImportPath, spec.compatSymbol),
          });
          rewrittenKinds.push(spec.kind);
          return;
        }

        const targetExpression = buildEsriLeafletDynamicImportExpression(spec.kind, esriLeafletNamespaceAlias);
        if (targetExpression) {
          dynamicImportEdits.push({
            start: node.getStart(sourceFile),
            end: node.getEnd(),
            text: targetExpression,
          });
          rewrittenKinds.push(spec.kind);
          requiresEsriLeafletImport.value = true;
          return;
        }

        const nodeStart = node.getStart(sourceFile);
        const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
        manualTodos.push({
          kind: spec.kind,
          file,
          line: location.line + 1,
          column: location.character + 1,
          reason: ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON,
        });
        if (annotateTodos) {
          const lineStart = findLineStartOffset(source, nodeStart);
          if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
            todoCommentEdits.push({
              start: lineStart,
              end: lineStart,
              text: `// ${TODO_MARKER}[${spec.kind}]: ${ESRI_LEAFLET_UNSUPPORTED_DYNAMIC_IMPORT_REASON}\n`,
            });
          }
        }
      }
      return;
    }

    if (!ts.isNewExpression(node)) {
      return;
    }

    const rewriteTarget = resolveConstructorRewriteTarget(node.expression, sourceFile, importsByLocalName);
    if (!rewriteTarget) {
      return;
    }

    const importBinding = rewriteTarget.binding;
    if (isCommonJsModule && importBinding.sourceKind === "require") {
      const nodeStart = node.getStart(sourceFile);
      const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: importBinding.kind,
        file,
        line: location.line + 1,
        column: location.character + 1,
        reason: CJS_REQUIRE_MANUAL_REASON,
      });
      if (annotateTodos) {
        const lineStart = findLineStartOffset(source, nodeStart);
        if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[${importBinding.kind}]: ${CJS_REQUIRE_MANUAL_REASON}\n`,
          });
        }
      }
      return;
    }

    const safeCheck = isSafeConstructorCall(importBinding.kind, node);
    if (safeCheck.ok) {
      if (target === "honua-compat") {
        const spec = specForKind(importBinding.kind);
        requiredCompatSymbols.add(spec.compatSymbol);
        constructorEdits.push({
          start: rewriteTarget.start,
          end: rewriteTarget.end,
          text: spec.compatSymbol,
        });
        rewrittenKinds.push(importBinding.kind);
        return;
      }

      const replacement = buildEsriLeafletConstructorExpression(
        importBinding.kind,
        node,
        sourceFile,
        esriLeafletNamespaceAlias,
      );
      if (replacement) {
        constructorEdits.push({
          start: node.getStart(sourceFile),
          end: node.getEnd(),
          text: replacement,
        });
        rewrittenKinds.push(importBinding.kind);
        requiresEsriLeafletImport.value = true;
        return;
      }

      const nodeStart = node.getStart(sourceFile);
      const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: importBinding.kind,
        file,
        line: location.line + 1,
        column: location.character + 1,
        reason: ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON,
      });
      if (annotateTodos) {
        const lineStart = findLineStartOffset(source, nodeStart);
        if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[${importBinding.kind}]: ${ESRI_LEAFLET_UNSUPPORTED_CONSTRUCTOR_REASON}\n`,
          });
        }
      }
      return;
    }

    const nodeStart = node.getStart(sourceFile);
    const location = sourceFile.getLineAndCharacterOfPosition(nodeStart);
    manualTodos.push({
      kind: importBinding.kind,
      file,
      line: location.line + 1,
      column: location.character + 1,
      reason: safeCheck.reason,
    });
    if (annotateTodos) {
      const lineStart = findLineStartOffset(source, nodeStart);
      if (shouldInsertTodoComment(source, lineStart, nodeStart)) {
        todoCommentEdits.push({
          start: lineStart,
          end: lineStart,
          text: `// ${TODO_MARKER}[${importBinding.kind}]: ${safeCheck.reason}\n`,
        });
      }
    }
  });

  if (constructorEdits.length === 0 && dynamicImportEdits.length === 0 && importEdits.length === 0) {
    return {
      nextSource: applyTextEdits(source, todoCommentEdits),
      rewrittenImports: 0,
      rewrittenConstructors: 0,
      rewrittenDynamicImports: 0,
      rewrittenKinds: [],
      addedCompatImport: false,
      removedArcGisImports: 0,
      annotatedTodoComments: todoCommentEdits.length,
      manualTodos: manualTodos.sort(compareTodos),
    };
  }

  let transformed = applyTextEdits(source, [
    ...importEdits,
    ...constructorEdits,
    ...dynamicImportEdits,
    ...todoCommentEdits,
  ]);
  const removedArcGisImports = removeUnusedArcGisImports(file, transformed);
  transformed = removedArcGisImports.nextSource;

  let addedCompatImport = false;
  if (target === "honua-compat") {
    const compatSymbols = Array.from(requiredCompatSymbols).sort();
    const compatImportResult = ensureCompatNamedImports(
      file,
      transformed,
      compatSymbols,
      compatImportPath,
    );
    transformed = compatImportResult.nextSource;
    addedCompatImport = compatImportResult.changed;
  } else if (requiresEsriLeafletImport.value) {
    const esriLeafletImportResult = ensureNamespaceImport(
      file,
      transformed,
      ESRI_LEAFLET_IMPORT_PATH,
      esriLeafletNamespaceAlias,
    );
    transformed = esriLeafletImportResult.nextSource;
    addedCompatImport = esriLeafletImportResult.changed;
  }

  return {
    nextSource: transformed,
    rewrittenImports: importEdits.length,
    rewrittenConstructors: constructorEdits.length,
    rewrittenDynamicImports: dynamicImportEdits.length,
    rewrittenKinds,
    addedCompatImport,
    removedArcGisImports: removedArcGisImports.removedCount,
    annotatedTodoComments: todoCommentEdits.length,
    manualTodos: manualTodos.sort(compareTodos),
  };
}

function rewriteReactiveUtilsImports(options: {
  source: string;
  sourceFile: ts.SourceFile;
  file: string;
  compatImportPath: string;
  annotateTodos: boolean;
}): {
  edits: TextEdit[];
  rewrittenKinds: CodemodConstructorKind[];
  manualTodos: MigrationTodo[];
  todoCommentEdits: TextEdit[];
} {
  const edits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];

  for (const statement of options.sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (MODULE_TO_SPEC.get(statement.moduleSpecifier.text)?.kind !== "reactive-utils") {
      continue;
    }

    const replacement = buildReactiveUtilsCompatImport(
      statement,
      options.sourceFile,
      options.compatImportPath,
    );
    if (!replacement) {
      const nodeStart = statement.getStart(options.sourceFile);
      const location = options.sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: "reactive-utils",
        file: options.file,
        line: location.line + 1,
        column: location.character + 1,
        reason: REACTIVE_UTILS_IMPORT_UNSUPPORTED_REASON,
      });

      if (options.annotateTodos) {
        const lineStart = findLineStartOffset(options.source, nodeStart);
        if (shouldInsertTodoComment(options.source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[reactive-utils]: ${REACTIVE_UTILS_IMPORT_UNSUPPORTED_REASON}\n`,
          });
        }
      }
      continue;
    }

    edits.push({
      start: statement.getStart(options.sourceFile),
      end: statement.getEnd(),
      text: replacement,
    });
    rewrittenKinds.push("reactive-utils");
  }

  return {
    edits,
    rewrittenKinds,
    manualTodos,
    todoCommentEdits,
  };
}

function rewriteEsriRequestImports(options: {
  source: string;
  sourceFile: ts.SourceFile;
  file: string;
  compatImportPath: string;
  annotateTodos: boolean;
}): {
  edits: TextEdit[];
  rewrittenKinds: CodemodConstructorKind[];
  manualTodos: MigrationTodo[];
  todoCommentEdits: TextEdit[];
} {
  const edits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];

  for (const statement of options.sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!statement.importClause) {
      continue;
    }
    if (MODULE_TO_SPEC.get(statement.moduleSpecifier.text)?.kind !== "esri-request") {
      continue;
    }

    const replacement = buildEsriRequestCompatImport(statement, options.sourceFile, options.compatImportPath);
    if (!replacement) {
      const nodeStart = statement.getStart(options.sourceFile);
      const location = options.sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: "esri-request",
        file: options.file,
        line: location.line + 1,
        column: location.character + 1,
        reason: ESRI_REQUEST_IMPORT_UNSUPPORTED_REASON,
      });

      if (options.annotateTodos) {
        const lineStart = findLineStartOffset(options.source, nodeStart);
        if (shouldInsertTodoComment(options.source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[esri-request]: ${ESRI_REQUEST_IMPORT_UNSUPPORTED_REASON}\n`,
          });
        }
      }
      continue;
    }

    edits.push({
      start: statement.getStart(options.sourceFile),
      end: statement.getEnd(),
      text: replacement,
    });
    rewrittenKinds.push("esri-request");
  }

  return {
    edits,
    rewrittenKinds,
    manualTodos,
    todoCommentEdits,
  };
}

function buildEsriRequestCompatImport(
  statement: ts.ImportDeclaration,
  sourceFile: ts.SourceFile,
  compatImportPath: string,
): string | undefined {
  const importClause = statement.importClause;
  if (!importClause) {
    return undefined;
  }

  const specifiers: string[] = [];
  if (importClause.name) {
    specifiers.push(renderImportSpecifier("esriRequest", importClause.name.text));
  }

  const namedBindings = importClause.namedBindings;
  if (namedBindings && ts.isNamespaceImport(namedBindings)) {
    specifiers.push(renderImportSpecifier("esriRequest", namedBindings.name.text));
  } else if (namedBindings && ts.isNamedImports(namedBindings)) {
    for (const element of namedBindings.elements) {
      const importedName = element.propertyName?.text ?? element.name.text;
      const localName = element.name.text;
      if (importedName === "default" || importedName === "esriRequest") {
        specifiers.push(renderImportSpecifier("esriRequest", localName));
        continue;
      }
      return undefined;
    }
  }

  const uniqueSpecifiers = Array.from(new Set(specifiers));
  if (uniqueSpecifiers.length === 0) {
    uniqueSpecifiers.push("esriRequest");
  }

  return `import { ${uniqueSpecifiers.join(", ")} } from "${compatImportPath}";`;
}

function rewriteIdentityManagerImports(options: {
  source: string;
  sourceFile: ts.SourceFile;
  file: string;
  compatImportPath: string;
  annotateTodos: boolean;
}): {
  edits: TextEdit[];
  rewrittenKinds: CodemodConstructorKind[];
  manualTodos: MigrationTodo[];
  todoCommentEdits: TextEdit[];
} {
  const edits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];

  for (const statement of options.sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!statement.importClause) {
      continue;
    }
    if (MODULE_TO_SPEC.get(statement.moduleSpecifier.text)?.kind !== "identity-manager") {
      continue;
    }

    const replacement = buildIdentityManagerCompatImport(
      statement,
      options.sourceFile,
      options.compatImportPath,
    );
    if (!replacement) {
      const nodeStart = statement.getStart(options.sourceFile);
      const location = options.sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: "identity-manager",
        file: options.file,
        line: location.line + 1,
        column: location.character + 1,
        reason: IDENTITY_MANAGER_IMPORT_UNSUPPORTED_REASON,
      });

      if (options.annotateTodos) {
        const lineStart = findLineStartOffset(options.source, nodeStart);
        if (shouldInsertTodoComment(options.source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[identity-manager]: ${IDENTITY_MANAGER_IMPORT_UNSUPPORTED_REASON}\n`,
          });
        }
      }
      continue;
    }

    edits.push({
      start: statement.getStart(options.sourceFile),
      end: statement.getEnd(),
      text: replacement,
    });
    rewrittenKinds.push("identity-manager");
  }

  return {
    edits,
    rewrittenKinds,
    manualTodos,
    todoCommentEdits,
  };
}

function buildIdentityManagerCompatImport(
  statement: ts.ImportDeclaration,
  sourceFile: ts.SourceFile,
  compatImportPath: string,
): string | undefined {
  const importClause = statement.importClause;
  if (!importClause) {
    return undefined;
  }

  const specifiers: string[] = [];
  if (importClause.name) {
    specifiers.push(renderImportSpecifier("identityManager", importClause.name.text));
  }

  const namedBindings = importClause.namedBindings;
  if (namedBindings && ts.isNamespaceImport(namedBindings)) {
    specifiers.push(renderImportSpecifier("identityManager", namedBindings.name.text));
  } else if (namedBindings && ts.isNamedImports(namedBindings)) {
    for (const element of namedBindings.elements) {
      const importedName = element.propertyName?.text ?? element.name.text;
      const localName = element.name.text;
      if (importedName === "default" || importedName === "identityManager") {
        specifiers.push(renderImportSpecifier("identityManager", localName));
        continue;
      }
      return undefined;
    }
  }

  const uniqueSpecifiers = Array.from(new Set(specifiers));
  if (uniqueSpecifiers.length === 0) {
    uniqueSpecifiers.push("identityManager");
  }

  return `import { ${uniqueSpecifiers.join(", ")} } from "${compatImportPath}";`;
}

function rewriteEsriConfigImports(options: {
  source: string;
  sourceFile: ts.SourceFile;
  file: string;
  compatImportPath: string;
  annotateTodos: boolean;
}): {
  edits: TextEdit[];
  rewrittenKinds: CodemodConstructorKind[];
  manualTodos: MigrationTodo[];
  todoCommentEdits: TextEdit[];
} {
  const edits: TextEdit[] = [];
  const rewrittenKinds: CodemodConstructorKind[] = [];
  const manualTodos: MigrationTodo[] = [];
  const todoCommentEdits: TextEdit[] = [];

  for (const statement of options.sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!statement.importClause) {
      continue;
    }
    if (MODULE_TO_SPEC.get(statement.moduleSpecifier.text)?.kind !== "esri-config") {
      continue;
    }

    const replacement = buildEsriConfigCompatImport(statement, options.sourceFile, options.compatImportPath);
    if (!replacement) {
      const nodeStart = statement.getStart(options.sourceFile);
      const location = options.sourceFile.getLineAndCharacterOfPosition(nodeStart);
      manualTodos.push({
        kind: "esri-config",
        file: options.file,
        line: location.line + 1,
        column: location.character + 1,
        reason: ESRI_CONFIG_IMPORT_UNSUPPORTED_REASON,
      });

      if (options.annotateTodos) {
        const lineStart = findLineStartOffset(options.source, nodeStart);
        if (shouldInsertTodoComment(options.source, lineStart, nodeStart)) {
          todoCommentEdits.push({
            start: lineStart,
            end: lineStart,
            text: `// ${TODO_MARKER}[esri-config]: ${ESRI_CONFIG_IMPORT_UNSUPPORTED_REASON}\n`,
          });
        }
      }
      continue;
    }

    edits.push({
      start: statement.getStart(options.sourceFile),
      end: statement.getEnd(),
      text: replacement,
    });
    rewrittenKinds.push("esri-config");
  }

  return {
    edits,
    rewrittenKinds,
    manualTodos,
    todoCommentEdits,
  };
}

function buildEsriConfigCompatImport(
  statement: ts.ImportDeclaration,
  sourceFile: ts.SourceFile,
  compatImportPath: string,
): string | undefined {
  const importClause = statement.importClause;
  if (!importClause) {
    return undefined;
  }

  const specifiers: string[] = [];
  if (importClause.name) {
    specifiers.push(renderImportSpecifier("esriConfig", importClause.name.text));
  }

  const namedBindings = importClause.namedBindings;
  if (namedBindings && ts.isNamespaceImport(namedBindings)) {
    specifiers.push(renderImportSpecifier("esriConfig", namedBindings.name.text));
  } else if (namedBindings && ts.isNamedImports(namedBindings)) {
    for (const element of namedBindings.elements) {
      const importedName = element.propertyName?.text ?? element.name.text;
      const localName = element.name.text;
      if (importedName === "default" || importedName === "esriConfig") {
        specifiers.push(renderImportSpecifier("esriConfig", localName));
        continue;
      }
      if (importedName === "resetEsriConfig" || importedName === "getEsriConfigHonuaInterceptors") {
        specifiers.push(renderImportSpecifier(importedName, localName));
        continue;
      }

      return undefined;
    }
  }

  const uniqueSpecifiers = Array.from(new Set(specifiers));
  if (uniqueSpecifiers.length === 0) {
    uniqueSpecifiers.push("esriConfig");
  }

  return `import { ${uniqueSpecifiers.join(", ")} } from "${compatImportPath}";`;
}

function buildReactiveUtilsCompatImport(
  statement: ts.ImportDeclaration,
  sourceFile: ts.SourceFile,
  compatImportPath: string,
): string | undefined {
  const importClause = statement.importClause;
  if (!importClause) {
    return `import { reactiveUtils } from "${compatImportPath}";`;
  }

  const specifiers: string[] = [];
  if (importClause.name) {
    specifiers.push(renderImportSpecifier("reactiveUtils", importClause.name.text));
  }

  const namedBindings = importClause.namedBindings;
  if (namedBindings && ts.isNamespaceImport(namedBindings)) {
    specifiers.push(renderImportSpecifier("reactiveUtils", namedBindings.name.text));
  } else if (namedBindings && ts.isNamedImports(namedBindings)) {
    for (const element of namedBindings.elements) {
      const importedName = element.propertyName?.text ?? element.name.text;
      const localName = element.name.text;
      if (importedName === "default" || importedName === "reactiveUtils") {
        specifiers.push(renderImportSpecifier("reactiveUtils", localName));
        continue;
      }
      if (importedName === "watch" || importedName === "when" || importedName === "whenOnce") {
        specifiers.push(renderImportSpecifier(importedName, localName));
        continue;
      }

      return undefined;
    }
  }

  const uniqueSpecifiers = Array.from(new Set(specifiers));
  if (uniqueSpecifiers.length === 0) {
    uniqueSpecifiers.push("reactiveUtils");
  }

  return `import { ${uniqueSpecifiers.join(", ")} } from "${compatImportPath}";`;
}

function renderImportSpecifier(importedName: string, localName: string): string {
  return importedName === localName ? importedName : `${importedName} as ${localName}`;
}

function hasCommonJsExportMarkers(source: string): boolean {
  return /\bmodule\.exports\b/.test(source) || /\bexports\.[A-Za-z_$][A-Za-z0-9_$]*\b/.test(source);
}

function buildModuleToSpecLookup(specs: readonly ConstructorRewriteSpec[]): Map<string, ConstructorRewriteSpec> {
  const result = new Map<string, ConstructorRewriteSpec>();
  for (const spec of specs) {
    for (const modulePath of spec.arcGisModules) {
      result.set(modulePath, spec);
    }
  }
  return result;
}

function buildModuleToKindLookup(
  specs: readonly ConstructorRewriteSpec[],
): Record<string, CodemodConstructorKind> {
  const result: Record<string, CodemodConstructorKind> = {};
  for (const spec of specs) {
    for (const modulePath of spec.arcGisModules) {
      result[modulePath] = spec.kind;
    }
  }
  return result;
}

function createEmptyByKindMetrics(): CodemodMetricsByKind {
  return {
    "feature-layer": { total: 0, autoMigrated: 0, manual: 0 },
    graphic: { total: 0, autoMigrated: 0, manual: 0 },
    "point-geometry": { total: 0, autoMigrated: 0, manual: 0 },
    "polyline-geometry": { total: 0, autoMigrated: 0, manual: 0 },
    "polygon-geometry": { total: 0, autoMigrated: 0, manual: 0 },
    "extent-geometry": { total: 0, autoMigrated: 0, manual: 0 },
    "spatial-reference": { total: 0, autoMigrated: 0, manual: 0 },
    color: { total: 0, autoMigrated: 0, manual: 0 },
    "simple-line-symbol": { total: 0, autoMigrated: 0, manual: 0 },
    "simple-marker-symbol": { total: 0, autoMigrated: 0, manual: 0 },
    "simple-fill-symbol": { total: 0, autoMigrated: 0, manual: 0 },
    "class-breaks-renderer": { total: 0, autoMigrated: 0, manual: 0 },
    "simple-renderer": { total: 0, autoMigrated: 0, manual: 0 },
    "unique-value-renderer": { total: 0, autoMigrated: 0, manual: 0 },
    "graphics-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "group-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "map-image-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "tile-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "route-layer": { total: 0, autoMigrated: 0, manual: 0 },
    "route-task": { total: 0, autoMigrated: 0, manual: 0 },
    basemap: { total: 0, autoMigrated: 0, manual: 0 },
    map: { total: 0, autoMigrated: 0, manual: 0 },
    "map-view": { total: 0, autoMigrated: 0, manual: 0 },
    "scene-view": { total: 0, autoMigrated: 0, manual: 0 },
    "web-map": { total: 0, autoMigrated: 0, manual: 0 },
    "layer-list": { total: 0, autoMigrated: 0, manual: 0 },
    "table-list-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "feature-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "feature-templates-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "feature-form-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "feature-table-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "feature-set": { total: 0, autoMigrated: 0, manual: 0 },
    "legend-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "popup-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "popup-template": { total: 0, autoMigrated: 0, manual: 0 },
    "swipe-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "print-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "home-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "basemap-toggle-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "locate-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "scale-bar-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "search-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "basemap-layer-list-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "basemap-gallery-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "expand-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "compass-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "bookmarks-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "fullscreen-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "zoom-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "attribution-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "sketch-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "editor-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "track-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "distance-measurement-2d-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "area-measurement-2d-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "measurement-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "time-slider-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "directions-widget": { total: 0, autoMigrated: 0, manual: 0 },
    "coordinate-conversion-widget": { total: 0, autoMigrated: 0, manual: 0 },
    query: { total: 0, autoMigrated: 0, manual: 0 },
    "oauth-info": { total: 0, autoMigrated: 0, manual: 0 },
    "identity-manager": { total: 0, autoMigrated: 0, manual: 0 },
    "esri-request": { total: 0, autoMigrated: 0, manual: 0 },
    "esri-config": { total: 0, autoMigrated: 0, manual: 0 },
    "reactive-utils": { total: 0, autoMigrated: 0, manual: 0 },
  };
}

function collectSupportedImports(sourceFile: ts.SourceFile): ArcGisImportBinding[] {
  const result: ArcGisImportBinding[] = [];

  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }

    const spec = MODULE_TO_SPEC.get(statement.moduleSpecifier.text);
    if (!spec) {
      continue;
    }

    const importClause = statement.importClause;
    if (!importClause) {
      continue;
    }

    if (importClause.name) {
      result.push({
        kind: spec.kind,
        localName: importClause.name.text,
        importStyle: "identifier",
        sourceKind: "import",
      });
    }

    const namedBindings = importClause.namedBindings;
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      for (const element of namedBindings.elements) {
        const importedName = element.propertyName?.text ?? element.name.text;
        if (importedName === "default") {
          result.push({
            kind: spec.kind,
            localName: element.name.text,
            importStyle: "identifier",
            sourceKind: "import",
          });
        }
      }
    }
    if (namedBindings && ts.isNamespaceImport(namedBindings)) {
      result.push({
        kind: spec.kind,
        localName: namedBindings.name.text,
        importStyle: "namespace-default",
        sourceKind: "import",
      });
    }
  }

  for (const statement of sourceFile.statements) {
    if (!ts.isVariableStatement(statement)) {
      continue;
    }

    for (const declaration of statement.declarationList.declarations) {
      const requireBinding = extractRequireBindingFromDeclaration(declaration);
      if (!requireBinding) {
        continue;
      }

      const spec = MODULE_TO_SPEC.get(requireBinding.modulePath);
      if (!spec) {
        continue;
      }

      result.push({
        kind: spec.kind,
        localName: requireBinding.localName,
        importStyle: "identifier",
        sourceKind: "require",
      });
    }
  }

  return result;
}

function resolveConstructorRewriteTarget(
  expression: ts.Expression,
  sourceFile: ts.SourceFile,
  importsByLocalName: ReadonlyMap<string, ArcGisImportBinding>,
): { binding: ArcGisImportBinding; start: number; end: number } | undefined {
  if (ts.isIdentifier(expression)) {
    const binding = importsByLocalName.get(expression.text);
    if (!binding || binding.importStyle !== "identifier") {
      return undefined;
    }

    return {
      binding,
      start: expression.getStart(sourceFile),
      end: expression.getEnd(),
    };
  }

  if (
    ts.isPropertyAccessExpression(expression) &&
    expression.name.text === "default" &&
    ts.isIdentifier(expression.expression)
  ) {
    const binding = importsByLocalName.get(expression.expression.text);
    if (!binding || binding.importStyle !== "namespace-default") {
      return undefined;
    }

    return {
      binding,
      start: expression.getStart(sourceFile),
      end: expression.getEnd(),
    };
  }

  return undefined;
}

function ensureCompatNamedImports(
  file: string,
  source: string,
  symbols: readonly string[],
  importPath: string,
): { nextSource: string; changed: boolean } {
  if (symbols.length === 0) {
    return { nextSource: source, changed: false };
  }

  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const importClause = statement.importClause;
    const namedBindings = importClause?.namedBindings;
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      const existingSpecifiers = namedBindings.elements.map((element) => element.getText(sourceFile));
      const existingLocalNames = new Set(namedBindings.elements.map((element) => element.name.text));
      const missing = symbols.filter((symbol) => !existingLocalNames.has(symbol));
      if (missing.length === 0) {
        return { nextSource: source, changed: false };
      }

      const mergedSymbols = [...existingSpecifiers, ...missing];
      const replacement = buildNamedImportText(
        importPath,
        importClause?.name?.text,
        mergedSymbols,
      );

      const nextSource = applyTextEdits(source, [
        {
          start: statement.getStart(sourceFile),
          end: statement.getEnd(),
          text: replacement,
        },
      ]);
      return { nextSource, changed: true };
    }

    const importLine = `${buildNamedImportText(importPath, undefined, symbols)}\n`;
    const insertion = statement.getEnd();
    const nextSource = `${source.slice(0, insertion)}\n${importLine}${source.slice(insertion)}`;
    return { nextSource, changed: true };
  }

  const insertionIndex = findImportInsertionIndex(sourceFile);
  const importLine = buildNamedImportText(importPath, undefined, symbols);
  const prefix = source.slice(0, insertionIndex);
  const suffix = source.slice(insertionIndex);
  const needsLeadingNewline = prefix.length > 0 && !prefix.endsWith("\n");
  const leading = needsLeadingNewline ? "\n" : "";
  const needsTrailingNewline =
    suffix.length > 0 && !suffix.startsWith("\n") && !importLine.endsWith("\n");
  const trailing = needsTrailingNewline ? "\n" : "";

  return {
    nextSource: `${prefix}${leading}${importLine}${trailing}${suffix}`,
    changed: true,
  };
}

function findNamespaceImportAlias(sourceFile: ts.SourceFile, importPath: string): string | undefined {
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const namedBindings = statement.importClause?.namedBindings;
    if (namedBindings && ts.isNamespaceImport(namedBindings)) {
      return namedBindings.name.text;
    }
  }

  return undefined;
}

function ensureNamespaceImport(
  file: string,
  source: string,
  importPath: string,
  namespaceAlias: string,
): { nextSource: string; changed: boolean } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (statement.moduleSpecifier.text !== importPath) {
      continue;
    }

    const namedBindings = statement.importClause?.namedBindings;
    if (namedBindings && ts.isNamespaceImport(namedBindings) && namedBindings.name.text === namespaceAlias) {
      return { nextSource: source, changed: false };
    }

    const importLine = `import * as ${namespaceAlias} from "${importPath}";\n`;
    const insertion = statement.getEnd();
    return {
      nextSource: `${source.slice(0, insertion)}\n${importLine}${source.slice(insertion)}`,
      changed: true,
    };
  }

  const insertionIndex = findImportInsertionIndex(sourceFile);
  const importLine = `import * as ${namespaceAlias} from "${importPath}";`;
  const prefix = source.slice(0, insertionIndex);
  const suffix = source.slice(insertionIndex);
  const needsLeadingNewline = prefix.length > 0 && !prefix.endsWith("\n");
  const leading = needsLeadingNewline ? "\n" : "";
  const needsTrailingNewline =
    suffix.length > 0 && !suffix.startsWith("\n") && !importLine.endsWith("\n");
  const trailing = needsTrailingNewline ? "\n" : "";

  return {
    nextSource: `${prefix}${leading}${importLine}${trailing}${suffix}`,
    changed: true,
  };
}

function buildNamedImportText(
  importPath: string,
  defaultImport: string | undefined,
  namedImports: readonly string[],
): string {
  const uniqueNamed = Array.from(new Set(namedImports));
  const namedImportText = `{ ${uniqueNamed.join(", ")} }`;
  if (defaultImport) {
    return `import ${defaultImport}, ${namedImportText} from "${importPath}";`;
  }

  return `import ${namedImportText} from "${importPath}";`;
}

function findImportInsertionIndex(sourceFile: ts.SourceFile): number {
  let index = 0;
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement)) {
      break;
    }
    index = statement.end;
  }
  return index;
}

function isArcGisDynamicImportCall(node: ts.Node): node is ts.CallExpression {
  if (!ts.isCallExpression(node)) {
    return false;
  }
  if (node.expression.kind !== ts.SyntaxKind.ImportKeyword) {
    return false;
  }
  return node.arguments.length === 1;
}

function buildCompatDynamicImportExpression(compatImportPath: string, compatSymbol: string): string {
  return `import("${compatImportPath}").then((m) => ({ default: m.${compatSymbol} }))`;
}

function buildEsriLeafletConstructorExpression(
  kind: CodemodConstructorKind,
  node: ts.NewExpression,
  sourceFile: ts.SourceFile,
  namespaceAlias: string,
): string | undefined {
  const method = esriLeafletMethodForKind(kind);
  if (!method) {
    return undefined;
  }

  const argsText = node.arguments?.map((arg) => arg.getText(sourceFile)).join(", ") ?? "";
  return `${namespaceAlias}.${method}(${argsText})`;
}

function buildEsriLeafletDynamicImportExpression(
  kind: CodemodConstructorKind,
  namespaceAlias: string,
): string | undefined {
  const method = esriLeafletMethodForKind(kind);
  if (!method) {
    return undefined;
  }

  return `Promise.resolve({ default: ${namespaceAlias}.${method} })`;
}

function esriLeafletMethodForKind(kind: CodemodConstructorKind): string | undefined {
  switch (kind) {
    case "feature-layer":
      return "featureLayer";
    case "map-image-layer":
      return "dynamicMapLayer";
    case "tile-layer":
      return "tiledMapLayer";
    default:
      return undefined;
  }
}

function removeUnusedArcGisImports(
  file: string,
  source: string,
): { nextSource: string; removedCount: number } {
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const removals: TextEdit[] = [];

  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    if (!MODULE_TO_SPEC.has(statement.moduleSpecifier.text)) {
      continue;
    }

    const importClause = statement.importClause;
    if (!importClause) {
      continue;
    }

    const localIdentifiers = extractImportClauseLocalIdentifiers(importClause);
    if (localIdentifiers.length === 0) {
      continue;
    }

    const hasReferences = localIdentifiers.some(
      (identifier) => countIdentifierUsagesExcludingImports(sourceFile, identifier) > 0,
    );
    if (hasReferences) {
      continue;
    }

    const bounds = expandToFullLine(source, statement.getStart(sourceFile), statement.getEnd());
    removals.push({
      start: bounds.start,
      end: bounds.end,
      text: "",
    });
  }

  for (const statement of sourceFile.statements) {
    if (!ts.isVariableStatement(statement)) {
      continue;
    }

    if (statement.declarationList.declarations.length !== 1) {
      continue;
    }

    const declaration = statement.declarationList.declarations[0];
    const requireBinding = extractRequireBindingFromDeclaration(declaration);
    if (!requireBinding) {
      continue;
    }

    if (!MODULE_TO_SPEC.has(requireBinding.modulePath)) {
      continue;
    }

    const references = countIdentifierUsagesExcludingImportsAndDefinitions(
      sourceFile,
      requireBinding.localName,
    );
    if (references > 0) {
      continue;
    }

    const bounds = expandToFullLine(source, statement.getStart(sourceFile), statement.getEnd());
    removals.push({
      start: bounds.start,
      end: bounds.end,
      text: "",
    });
  }

  if (removals.length === 0) {
    return { nextSource: source, removedCount: 0 };
  }

  return {
    nextSource: applyTextEdits(source, removals),
    removedCount: removals.length,
  };
}

function extractModulePathFromRequireInitializer(initializer: ts.Expression): string | undefined {
  if (ts.isCallExpression(initializer) && initializer.arguments.length === 1) {
    if (
      ts.isIdentifier(initializer.expression) &&
      initializer.expression.text === "require" &&
      ts.isStringLiteral(initializer.arguments[0])
    ) {
      return initializer.arguments[0].text;
    }
    return undefined;
  }

  if (
    ts.isPropertyAccessExpression(initializer) &&
    initializer.name.text === "default" &&
    ts.isCallExpression(initializer.expression) &&
    initializer.expression.arguments.length === 1 &&
    ts.isIdentifier(initializer.expression.expression) &&
    initializer.expression.expression.text === "require" &&
    ts.isStringLiteral(initializer.expression.arguments[0])
  ) {
    return initializer.expression.arguments[0].text;
  }

  return undefined;
}

function extractRequireBindingFromDeclaration(
  declaration: ts.VariableDeclaration,
): RequireBinding | undefined {
  if (!declaration.initializer) {
    return undefined;
  }

  const modulePath = extractModulePathFromRequireInitializer(declaration.initializer);
  if (!modulePath) {
    return undefined;
  }

  if (ts.isIdentifier(declaration.name)) {
    return {
      modulePath,
      localName: declaration.name.text,
    };
  }

  if (!ts.isObjectBindingPattern(declaration.name)) {
    return undefined;
  }

  for (const element of declaration.name.elements) {
    let propertyNameText: string | undefined;
    if (!element.propertyName) {
      propertyNameText = ts.isIdentifier(element.name) ? element.name.text : undefined;
    } else if (ts.isIdentifier(element.propertyName)) {
      propertyNameText = element.propertyName.text;
    } else {
      propertyNameText = element.propertyName.getText();
    }
    if (propertyNameText !== "default") {
      continue;
    }
    if (!ts.isIdentifier(element.name)) {
      continue;
    }
    return {
      modulePath,
      localName: element.name.text,
    };
  }

  return undefined;
}

function extractImportClauseLocalIdentifiers(importClause: ts.ImportClause): string[] {
  const names: string[] = [];
  if (importClause.name) {
    names.push(importClause.name.text);
  }

  const namedBindings = importClause.namedBindings;
  if (!namedBindings) {
    return names;
  }

  if (ts.isNamespaceImport(namedBindings)) {
    names.push(namedBindings.name.text);
    return names;
  }

  for (const element of namedBindings.elements) {
    names.push(element.name.text);
  }

  return names;
}

function countIdentifierUsagesExcludingImports(sourceFile: ts.SourceFile, name: string): number {
  let count = 0;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== name) {
      return;
    }
    if (isInImportContext(node)) {
      return;
    }
    count += 1;
  });

  return count;
}

function countIdentifierUsagesExcludingImportsAndDefinitions(
  sourceFile: ts.SourceFile,
  name: string,
): number {
  let count = 0;

  walk(sourceFile, (node) => {
    if (!ts.isIdentifier(node) || node.text !== name) {
      return;
    }
    if (isInImportContext(node)) {
      return;
    }
    if (isVariableDeclarationName(node)) {
      return;
    }
    count += 1;
  });

  return count;
}

function isInImportContext(node: ts.Identifier): boolean {
  let current: ts.Node | undefined = node;
  while (current) {
    if (
      ts.isImportClause(current) ||
      ts.isImportDeclaration(current) ||
      ts.isImportSpecifier(current) ||
      ts.isNamespaceImport(current) ||
      ts.isNamedImports(current)
    ) {
      return true;
    }
    current = current.parent;
  }
  return false;
}

function isVariableDeclarationName(node: ts.Identifier): boolean {
  return (
    (ts.isVariableDeclaration(node.parent) && node.parent.name === node) ||
    (ts.isBindingElement(node.parent) && node.parent.name === node)
  );
}

function expandToFullLine(source: string, start: number, end: number): { start: number; end: number } {
  let expandedStart = start;
  while (expandedStart > 0 && source[expandedStart - 1] !== "\n") {
    expandedStart -= 1;
  }

  let expandedEnd = end;
  while (expandedEnd < source.length && source[expandedEnd] !== "\n") {
    expandedEnd += 1;
  }
  if (expandedEnd < source.length && source[expandedEnd] === "\n") {
    expandedEnd += 1;
  }

  return { start: expandedStart, end: expandedEnd };
}

function findLineStartOffset(source: string, position: number): number {
  let start = position;
  while (start > 0 && source[start - 1] !== "\n") {
    start -= 1;
  }
  return start;
}

function shouldInsertTodoComment(source: string, lineStart: number, nodeStart: number): boolean {
  const currentPrefix = source.slice(lineStart, nodeStart);
  if (currentPrefix.includes(TODO_MARKER)) {
    return false;
  }

  if (lineStart === 0) {
    return true;
  }

  const previousLineEnd = lineStart - 1;
  const previousLineStart = findLineStartOffset(source, previousLineEnd);
  const previousLine = source.slice(previousLineStart, lineStart);
  return !previousLine.includes(TODO_MARKER);
}

function applyTextEdits(source: string, edits: readonly TextEdit[]): string {
  const sorted = edits
    .slice()
    .sort((a, b) => (a.start === b.start ? b.end - a.end : b.start - a.start));

  let nextSource = source;
  for (const edit of sorted) {
    nextSource = `${nextSource.slice(0, edit.start)}${edit.text}${nextSource.slice(edit.end)}`;
  }
  return nextSource;
}

function isSafeConstructorCall(
  kind: CodemodConstructorKind,
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  switch (kind) {
    case "feature-layer":
      return isSafeFeatureLayerCompatCall(node);
    case "graphic":
      return isSafeGraphicCompatCall(node);
    case "point-geometry":
      return isSafePointGeometryCompatCall(node);
    case "polyline-geometry":
      return isSafePolylineGeometryCompatCall(node);
    case "polygon-geometry":
      return isSafePolygonGeometryCompatCall(node);
    case "extent-geometry":
      return isSafeExtentGeometryCompatCall(node);
    case "spatial-reference":
      return isSafeSpatialReferenceCompatCall(node);
    case "color":
      return isSafeColorCompatCall(node);
    case "simple-line-symbol":
      return isSafeSimpleLineSymbolCompatCall(node);
    case "simple-marker-symbol":
      return isSafeSimpleMarkerSymbolCompatCall(node);
    case "simple-fill-symbol":
      return isSafeSimpleFillSymbolCompatCall(node);
    case "class-breaks-renderer":
      return isSafeClassBreaksRendererCompatCall(node);
    case "simple-renderer":
      return isSafeSimpleRendererCompatCall(node);
    case "unique-value-renderer":
      return isSafeUniqueValueRendererCompatCall(node);
    case "graphics-layer":
      return isSafeGraphicsLayerCompatCall(node);
    case "group-layer":
      return isSafeGroupLayerCompatCall(node);
    case "map-image-layer":
      return isSafeMapImageLayerCompatCall(node);
    case "tile-layer":
      return isSafeTileLayerCompatCall(node);
    case "route-layer":
      return isSafeRouteLayerCompatCall(node);
    case "route-task":
      return isSafeRouteTaskCompatCall(node);
    case "basemap":
      return isSafeBasemapCompatCall(node);
    case "map":
      return isSafeMapCompatCall(node);
    case "map-view":
      return isSafeMapViewCompatCall(node);
    case "scene-view":
      return isSafeSceneViewCompatCall(node);
    case "web-map":
      return isSafeWebMapCompatCall(node);
    case "layer-list":
      return isSafeLayerListCompatCall(node);
    case "table-list-widget":
      return isSafeTableListWidgetCompatCall(node);
    case "feature-widget":
      return isSafeFeatureWidgetCompatCall(node);
    case "feature-templates-widget":
      return isSafeFeatureTemplatesWidgetCompatCall(node);
    case "feature-form-widget":
      return isSafeFeatureFormWidgetCompatCall(node);
    case "feature-table-widget":
      return isSafeFeatureTableWidgetCompatCall(node);
    case "feature-set":
      return isSafeFeatureSetCompatCall(node);
    case "legend-widget":
      return isSafeLegendWidgetCompatCall(node);
    case "popup-widget":
      return isSafePopupWidgetCompatCall(node);
    case "popup-template":
      return isSafePopupTemplateCompatCall(node);
    case "swipe-widget":
      return isSafeSwipeWidgetCompatCall(node);
    case "print-widget":
      return isSafePrintWidgetCompatCall(node);
    case "home-widget":
      return isSafeHomeWidgetCompatCall(node);
    case "basemap-toggle-widget":
      return isSafeBasemapToggleWidgetCompatCall(node);
    case "locate-widget":
      return isSafeLocateWidgetCompatCall(node);
    case "scale-bar-widget":
      return isSafeScaleBarWidgetCompatCall(node);
    case "search-widget":
      return isSafeSearchWidgetCompatCall(node);
    case "basemap-layer-list-widget":
      return isSafeBasemapLayerListWidgetCompatCall(node);
    case "basemap-gallery-widget":
      return isSafeBasemapGalleryWidgetCompatCall(node);
    case "expand-widget":
      return isSafeExpandWidgetCompatCall(node);
    case "compass-widget":
      return isSafeCompassWidgetCompatCall(node);
    case "bookmarks-widget":
      return isSafeBookmarksWidgetCompatCall(node);
    case "fullscreen-widget":
      return isSafeFullscreenWidgetCompatCall(node);
    case "zoom-widget":
      return isSafeZoomWidgetCompatCall(node);
    case "attribution-widget":
      return isSafeAttributionWidgetCompatCall(node);
    case "sketch-widget":
      return isSafeSketchWidgetCompatCall(node);
    case "editor-widget":
      return isSafeEditorWidgetCompatCall(node);
    case "track-widget":
      return isSafeTrackWidgetCompatCall(node);
    case "distance-measurement-2d-widget":
      return isSafeDistanceMeasurement2dWidgetCompatCall(node);
    case "area-measurement-2d-widget":
      return isSafeAreaMeasurement2dWidgetCompatCall(node);
    case "measurement-widget":
      return isSafeMeasurementWidgetCompatCall(node);
    case "time-slider-widget":
      return isSafeTimeSliderWidgetCompatCall(node);
    case "directions-widget":
      return isSafeDirectionsWidgetCompatCall(node);
    case "coordinate-conversion-widget":
      return isSafeCoordinateConversionWidgetCompatCall(node);
    case "query":
      return isSafeQueryCompatCall(node);
    case "oauth-info":
      return isSafeOAuthInfoCompatCall(node);
    case "identity-manager":
      return {
        ok: false,
        reason: "IdentityManager is not a constructor and requires import-based migration.",
      };
    case "esri-request":
      return {
        ok: false,
        reason: "esriRequest is not a constructor and requires import-based migration.",
      };
    case "esri-config":
      return {
        ok: false,
        reason: "esriConfig is not a constructor and requires import-based migration.",
      };
    case "reactive-utils":
      return {
        ok: false,
        reason: "ReactiveUtils is not a constructor and requires import-based migration.",
      };
    default:
      return { ok: false, reason: "Unsupported ArcGIS constructor usage." };
  }
}

function isSafeRouteLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "RouteLayer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "RouteLayer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "id",
    "title",
    "url",
    "visible",
    "opacity",
    "listMode",
    "stops",
    "autoSolve",
    "routeProvider",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "RouteLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "RouteLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeRouteTaskCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "RouteTask constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (ts.isStringLiteral(arg) || ts.isNoSubstitutionTemplateLiteral(arg)) {
    return { ok: true };
  }
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "RouteTask constructor argument is not an object literal or string literal.",
    };
  }

  const allowed = new Set(["url", "apiKey", "requestOptions"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "RouteTask options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "RouteTask options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Basemap constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Basemap constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["id", "title", "baseLayers", "referenceLayers"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Basemap options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Basemap options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "outFields", "definitionExpression"]);

  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "FeatureLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeGraphicCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Graphic constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Graphic constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["geometry", "symbol", "attributes", "popupTemplate", "layer"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Graphic options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Graphic options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePointGeometryCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Point constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Point constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["x", "y", "z", "m", "spatialReference"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Point options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Point options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePolylineGeometryCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Polyline constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Polyline constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["paths", "spatialReference", "hasZ", "hasM"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Polyline options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Polyline options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePolygonGeometryCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Polygon constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Polygon constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["rings", "spatialReference", "hasZ", "hasM"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Polygon options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Polygon options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeExtentGeometryCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Extent constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Extent constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "xmin",
    "ymin",
    "xmax",
    "ymax",
    "zmin",
    "zmax",
    "mmin",
    "mmax",
    "spatialReference",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Extent options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Extent options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSpatialReferenceCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SpatialReference constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SpatialReference constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["wkid", "latestWkid", "wkt", "vcsWkid", "latestVcsWkid"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "SpatialReference options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SpatialReference options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeColorCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Color constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (
    ts.isStringLiteralLike(arg) ||
    ts.isArrayLiteralExpression(arg) ||
    ts.isObjectLiteralExpression(arg)
  ) {
    return { ok: true };
  }

  return {
    ok: false,
    reason: "Color constructor argument is not a string/array/object literal.",
  };
}

function isSafeSimpleLineSymbolCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SimpleLineSymbol constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SimpleLineSymbol constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["style", "color", "width"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "SimpleLineSymbol options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SimpleLineSymbol options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSimpleMarkerSymbolCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SimpleMarkerSymbol constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SimpleMarkerSymbol constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["style", "color", "size", "outline"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "SimpleMarkerSymbol options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SimpleMarkerSymbol options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSimpleFillSymbolCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SimpleFillSymbol constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SimpleFillSymbol constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["style", "color", "outline"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "SimpleFillSymbol options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SimpleFillSymbol options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeClassBreaksRendererCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "ClassBreaksRenderer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "ClassBreaksRenderer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "field",
    "normalizationField",
    "normalizationTotal",
    "minValue",
    "defaultSymbol",
    "defaultLabel",
    "legendOptions",
    "valueExpression",
    "valueExpressionTitle",
    "classBreakInfos",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "ClassBreaksRenderer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "ClassBreaksRenderer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSimpleRendererCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SimpleRenderer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SimpleRenderer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["symbol", "label", "description", "visualVariables"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "SimpleRenderer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SimpleRenderer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeUniqueValueRendererCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "UniqueValueRenderer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "UniqueValueRenderer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "field",
    "field2",
    "field3",
    "defaultSymbol",
    "defaultLabel",
    "uniqueValueInfos",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "UniqueValueRenderer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "UniqueValueRenderer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMapImageLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "MapImageLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "MapImageLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "sublayers", "opacity", "visible"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "MapImageLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "MapImageLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "MapImageLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeTileLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length !== 1) {
    return {
      ok: false,
      reason: "TileLayer constructor is not a single object-literal argument.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "TileLayer constructor argument is not an object literal.",
    };
  }

  let hasUrlOption = false;
  const allowed = new Set(["url", "opacity", "visible"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "TileLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (name === "url") {
      hasUrlOption = true;
    }

    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "TileLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  if (!hasUrlOption) {
    return {
      ok: false,
      reason: "TileLayer options missing required url property; requires manual migration.",
    };
  }

  return { ok: true };
}

function isSafeGraphicsLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "GraphicsLayer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "GraphicsLayer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["graphics", "id", "title", "visible", "opacity", "listMode"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "GraphicsLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "GraphicsLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeGroupLayerCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "GroupLayer constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "GroupLayer constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "layers",
    "id",
    "title",
    "visible",
    "opacity",
    "listMode",
    "visibilityMode",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "GroupLayer options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "GroupLayer options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMapCompatCall(node: ts.NewExpression): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Map constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Map constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["basemap", "layers"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Map options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Map options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMapViewCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "MapView constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "MapView constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["map", "container", "center", "zoom", "popup"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "MapView options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "MapView options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeWebMapCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "WebMap constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "WebMap constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["portalItem", "basemap", "layers"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "WebMap options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "WebMap options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSceneViewCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "SceneView constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "SceneView constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["map", "container", "center", "zoom", "camera", "qualityProfile", "viewingMode"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "SceneView options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "SceneView options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLayerListCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "LayerList constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "LayerList constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "listItemCreatedFunction"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "LayerList options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "LayerList options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeTableListWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "TableList constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "TableList constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "map",
    "container",
    "tables",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "TableList options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "TableList options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Feature constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Feature constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "map",
    "container",
    "graphic",
    "title",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Feature options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Feature options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureTemplatesWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureTemplates constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureTemplates constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "layerInfos",
    "container",
    "filterFunction",
    "groupBy",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureTemplates options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureTemplates options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureFormWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureForm constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureForm constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "layer",
    "container",
    "feature",
    "fieldConfig",
    "groupDisplay",
    "headingLevel",
    "visibleElements",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureForm options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureForm options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureTableWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureTable constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureTable constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "layer",
    "container",
    "title",
    "description",
    "actionColumnConfig",
    "attachmentsEnabled",
    "paginationEnabled",
    "objectIdField",
    "where",
    "filterGeometry",
    "filterBySelectionEnabled",
    "relatedRecordsEnabled",
    "tableTemplate",
    "visibleElements",
    "fieldConfigs",
    "editingEnabled",
    "multiSortEnabled",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureTable options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureTable options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFeatureSetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "FeatureSet constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "FeatureSet constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "features",
    "fields",
    "geometryType",
    "spatialReference",
    "objectIdFieldName",
    "displayFieldName",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "FeatureSet options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "FeatureSet options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLegendWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Legend constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Legend constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "layers", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Legend options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Legend options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePopupWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Popup constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Popup constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "autoOpenEnabled", "dockEnabled", "dockOptions"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Popup options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Popup options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePopupTemplateCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "PopupTemplate constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "PopupTemplate constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["title", "content", "fieldInfos", "actions", "expressionInfos", "outFields"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason:
          "PopupTemplate options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "PopupTemplate options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSwipeWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Swipe constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Swipe constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "leadingLayers",
    "trailingLayers",
    "position",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Swipe options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Swipe options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafePrintWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Print constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Print constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "printServiceUrl",
    "templateOptions",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Print options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Print options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeHomeWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Home constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Home constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Home options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Home options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapToggleWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "BasemapToggle constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "BasemapToggle constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "nextBasemap"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "BasemapToggle options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "BasemapToggle options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeLocateWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Locate constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Locate constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Locate options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Locate options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeScaleBarWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "ScaleBar constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "ScaleBar constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "unit"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "ScaleBar options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "ScaleBar options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSearchWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Search constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Search constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "sources", "includeDefaultSources", "autoNavigate"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Search options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Search options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapLayerListWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "BasemapLayerList constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "BasemapLayerList constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "BasemapLayerList options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "BasemapLayerList options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBasemapGalleryWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "BasemapGallery constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "BasemapGallery constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "source"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "BasemapGallery options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "BasemapGallery options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeExpandWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Expand constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Expand constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "content", "expanded", "mode", "group"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Expand options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Expand options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeCompassWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Compass constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Compass constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Compass options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Compass options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeBookmarksWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Bookmarks constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Bookmarks constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "bookmarks"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Bookmarks options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Bookmarks options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeFullscreenWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Fullscreen constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Fullscreen constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "element"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Fullscreen options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Fullscreen options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeZoomWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Zoom constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Zoom constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "layout"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Zoom options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Zoom options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeAttributionWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Attribution constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Attribution constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "map", "container", "itemDelimiter", "attributions"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Attribution options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Attribution options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeSketchWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Sketch constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Sketch constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "layer",
    "container",
    "creationMode",
    "updateOnGraphicClick",
    "defaultCreateOptions",
    "defaultUpdateOptions",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Sketch options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Sketch options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeEditorWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Editor constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Editor constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "layerInfos",
    "allowedWorkflows",
    "supportingWidgetDefaults",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Editor options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Editor options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeTrackWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Track constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Track constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "tracking",
    "goToLocationEnabled",
    "useHeadingEnabled",
    "rotationEnabled",
    "scale",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Track options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Track options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeDistanceMeasurement2dWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "DistanceMeasurement2D constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "DistanceMeasurement2D constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "unit"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "DistanceMeasurement2D options contain spread/method/computed property syntax; requires manual migration.",
      };
    }
    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "DistanceMeasurement2D options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeAreaMeasurement2dWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "AreaMeasurement2D constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "AreaMeasurement2D constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "unit"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "AreaMeasurement2D options contain spread/method/computed property syntax; requires manual migration.",
      };
    }
    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "AreaMeasurement2D options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeMeasurementWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Measurement constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Measurement constructor argument is not an object literal.",
    };
  }

  const allowed = new Set(["view", "container", "activeTool", "linearUnit", "areaUnit"]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Measurement options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Measurement options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeTimeSliderWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "TimeSlider constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "TimeSlider constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "fullTimeExtent",
    "timeExtent",
    "stops",
    "mode",
    "loop",
    "playRate",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "TimeSlider options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "TimeSlider options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeDirectionsWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Directions constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Directions constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "layer",
    "routeProvider",
    "stops",
    "useDefaultRouteLayer",
    "showSaveAsButton",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Directions options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Directions options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeCoordinateConversionWidgetCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "CoordinateConversion constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "CoordinateConversion constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "view",
    "container",
    "formats",
    "mode",
    "multipleConversionsEnabled",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "CoordinateConversion options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "CoordinateConversion options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeQueryCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "Query constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "Query constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "where",
    "outFields",
    "returnGeometry",
    "orderByFields",
    "objectIds",
    "geometry",
    "spatialRelationship",
    "outSpatialReference",
    "num",
    "start",
    "timeExtent",
    "groupByFieldsForStatistics",
    "outStatistics",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "Query options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "Query options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function isSafeOAuthInfoCompatCall(
  node: ts.NewExpression,
): { ok: true } | { ok: false; reason: string } {
  const args = node.arguments;
  if (!args || args.length === 0) {
    return { ok: true };
  }
  if (args.length !== 1) {
    return {
      ok: false,
      reason: "OAuthInfo constructor has more than one argument; requires manual migration.",
    };
  }

  const [arg] = args;
  if (!ts.isObjectLiteralExpression(arg)) {
    return {
      ok: false,
      reason: "OAuthInfo constructor argument is not an object literal.",
    };
  }

  const allowed = new Set([
    "appId",
    "portalUrl",
    "popup",
    "flowType",
    "expiration",
    "authNamespace",
    "preserveUrlHash",
  ]);
  for (const property of arg.properties) {
    if (!isAssignableObjectProperty(property)) {
      return {
        ok: false,
        reason: "OAuthInfo options contain spread/method/computed property syntax; requires manual migration.",
      };
    }

    const name = getObjectPropertyName(property);
    if (!name || !allowed.has(name)) {
      return {
        ok: false,
        reason: "OAuthInfo options include unsupported properties; requires manual migration.",
      };
    }
  }

  return { ok: true };
}

function getPropertyNameText(name: ts.PropertyName): string | undefined {
  if (ts.isIdentifier(name)) {
    return name.text;
  }
  if (ts.isStringLiteral(name) || ts.isNoSubstitutionTemplateLiteral(name)) {
    return name.text;
  }
  return undefined;
}

function isAssignableObjectProperty(property: ts.ObjectLiteralElementLike): boolean {
  return ts.isPropertyAssignment(property) || ts.isShorthandPropertyAssignment(property);
}

function getObjectPropertyName(property: ts.ObjectLiteralElementLike): string | undefined {
  if (ts.isPropertyAssignment(property)) {
    return getPropertyNameText(property.name);
  }
  if (ts.isShorthandPropertyAssignment(property)) {
    return property.name.text;
  }
  return undefined;
}

function collectSourceFiles(rootDir: string): string[] {
  const queue = [rootDir];
  const result: string[] = [];

  while (queue.length > 0) {
    const current = queue.pop()!;
    const entries = fs.readdirSync(current, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!SKIP_DIRS.has(entry.name)) {
          queue.push(fullPath);
        }
        continue;
      }

      if (SOURCE_EXTENSIONS.has(path.extname(entry.name))) {
        result.push(fullPath);
      }
    }
  }

  return result;
}

function walk(node: ts.Node, visit: (node: ts.Node) => void): void {
  visit(node);
  node.forEachChild((child) => walk(child, visit));
}

function specForKind(kind: CodemodConstructorKind): ConstructorRewriteSpec {
  for (const spec of REWRITE_SPECS) {
    if (spec.kind === kind) {
      return spec;
    }
  }
  throw new Error(`Unknown constructor rewrite kind: ${kind}`);
}

function compareTodos(a: MigrationTodo, b: MigrationTodo): number {
  const fileCmp = a.file.localeCompare(b.file);
  if (fileCmp !== 0) {
    return fileCmp;
  }
  if (a.line !== b.line) {
    return a.line - b.line;
  }
  if (a.column !== b.column) {
    return a.column - b.column;
  }
  if (a.kind !== b.kind) {
    return a.kind.localeCompare(b.kind);
  }
  return a.reason.localeCompare(b.reason);
}
