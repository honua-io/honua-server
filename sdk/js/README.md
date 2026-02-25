# Honua JS SDK (Scaffold)

Initial JavaScript SDK scaffold for the JS-first migration phase (`#324`).

This package currently provides:

- core HTTP client (`HonuaClient`) for FeatureServer and catalog operations,
- Esri-style compatibility wrappers (`FeatureLayerCompat`, `MapCompat`, `MapViewCompat`, `SceneViewCompat`, `WebMapCompat`) for migration-critical patterns,
  including basic `when()` lifecycle support, `FeatureLayer.createQuery()/queryObjectIds()/queryFeatureCount()`, `Map` layer collection helpers, and `MapView` watch/event handles with popup/layer-view bridges,
- URL parsing helpers for ArcGIS FeatureLayer endpoint detection,
- ArcGIS usage scanner (`scanArcGisUsage`) for migration inventory and risk flags,
- safe codemod runner (`runEsriCompatCodemod`) for `FeatureLayer`, `Map`, `MapView`, `SceneView`, and `WebMap` safe constructors,
- migration report builder with explicit manual TODOs and rewrite metric,
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
```

Create local tarballs for all split packages:

```bash
npm run pack:split-packages
```

CI publish workflow:
- manual dry-run or publish via `Publish JS SDK Packages` workflow
- tag-triggered publish uses tags in form `js-sdk-v<version>` and enforces tag/version match

## Migration CLI

```bash
# Scan only
node dist/src/migration/cli.js scan ./src --report scan-report.json

# Safe codemod (dry run)
node dist/src/migration/cli.js codemod ./src --report migration-report.json

# Safe codemod (write changes)
node dist/src/migration/cli.js codemod ./src --write --report migration-report.json

# Safe codemod (write + inline TODO annotations for manual sites)
node dist/src/migration/cli.js codemod ./src --write --annotate-todos --report migration-report.json

# Gate in CI (non-zero exit if migration constraints fail)
node dist/src/migration/cli.js codemod ./src --fail-on-manual --fail-on-unhandled --fail-on-blocked --max-manual-ratio 0.2 --max-manual-intervention-ratio 0.3
```

The codemod is intentionally conservative:
- it rewrites safe constructors:
  - `new FeatureLayer({ url: ... })` -> `new FeatureLayerCompat({ url: ... })` (supports `outFields` and `definitionExpression`)
  - `new Map(...)` -> `new MapCompat(...)`
  - `new MapView(...)` -> `new MapViewCompat(...)`
  - `new SceneView(...)` -> `new SceneViewCompat(...)`
  - `new WebMap(...)` -> `new WebMapCompat(...)`
- it rewrites supported dynamic imports to compat bridge expressions when safe (for example SceneView dynamic import),
- it skips complex constructors and records manual TODO entries in the report,
- optionally it can inject inline `// TODO(honua-migrate)...` comments for manual sites (`--annotate-todos`),
- it computes `manualRewrite = numerator / denominator` for codemod-scoped call sites,
- it computes `manualIntervention = numerator / denominator` across codemod-scoped call sites plus unhandled ArcGIS usage hits,
- it supports CI gating flags:
  - `--fail-on-manual`
  - `--fail-on-unhandled`
  - `--fail-on-blocked`
  - `--max-manual-ratio <0..1>`
  - `--max-manual-intervention-ratio <0..1>`
- CLI summary includes:
  - per-type migration counts as `byKind=feature-layer:auto/manual/total,...`,
  - grouped manual reasons,
  - unhandled ArcGIS module inventory (with `static-import` / `dynamic-import` / `require` usage style),
  - readiness classification (`ready`, `assisted`, `blocked`) with explicit gate results.
