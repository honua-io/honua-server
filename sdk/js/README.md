# Honua JS SDK (Scaffold)

Initial JavaScript SDK scaffold for the JS-first migration phase (`#324`).

This package currently provides:

- core HTTP client (`HonuaClient`) for FeatureServer, MapServer export, and catalog operations,
- Esri-style compatibility wrappers (`FeatureLayerCompat`, `MapImageLayerCompat`, `TileLayerCompat`, `MapCompat`, `MapViewCompat`, `SceneViewCompat`, `WebMapCompat`) for migration-critical patterns,
  including basic `when()` lifecycle support, `FeatureLayer.refresh()/createQuery()/queryObjectIds()/queryFeatureCount()/queryExtent()/queryRelatedFeatures()`, `MapImageLayer.when()/refresh()/exportImage()/getLegend()/identify()`, `Map` layer collection helpers, `GraphicsLayerCompat`/`GroupLayerCompat`, and `MapView` watch/event handles with popup/layer-view bridges plus `toMap`/`toScreen`/`hitTest`,
- identify controller (`IdentifyCompat`) for cross-layer MapServer identify workflows with optional popup auto-open,
- compat widgets/components (`LayerListCompat`, `LegendCompat`, `PopupCompat`) backed by a shared `CompatEventBus` so widgets/components can subscribe to layer/view changes,
- common map controls (`HomeCompat`, `BasemapToggleCompat`, `LocateCompat`, `ScaleBarCompat`) wired to the same event bus for shared view state updates,
- request/auth migration bridge helpers (`createEsriRequestInterceptors`, `createArcGisTokenInterceptor`) plus core `HonuaClient` interceptor hooks (`before`/`after`/`error`),
- URL parsing helpers for ArcGIS FeatureLayer endpoint detection,
- ArcGIS usage scanner (`scanArcGisUsage`) for migration inventory and risk flags,
- safe codemod runner (`runEsriCompatCodemod`) for `FeatureLayer`, `MapImageLayer`, `Map`, `MapView`, `SceneView`, and `WebMap` safe constructors,
- migration report builder with explicit manual TODOs and rewrite metric,
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

# Gate in CI (non-zero exit if migration constraints fail)
node dist/src/migration/cli.js codemod ./src --fail-on-manual --fail-on-unhandled --fail-on-blocked --max-manual-ratio 0.2 --max-manual-intervention-ratio 0.3

# Compare source vs target service fidelity for one layer
node dist/src/migration/cli.js reconcile --source-base-url https://source.example --source-service-id parcels --target-base-url https://target.example --target-service-id parcels --layer-id 0 --sample-size 200 --report reconcile-report.json
```

The codemod is intentionally conservative:
- default target (`--target honua-compat`) rewrites safe constructors:
  - `new FeatureLayer({ url: ... })` -> `new FeatureLayerCompat({ url: ... })` (supports `outFields` and `definitionExpression`)
  - `new GraphicsLayer(...)` -> `new GraphicsLayerCompat(...)`
  - `new GroupLayer(...)` -> `new GroupLayerCompat(...)`
  - `new MapImageLayer({ url: ... })` -> `new MapImageLayerCompat({ url: ... })` (supports `sublayers`, `opacity`, and `visible`)
  - `new TileLayer({ url: ... })` -> `new TileLayerCompat({ url: ... })` (supports `opacity` and `visible`)
  - `new Map(...)` -> `new MapCompat(...)`
  - `new MapView(...)` -> `new MapViewCompat(...)`
  - `new SceneView(...)` -> `new SceneViewCompat(...)`
  - `new WebMap(...)` -> `new WebMapCompat(...)`
  - `new LayerList(...)` -> `new LayerListCompat(...)`
  - `new Legend(...)` -> `new LegendCompat(...)`
  - `new Popup(...)` -> `new PopupCompat(...)`
  - `new Home(...)` -> `new HomeCompat(...)`
  - `new BasemapToggle(...)` -> `new BasemapToggleCompat(...)`
  - `new Locate(...)` -> `new LocateCompat(...)`
  - `new ScaleBar(...)` -> `new ScaleBarCompat(...)`
- alternate target (`--target esri-leaflet`) rewrites deterministic subset only:
  - `new FeatureLayer({ ... })` -> `HonuaEsriLeaflet.featureLayer({ ... })`
  - `new MapImageLayer({ ... })` -> `HonuaEsriLeaflet.dynamicMapLayer({ ... })`
  - `new TileLayer({ ... })` -> `HonuaEsriLeaflet.tiledMapLayer({ ... })`
  - dynamic imports for those modules -> `Promise.resolve({ default: HonuaEsriLeaflet.* })`
  - non-deterministic APIs (for example `Map`, `MapView`, `SceneView`, `WebMap`, `GraphicsLayer`, `GroupLayer`, `LayerList`, `Legend`, `Popup`, `Home`, `BasemapToggle`, `Locate`, `ScaleBar`) are emitted as manual TODO/report entries
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
  - per-type migration counts as `byKind=feature-layer:auto/manual/total,...,layer-list:auto/manual/total,legend-widget:auto/manual/total,popup-widget:auto/manual/total,home-widget:auto/manual/total,basemap-toggle-widget:auto/manual/total,locate-widget:auto/manual/total,scale-bar-widget:auto/manual/total`,
  - grouped manual reasons,
  - unhandled ArcGIS module inventory (with `static-import` / `dynamic-import` / `require` usage style),
  - scanner flags include module-shape and risk hints (for example `commonjs-detected`, `scene-3d-detected`, `dynamic-import-detected`),
  - readiness classification (`ready`, `assisted`, `blocked`) with explicit gate results.
