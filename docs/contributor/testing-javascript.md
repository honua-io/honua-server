# JavaScript Integration Tests

The `tests/js/` directory contains two test suites exercising Honua from JavaScript/TypeScript clients: the **Esri Feature Server** suite and the **OpenLayers compatibility** suite. Both run via Vitest; the OpenLayers suite also uses Playwright for browser-based rendering tests.

## Prerequisites

```bash
cd tests/js
npm install
npx playwright install --with-deps chromium   # Required for rendering tests
```

### Automatic Test Server (Default)

If `HONUA_BASE_URL` points to a healthy server (`/healthz/live` returns 200), the tests use it. Otherwise Vitest will bootstrap a local stack by running `tests/python/shared/js_test_server.py`, which:

- Starts a PostGIS container (Docker required)
- Seeds the catalog and test features
- Launches the Honua server and exports runtime IDs

The bootstrap uses `.venv-tests/bin/python` when available, otherwise `python3`. You can control the port and startup timeout via `HONUA_TEST_PORT` and `HONUA_TEST_TIMEOUT` if needed.

### Environment Variables

```bash
HONUA_BASE_URL=http://localhost:5555    # Honua server URL
HONUA_SERVICE_ID=test_service_gw0       # Test service ID
HONUA_LAYER_ID=1000                     # Test layer ID (also 0 for OpenLayers)
HONUA_API_KEY=your-api-key              # API key (CI sets this; optional locally)
HONUA_TEST_TIMEOUT=30000                # Request timeout (ms)
```

### Run Commands

```bash
npm test                        # All Vitest tests (Feature Server + OpenLayers)
npm run test:openlayers         # OpenLayers Vitest tests only
npm run test:openlayers:render  # Playwright rendering tests only
npm run test:query              # Feature Server query tests
npm run test:matrix             # Spatial/geometry matrix tests
npm run test:geometry           # Geometry roundtrip tests
npm run test:edits              # ApplyEdits tests
npm run test:metadata           # Metadata endpoint tests
npm run test:watch              # Watch mode
npm run test:coverage           # Coverage report
```

---

## Esri Feature Server Suite

Comprehensive integration tests for Honua's Esri Feature Service compatibility.

### Test Matrix Coverage

#### Spatial Relationships (17 variations)

| Category | Relationships |
|----------|---------------|
| Non-Distance | esriSpatialRelIntersects, esriSpatialRelContains, esriSpatialRelWithin, esriSpatialRelEnvelopeIntersects, esriSpatialRelCrosses, esriSpatialRelTouches, esriSpatialRelOverlaps, esriSpatialRelDisjoint, esriSpatialRelEquals |
| Distance-Based | esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance |
| Distance Units | esriSRUnit_Meter, esriSRUnit_Foot, esriSRUnit_Kilometer, esriSRUnit_StatuteMile |

#### Geometry Types (11 variations)

| Type | Method | Esri Type |
|------|--------|-----------|
| Point | `point()` | esriGeometryPoint |
| MultiPoint | `multipoint()` | esriGeometryMultipoint |
| LineString | `linestring()` | esriGeometryPolyline |
| MultiLineString | `multilinestring()` | esriGeometryPolyline |
| Polygon (simple) | `polygonSimple()` | esriGeometryPolygon |
| Polygon (with hole) | `polygonWithHole()` | esriGeometryPolygon |
| Polygon (multi-hole) | `polygonWithMultipleHoles()` | esriGeometryPolygon |
| MultiPolygon (simple) | `multipolygonSimple()` | esriGeometryPolygon |
| MultiPolygon (with holes) | `multipolygonWithHoles()` | esriGeometryPolygon |
| GeometryCollection | `geometryCollection()` | N/A |
| Null | `nullGeometry()` | null |

#### WHERE Clause Operators (16 cases)

- Comparison: `=`, `<>`, `<`, `>`, `<=`, `>=`
- Pattern: `LIKE`, `IN`, `BETWEEN`
- Null handling: `IS NULL`, `IS NOT NULL`
- Logical: `AND`, `OR`, `NOT`
- Grouping: Parentheses, nested expressions

#### Output Formats

- `json` (Esri JSON)
- `geojson` (GeoJSON)

#### Spatial References

- EPSG:4326 (WGS84)
- EPSG:3857 (Web Mercator)
- Input/Output transformation

### Feature Server Test Counts

| File | Test Cases |
|------|------------|
| query.test.ts | ~80 tests |
| query-matrix.test.ts | ~100+ tests (matrix expansion) |
| geometry-types.test.ts | ~60 tests |
| apply-edits.test.ts | ~50 tests |
| metadata.test.ts | ~40 tests |
| **Total** | **~330+ tests** |

### Comparison with Python Tests

This JavaScript test suite mirrors the Python test suite structure:

| Python File | JavaScript File |
|-------------|-----------------|
| `test_query.py` | `query.test.ts` |
| `test_query_matrix.py` | `query-matrix.test.ts` |
| `test_geometry_types.py` | `geometry-types.test.ts` |
| `test_apply_edits.py` | `apply-edits.test.ts` |
| `test_metadata.py` | `metadata.test.ts` |

Both suites use the same test coordinates (San Francisco area), geometry generation patterns, matrix parameterization approach, and assertion helpers.

---

## OpenLayers Compatibility Suite

Proves that OpenLayers (`ol` v10.5+) can consume Honua's OGC and tile APIs end-to-end. Tests use real `ol/format/*` parsers against live server responses.

### Protocols Covered

| Protocol | Test Runner | Test Files |
|----------|------------|------------|
| OGC API Features | Vitest | `oapif/oapif-discovery.test.ts`, `oapif/oapif-features.test.ts` |
| WFS 2.0 | Vitest | `wfs/wfs-discovery.test.ts`, `wfs/wfs-features.test.ts` |
| OGC API Tiles / MVT | Vitest | `tiles/tiles-metadata.test.ts`, `tiles/tiles-mvt.test.ts` |
| MVT Rendering | Playwright | `rendering/render.spec.ts` |

### What Each Suite Exercises

- **OGC API Features**: landing page, conformance classes, collection discovery, items list with pagination, single-item fetch, `ol/format/GeoJSON` feature parsing.
- **WFS 2.0**: GetCapabilities XML parsing, FeatureType discovery, ServiceIdentification, GetFeature consumption via `ol/format/WFS` and `ol/format/GML`, geometry extraction.
- **OGC Tiles / MVT**: tiles landing page, tile matrix sets, collection tilesets, tileset metadata, MVT tile fetch and decode via `ol/format/MVT`, feature property access.
- **MVT Rendering** (Playwright): headless Chromium loads an Express-served page with an OpenLayers `VectorTile` layer pointed at Honua's MVT endpoint; test verifies canvas pixel output after `rendercomplete`.

### Certification Evidence

The OpenLayers suite produces `.cert.json` evidence files conforming to the [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) specification. The `EvidenceCollector` class in `openlayers/shared/evidence.ts` implements merge-on-write so multiple Vitest forks accumulate into a single file per protocol.

Evidence files are written to `tests/js/certification-evidence/` and uploaded as CI artifacts. See the [Certification Matrix JS lane](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md#js-lane) for the full list of extension IDs (`JS-EXT-OL-*`, `JS-EXT-TILES-*`).

### Node.js DOM Polyfill

WFS/GML tests require a DOM parser in Node.js. The setup file `openlayers/shared/ol-node-setup.ts` provides `DOMParser` via JSDOM so `ol/format/WFS` and `ol/format/GML` work outside the browser.

---

## Directory Structure

```
tests/js/
├── package.json               # Dependencies and scripts
├── vitest.config.ts           # Vitest configuration
├── vitest.global.ts           # Auto-bootstrap (starts PostGIS + server if needed)
├── tsconfig.json              # TypeScript config
├── shared/                    # Shared helpers for Feature Server tests
│   ├── client.ts              # HTTP client for FeatureServer
│   ├── constants.ts           # Test constants and enums
│   ├── geometry.ts            # Geometry generator
│   └── index.ts               # Barrel export
├── feature-server/            # Esri Feature Server tests
│   ├── query.test.ts          # Query endpoint tests
│   ├── query-matrix.test.ts   # Spatial/geometry matrix
│   ├── geometry-types.test.ts # Geometry roundtrip tests
│   ├── apply-edits.test.ts    # Edit operations
│   └── metadata.test.ts       # Service/layer metadata
└── openlayers/                # OpenLayers compatibility tests
    ├── shared/
    │   ├── config.ts          # Env-driven base URL, service/layer IDs
    │   ├── evidence.ts        # EvidenceCollector (cert.json output)
    │   ├── evidence.test.ts   # Unit tests for EvidenceCollector
    │   └── ol-node-setup.ts   # JSDOM polyfill for WFS/GML parsing
    ├── oapif/                 # OGC API Features
    │   ├── oapif-discovery.test.ts
    │   └── oapif-features.test.ts
    ├── wfs/                   # WFS 2.0
    │   ├── wfs-discovery.test.ts
    │   └── wfs-features.test.ts
    ├── tiles/                 # OGC Tiles / MVT
    │   ├── tiles-metadata.test.ts
    │   └── tiles-mvt.test.ts
    └── rendering/             # Playwright browser rendering
        ├── global-setup.ts    # Server health check before tests
        ├── playwright.config.ts
        ├── serve-test-page.ts # Express server for test page
        ├── test-page.html     # OpenLayers UMD + MVT source
        └── render.spec.ts     # Canvas pixel verification
```
