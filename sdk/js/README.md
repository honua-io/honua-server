# Honua JS SDK (Scaffold)

Initial JavaScript SDK scaffold for the JS-first migration phase (`#324`).

This package currently provides:

- core HTTP client (`HonuaClient`) for FeatureServer and catalog operations,
- Esri-style compatibility wrappers (`FeatureLayerCompat`, `MapCompat`, `MapViewCompat`) for migration-critical patterns,
- URL parsing helpers for ArcGIS FeatureLayer endpoint detection,
- ArcGIS usage scanner (`scanArcGisUsage`) for migration inventory and risk flags,
- safe codemod runner (`runEsriCompatCodemod`) for `FeatureLayer`, `Map`, and `MapView` safe constructors,
- migration report builder with explicit manual TODOs and rewrite metric,
- unit tests for request mapping and URL parsing.

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
```

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
```

The codemod is intentionally conservative:
- it rewrites safe constructors:
  - `new FeatureLayer({ url: ... })` -> `new FeatureLayerCompat({ url: ... })`
  - `new Map(...)` -> `new MapCompat(...)`
  - `new MapView(...)` -> `new MapViewCompat(...)`
- it skips complex constructors and records manual TODO entries in the report,
- optionally it can inject inline `// TODO(honua-migrate)...` comments for manual sites (`--annotate-todos`),
- it computes `manualRewrite = numerator / denominator` for codemod-scoped call sites,
- CLI summary includes per-type migration counts as `byKind=feature-layer:auto/manual/total,...`.
