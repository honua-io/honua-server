# Honua JS SDK (Scaffold)

Initial JavaScript SDK scaffold for the JS-first migration phase (`#324`).

This package currently provides:

- core HTTP client (`HonuaClient`) for FeatureServer and catalog operations,
- Esri-style compatibility wrapper (`FeatureLayerCompat`) for layer URL-based migration,
- URL parsing helpers for ArcGIS FeatureLayer endpoint detection,
- ArcGIS usage scanner (`scanArcGisUsage`) for migration inventory and risk flags,
- safe codemod runner (`runEsriCompatCodemod`) for simple `FeatureLayer({ url })` migrations,
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
```

The codemod is intentionally conservative:
- it rewrites `new FeatureLayer({ url: ... })` to `new FeatureLayerCompat({ url: ... })`,
- it skips complex constructors and records manual TODO entries in the report,
- it computes `manualRewrite = numerator / denominator` for codemod-scoped call sites.
