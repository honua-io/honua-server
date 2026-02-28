# Honua JS SDK (Scaffold)

Initial JavaScript SDK scaffold for the JS-first migration phase (`#324`).

This package currently provides:

- core HTTP client (`HonuaClient`) for FeatureServer, MapServer export/query/related-record operations, and catalog operations, plus fluent layer wrappers (`client.featureLayer(...)`, `client.mapLayer(...)`, `service.featureLayer(...)`, `service.featureLayers()`, `service.mapLayer(...)`, `service.mapLayers()`, `service.mapService().layer(...)`),
- first-class OGC API Features client/wrappers (`client.ogcFeatures()`) for landing page, conformance, collections, queryables, items, paged `itemsAll` helpers, and item CRUD,
- Esri-style compatibility wrappers (`FeatureLayerCompat`, `MapImageLayerCompat`, `TileLayerCompat`, `RouteLayerCompat`, `MapCompat`, `MapViewCompat`, `SceneViewCompat`, `WebMapCompat`) for migration-critical patterns,
  including basic `when()` lifecycle support, `FeatureLayer.refresh()/createQuery()/queryFeaturesAll()/queryObjectIds()/queryFeatureCount()/queryExtent()/queryRelatedFeatures()/addAttachment()/updateAttachment()/listFields()/getField()`, `MapImageLayer.when()/refresh()/createQuery()/exportImage()/getLegend()/find()/identify()/queryFeatures()/queryFeaturesAll()/queryFeatureCount()/queryObjectIds()/queryExtent()/queryRelatedFeatures()/findSublayerById()/sublayer(...).query*()` where writable `layer.sublayers` and sublayer lookups return query-capable `MapImageSublayerCompat` wrappers (and auto-hydrate from metadata when not explicitly configured) with nested `sublayer.sublayers/allSublayers`, `sublayer.visible`, and `sublayer.definitionExpression` bridging to query defaults, `FeatureTableCompat` row/query helpers with runtime `layer` switching (`table.layer = nextLayer` / `table.setLayer(nextLayer)`), `Map` layer collection helpers, `GraphicsLayerCompat`/`GroupLayerCompat`, and `MapView` watch/event handles with popup/layer-view bridges plus `toMap`/`toScreen`/`hitTest`/`takeScreenshot()` and `view.ui.add/remove/move/getComponents` compatibility,
- identify controller (`IdentifyCompat`) for cross-layer MapServer identify workflows with optional popup auto-open,
- compat widgets/components (`LayerListCompat`, `LegendCompat`, `PopupCompat`, `SearchCompat`, `BasemapGalleryCompat`, `BookmarksCompat`, `ExpandCompat`, `SketchCompat`, `EditorCompat`, `TrackCompat`, `MeasurementCompat`, `TimeSliderCompat`, `DirectionsCompat`) backed by a shared `CompatEventBus` so widgets/components can subscribe to layer/view changes, including popup feature-selection navigation and search source/result state helpers (default search sources support both ArcGIS `queryFeatures` layers and OGC collection-style `items()` layers),
- common map controls (`HomeCompat`, `BasemapToggleCompat`, `LocateCompat`, `ScaleBarCompat`, `CompassCompat`, `FullscreenCompat`, `ZoomCompat`, `AttributionCompat`) wired to the same event bus for shared view state updates,
- request/auth migration bridge helpers (`createEsriRequestInterceptors`, `createArcGisTokenInterceptor`) plus core `HonuaClient` interceptor hooks (`before`/`after`/`error`),
- URL parsing helpers for ArcGIS FeatureLayer endpoint detection,
- ArcGIS usage scanner (`scanArcGisUsage`) for migration inventory and risk flags,
- safe codemod runner (`runEsriCompatCodemod`) for migration-safe constructors across layers, views, widgets, and controls,
- migration report builder with explicit manual TODOs and rewrite metric,
- JS parity matrix artifacts (`getJsParityMatrix` / `summarizeJsParityMatrix` and `getJsRuntimeParityMatrix` / `summarizeJsRuntimeParity`) for constructor-level and runtime-capability `native/compat/assisted/unsupported` tracking across Honua compat and esri-leaflet targets,
- service reconciliation helper (`runLayerReconciliation`) for feature-count, geometry-validity, and attribute-key checks,
- unit tests for request mapping and URL parsing.

## Entrypoints

Prefer subpath entrypoints to keep Honua-first and migration layers separate:

- Honua-first core: `@honua/sdk-js/honua`
- Esri compat bridge: `@honua/sdk-js/esri-compat`
- Migration tooling: `@honua/sdk-js/migration`

The root entrypoint (`@honua/sdk-js`) remains available as an aggregate export for compatibility.

## Install

```bash
cd sdk/js
npm install
```

## Verify

```bash
npm run typecheck
npm test
npm run test:playwright
npm run scan:arcgis -- ../../path/to/arcgis-app
npm run migrate:arcgis -- ../../path/to/arcgis-app --write --report migration-report.json
npm run report:migration:real-samples
npm run gate:migration:real-samples
npm run gate:migration:demo-target
npm run matrix:runtime
npm run build:split-packages
```

## Split Package Artifacts

Generate publish-ready split packages under `dist/packages/`:

- `@honua/sdk` -> `dist/packages/honua-sdk`
- `@honua/sdk-esri-compat` -> `dist/packages/honua-sdk-esri-compat`
- `@honua/honua-migrate` -> `dist/packages/honua-migrate`

```bash
npm run build:split-packages
npm run verify:split-packages
```

Create local tarballs for all split packages:

```bash
npm run pack:split-packages
```

CI publish workflow:
- manual dry-run or publish via `Publish JS SDK Packages` workflow
- tag-triggered publish uses tags in form `js-sdk-v<version>` and enforces tag/version match

## Request/Auth Bridge

```ts
import {
  HonuaClient,
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
} from "@honua/sdk-js";

const client = new HonuaClient({
  baseUrl: "https://example.test",
  interceptors: [
    ...createEsriRequestInterceptors([
      {
        urls: "/rest/services/default",
        before: (params) => {
          params.requestOptions.headers = {
            ...(params.requestOptions.headers ?? {}),
            "X-Migrated-By": "honua",
          };
        },
      },
    ]),
    createArcGisTokenInterceptor({
      applyTo: "/rest/services/default",
      mode: "query",
      getToken: async () => "arcgis-token-value",
    }),
  ],
});
```

## OGC API Features (Honua-first)

```ts
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({ baseUrl: "https://example.test" });
const ogc = client.ogcFeatures();

const collections = await ogc.collections();
const parcels = ogc.collection("0");
const items = await parcels.items({ limit: 100, filter: "status = 'active'" });
const allItems = await parcels.itemsAll({ pageSize: 500, maxPages: 20 });
const feature = await parcels.item({ featureId: "123" });
```

## Mixed Esri + OGC in one app

```ts
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({ baseUrl: "https://example.test" });

const parcelsLayer = client.service("transport").featureLayer(0);
const parcelsOgc = client.ogcFeatures().collection("parcels");

const [features, items] = await Promise.all([
  parcelsLayer.queryFeatures({ where: "status = 'active'", outFields: ["OBJECTID"] }),
  parcelsOgc.items({ limit: 50 }),
]);
```

## MapServer query helpers

```ts
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({
  baseUrl: "https://example.test",
  timeoutMs: 15000,
  retry: { maxRetries: 2, retryStatuses: [429, 503] },
});

const mapLayer = client.mapLayer("basemap", 4);
const allMapLayerFeatures = await mapLayer.queryFeaturesAll({ pageSize: 2000, maxPages: 25 });

const mapService = client.mapService("basemap");
const allServiceLayerFeatures = await mapService.queryLayerFeaturesAll({
  layerId: 4,
  pageSize: 2000,
  maxPages: 25,
});

const related = await mapService.queryLayerRelatedRecords({
  layerId: 4,
  relationshipId: 1,
  objectIds: [1001, 1002],
});

const mapLayerRelated = await mapLayer.queryRelatedFeatures({
  relationshipId: 1,
  objectIds: [1001],
});
```

## Migration CLI

```bash
# Scan only
node dist/src/migration/cli.js scan ./src --report scan-report.json

# Safe codemod (dry run)
node dist/src/migration/cli.js codemod ./src --report migration-report.json

# Safe codemod (write changes)
node dist/src/migration/cli.js codemod ./src --write --report migration-report.json

# Safe codemod (write changes targeting esri-leaflet for supported subset)
node dist/src/migration/cli.js codemod ./src --target esri-leaflet --write --report migration-report.json

# Safe codemod (write + inline TODO annotations for manual sites)
node dist/src/migration/cli.js codemod ./src --write --annotate-todos --report migration-report.json

# Emit parity matrix JSON (for docs/CI dashboards)
node dist/src/migration/cli.js matrix --report parity-matrix.json

# Emit runtime parity matrix JSON (for JS API capability tracking)
node dist/src/migration/cli.js runtime-matrix --report runtime-parity-matrix.json

# Generate readiness metrics for bundled complex real-sample fixtures
node dist/src/migration/cli.js fixtures --report reports/real-sample-metrics.json

# Enforce strict readiness gates for bundled real-sample fixtures
node dist/src/migration/cli.js fixtures --fail-on-manual --fail-on-unhandled --fail-on-blocked --max-manual-ratio 0 --max-manual-intervention-ratio 0 --report reports/real-sample-metrics.json

# Enforce strict readiness gates for the demo target fixture only
node dist/src/migration/cli.js fixtures --fixtures esri-real-sample-incident-command-app --fail-on-manual --fail-on-unhandled --fail-on-blocked --max-manual-ratio 0 --max-manual-intervention-ratio 0 --report reports/demo-target-metrics.json

# Limit fixture metrics to a subset and esri-leaflet target mode
node dist/src/migration/cli.js fixtures --target esri-leaflet --fixtures esri-real-sample-network-app --report reports/network-metrics.json

# Gate in CI (non-zero exit if migration constraints fail)
node dist/src/migration/cli.js codemod ./src --fail-on-manual --fail-on-unhandled --fail-on-blocked --max-manual-ratio 0.2 --max-manual-intervention-ratio 0.3

# Compare source vs target service fidelity for one layer
node dist/src/migration/cli.js reconcile --source-base-url https://source.example --source-service-id parcels --target-base-url https://target.example --target-service-id parcels --layer-id 0 --sample-size 200 --report reconcile-report.json
```

The codemod is intentionally conservative:
- default target (`--target honua-compat`) rewrites safe constructors:
  - `new FeatureLayer({ url: ... })` -> `new FeatureLayerCompat({ url: ... })` (supports `id`, `title`, `outFields`, `definitionExpression`, `renderer`, `popupTemplate`, `labelingInfo`, `labelsVisible`, `opacity`, `visible`, `minScale`, `maxScale`, `legendEnabled`, and `listMode`; `url` may be absolute or relative, including path-prefixed deployments)
  - `new Polyline(...)` -> `new PolylineCompat(...)`
  - `new Polygon(...)` -> `new PolygonCompat(...)`
  - `new Extent(...)` -> `new ExtentCompat(...)`
  - `new SpatialReference(...)` -> `new SpatialReferenceCompat(...)`
  - `new Color(...)` -> `new ColorCompat(...)`
  - `new PictureMarkerSymbol(...)` -> `new PictureMarkerSymbolCompat(...)`
  - `new TextSymbol(...)` -> `new TextSymbolCompat(...)`
  - `new LabelClass(...)` -> `new LabelClassCompat(...)`
  - `new SimpleFillSymbol(...)` -> `new SimpleFillSymbolCompat(...)`
  - `new ClassBreaksRenderer(...)` -> `new ClassBreaksRendererCompat(...)`
  - `new SimpleRenderer(...)` -> `new SimpleRendererCompat(...)`
  - `new UniqueValueRenderer(...)` -> `new UniqueValueRendererCompat(...)`
  - `new GraphicsLayer(...)` -> `new GraphicsLayerCompat(...)`
  - `new GroupLayer(...)` -> `new GroupLayerCompat(...)`
  - `new MapImageLayer({ url: ... })` -> `new MapImageLayerCompat({ url: ... })` (supports `id`, `title`, `sublayers`, `opacity`, `visible`, `minScale`, `maxScale`, `listMode`, and `legendEnabled`; runtime helpers include `exportImage/getLegend/find/identify/queryFeatures/queryFeaturesAll/queryFeatureCount/queryObjectIds/queryExtent/queryRelatedFeatures` plus `sublayer(...).query*()`, `sublayer(...).queryFeaturesAll()`, `sublayer(...).queryRelatedFeatures()`, and `sublayer.visible/definitionExpression`; `url` may be absolute or relative, including path-prefixed deployments)
  - `new TileLayer({ url: ... })` -> `new TileLayerCompat({ url: ... })` (supports `id`, `title`, `opacity`, `visible`, `minScale`, `maxScale`, and `listMode`; `url` may be absolute or relative, including path-prefixed deployments)
  - `new RouteLayer(...)` -> `new RouteLayerCompat(...)`
  - `new Map(...)` -> `new MapCompat(...)` (supports `basemap`, `layers`, `ground`, `tables`, `portalItem`, and `spatialReference`)
  - `new MapView(...)` -> `new MapViewCompat(...)` (supports `map`, `container`, `center`, `zoom`, `scale`, `rotation`, `extent`, `constraints`, `padding`, `highlightOptions`, `spatialReference`, and `popup`)
  - `new SceneView(...)` -> `new SceneViewCompat(...)` (supports core `MapView` options plus `viewingMode`, `qualityProfile`, and `camera`)
  - `new WebMap(...)` -> `new WebMapCompat(...)` (supports `portalItem`, `basemap`, `layers`, `ground`, `tables`, and `spatialReference`)
  - `new FeatureTable(...)` -> `new FeatureTableCompat(...)` (supports common table options including `highlightIds` and `multiSortEnabled`)
  - `new FeatureForm(...)` -> `new FeatureFormCompat(...)` (supports `feature`, `fieldConfig`, `groupDisplay`, `headingLevel`, and `visibleElements`)
  - `new FeatureTemplates(...)` -> `new FeatureTemplatesCompat(...)` (supports `layerInfos`, `container`, `filterFunction`, and `groupBy`)
  - `new LayerList(...)` -> `new LayerListCompat(...)` (supports `view`, `map`, `container`, `includeHidden`, `autoRefresh`, and `listItemCreatedFunction`)
  - `new Legend(...)` -> `new LegendCompat(...)` (supports `view`, `map`, `layers`, `container`, `includeHidden`, and `autoRefresh`)
  - `new Popup(...)` -> `new PopupCompat(...)`
  - `new Home(...)` -> `new HomeCompat(...)` (supports `view`, `container`, and `viewpoint`)
  - `new BasemapToggle(...)` -> `new BasemapToggleCompat(...)`
  - `new Locate(...)` -> `new LocateCompat(...)` (supports `view`, `container`, `zoom`, and `locateProvider`)
  - `new ScaleBar(...)` -> `new ScaleBarCompat(...)`
  - `new Search(...)` -> `new SearchCompat(...)` (supports `sources`, `includeDefaultSources`, `autoNavigate`, and `autoRefreshSources`)
  - `new BasemapGallery(...)` -> `new BasemapGalleryCompat(...)`
  - `new Bookmarks(...)` -> `new BookmarksCompat(...)`
  - `new Expand(...)` -> `new ExpandCompat(...)`
  - `new Compass(...)` -> `new CompassCompat(...)`
  - `new Fullscreen(...)` -> `new FullscreenCompat(...)`
  - `new Zoom(...)` -> `new ZoomCompat(...)`
  - `new Attribution(...)` -> `new AttributionCompat(...)`
  - `new Sketch(...)` -> `new SketchCompat(...)`
  - `new Editor(...)` -> `new EditorCompat(...)`
  - `new Track(...)` -> `new TrackCompat(...)` (supports `tracking`, `goToLocationEnabled`, `useHeadingEnabled`, `rotationEnabled`, `scale`, and `trackProvider`)
  - `new Measurement(...)` -> `new MeasurementCompat(...)`
  - `new TimeSlider(...)` -> `new TimeSliderCompat(...)`
  - `new Directions(...)` -> `new DirectionsCompat(...)`
- alternate target (`--target esri-leaflet`) rewrites deterministic subset plus broad compat fallbacks:
  - `new FeatureLayer({ ... })` -> `HonuaEsriLeaflet.featureLayer({ ... })`
  - `new MapImageLayer({ ... })` -> `HonuaEsriLeaflet.dynamicMapLayer({ ... })`
  - `new TileLayer({ ... })` -> `HonuaEsriLeaflet.tiledMapLayer({ ... })`
  - `new Map({ ... })` -> `new MapCompat({ ... })`
  - `new MapView({ ... })` -> `new MapViewCompat({ ... })`
  - `new SceneView({ ... })` -> `new SceneViewCompat({ ... })`
  - `new LayerList({ ... })` -> `new LayerListCompat({ ... })`
  - `new Legend({ ... })` -> `new LegendCompat({ ... })`
  - `new Popup({ ... })` -> `new PopupCompat({ ... })`
  - `new Search({ ... })` -> `new SearchCompat({ ... })`
  - `new Home/BasemapToggle/Locate/ScaleBar/BasemapGallery/Expand/Compass/Bookmarks/Fullscreen/Zoom/Attribution(...)` -> corresponding `*Compat` constructor
  - dynamic imports for those modules -> `Promise.resolve({ default: HonuaEsriLeaflet.* })`
  - dynamic imports for compat fallback modules -> `import("@honua/sdk-esri-compat").then((m) => ({ default: m.*Compat }))`
  - advanced 3D-only modules (for example `Slice` and external-renderer style APIs) are emitted as manual TODO/report entries
- `--target esri-leaflet` is for ArcGIS JS (`@arcgis/core`) inputs; existing Esri Leaflet apps generally do not need codemod migration
- it rewrites supported dynamic imports to compat bridge expressions when safe (for example SceneView dynamic import),
- it skips complex constructors and records manual TODO entries in the report,
- it keeps CommonJS `require(...)` constructor sites as manual TODOs (for example `.cjs` or `.js` files exporting via `module.exports`/`exports.*`),
- optionally it can inject inline `// TODO(honua-migrate)...` comments for manual sites (`--annotate-todos`),
- it computes `manualRewrite = numerator / denominator` for codemod-scoped call sites,
- it computes `manualIntervention = numerator / denominator` across codemod-scoped call sites plus unhandled ArcGIS usage hits,
- migration report JSON includes `codemodTarget` so CI artifacts clearly indicate target mode (`honua-compat` or `esri-leaflet`),
- it supports CI gating flags:
  - `--fail-on-manual`
  - `--fail-on-unhandled`
  - `--fail-on-blocked`
  - `--max-manual-ratio <0..1>`
  - `--max-manual-intervention-ratio <0..1>`
- CLI summary includes:
  - per-type migration counts as `byKind=feature-layer:auto/manual/total,graphic:auto/manual/total,point-geometry:auto/manual/total,polyline-geometry:auto/manual/total,polygon-geometry:auto/manual/total,extent-geometry:auto/manual/total,spatial-reference:auto/manual/total,color:auto/manual/total,simple-line-symbol:auto/manual/total,simple-marker-symbol:auto/manual/total,picture-marker-symbol:auto/manual/total,text-symbol:auto/manual/total,label-class:auto/manual/total,simple-fill-symbol:auto/manual/total,class-breaks-renderer:auto/manual/total,simple-renderer:auto/manual/total,unique-value-renderer:auto/manual/total,...,route-layer:auto/manual/total,layer-list:auto/manual/total,legend-widget:auto/manual/total,popup-widget:auto/manual/total,home-widget:auto/manual/total,basemap-toggle-widget:auto/manual/total,locate-widget:auto/manual/total,scale-bar-widget:auto/manual/total,search-widget:auto/manual/total,basemap-gallery-widget:auto/manual/total,bookmarks-widget:auto/manual/total,expand-widget:auto/manual/total,compass-widget:auto/manual/total,fullscreen-widget:auto/manual/total,zoom-widget:auto/manual/total,attribution-widget:auto/manual/total,sketch-widget:auto/manual/total,editor-widget:auto/manual/total,track-widget:auto/manual/total,measurement-widget:auto/manual/total,time-slider-widget:auto/manual/total,directions-widget:auto/manual/total,query:auto/manual/total,feature-set:auto/manual/total,oauth-info:auto/manual/total,identity-manager:auto/manual/total,esri-request:auto/manual/total,esri-config:auto/manual/total,reactive-utils:auto/manual/total`,
  - grouped manual reasons,
  - unhandled ArcGIS module inventory (with `static-import` / `dynamic-import` / `require` usage style),
  - scanner flags include module-shape and risk hints (for example `commonjs-detected`, `scene-3d-detected`, `dynamic-import-detected`),
  - readiness classification (`ready`, `assisted`, `blocked`) with explicit gate results.
