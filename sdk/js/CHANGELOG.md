# Changelog

All notable changes to the Honua JS SDK will be documented in this file.

## [0.0.1-alpha.0] - Unreleased

### Added

- Core HTTP client (`HonuaClient`) with FeatureServer, MapServer, and OGC API Features support
- Fluent layer wrappers (`featureLayer()`, `mapLayer()`, `mapService()`, `ogcFeatures()`)
- Typed response models for all query, edit, metadata, and OGC endpoints
- Schema-aware typed collections via `HonuaFeatureLayer<T>` generic parameter
- Expression engine with 65 operators including spatial (`distance`, `within`, `intersects`)
- PBF binary transport with transparent JSON fallback (`preferBinary` option)
- gRPC-Web transport via Connect protocol (`transport: "grpc-web"`)
- Request interceptor pipeline (`before`/`after`/`error` hooks)
- Retry with exponential backoff for transient failures
- `HonuaMap` for source/layer separation with MapLibre GL JS
- Feature-state interaction helpers (`createHoverHandler`, `createSelectionHandler`)
- Esri compatibility layer for migration (`FeatureLayerCompat`, `MapImageLayerCompat`, etc.)
- Migration tooling: scanner, codemod, reconciliation, parity matrices
- Split-package publishing (`@honua/sdk`, `@honua/sdk-esri-compat`, `@honua/migrate`)
- Biome linter and formatter configuration
