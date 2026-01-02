# Esri Feature Server JavaScript Integration Tests

Comprehensive integration test suite for Honua's Esri Feature Service compatibility, written in TypeScript with Vitest.

## Test Matrix Coverage

### Spatial Relationships (17 variations)

| Category | Relationships |
|----------|---------------|
| Non-Distance | esriSpatialRelIntersects, esriSpatialRelContains, esriSpatialRelWithin, esriSpatialRelEnvelopeIntersects, esriSpatialRelCrosses, esriSpatialRelTouches, esriSpatialRelOverlaps, esriSpatialRelDisjoint, esriSpatialRelEquals |
| Distance-Based | esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance |
| Distance Units | esriSRUnit_Meter, esriSRUnit_Foot, esriSRUnit_Kilometer, esriSRUnit_StatuteMile |

### Geometry Types (11 variations)

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

### WHERE Clause Operators (16 cases)

- Comparison: `=`, `<>`, `<`, `>`, `<=`, `>=`
- Pattern: `LIKE`, `IN`, `BETWEEN`
- Null handling: `IS NULL`, `IS NOT NULL`
- Logical: `AND`, `OR`, `NOT`
- Grouping: Parentheses, nested expressions

### Output Formats

- `json` (Esri JSON)
- `geojson` (GeoJSON)

### Spatial References

- EPSG:4326 (WGS84)
- EPSG:3857 (Web Mercator)
- Input/Output transformation

## Running Tests

### Prerequisites

```bash
cd tests/js
npm install
```

### Environment Variables

```bash
HONUA_BASE_URL=http://localhost:5555    # Honua server URL
HONUA_SERVICE_ID=test_service_gw0       # Test service ID
HONUA_LAYER_ID=1000                     # Test layer ID
HONUA_TEST_TIMEOUT=30000                # Request timeout (ms)
```

### Run All Tests

```bash
npm test
```

### Run Specific Test Suites

```bash
npm run test:query      # Query endpoint tests
npm run test:matrix     # Spatial/geometry matrix tests
npm run test:geometry   # Geometry roundtrip tests
npm run test:edits      # ApplyEdits tests
npm run test:metadata   # Metadata endpoint tests
```

### Watch Mode

```bash
npm run test:watch
```

### Coverage Report

```bash
npm run test:coverage
```

## Test Structure

```
tests/js/
├── package.json           # Dependencies
├── vitest.config.ts       # Test configuration
├── tsconfig.json          # TypeScript config
├── shared/
│   ├── client.ts          # HTTP client for FeatureServer
│   ├── constants.ts       # Test constants and enums
│   ├── geometry.ts        # Geometry generator
│   └── index.ts           # Barrel export
└── feature-server/
    ├── query.test.ts          # Query endpoint tests
    ├── query-matrix.test.ts   # Spatial/geometry matrix
    ├── geometry-types.test.ts # Geometry roundtrip tests
    ├── apply-edits.test.ts    # Edit operations
    └── metadata.test.ts       # Service/layer metadata
```

## Test Counts

| File | Test Cases |
|------|------------|
| query.test.ts | ~80 tests |
| query-matrix.test.ts | ~100+ tests (matrix expansion) |
| geometry-types.test.ts | ~60 tests |
| apply-edits.test.ts | ~50 tests |
| metadata.test.ts | ~40 tests |
| **Total** | **~330+ tests** |

## Comparison with Python Tests

This JavaScript test suite mirrors the Python test suite structure:

| Python File | JavaScript File |
|-------------|-----------------|
| `test_query.py` | `query.test.ts` |
| `test_query_matrix.py` | `query-matrix.test.ts` |
| `test_geometry_types.py` | `geometry-types.test.ts` |
| `test_apply_edits.py` | `apply-edits.test.ts` |
| `test_metadata.py` | `metadata.test.ts` |

Both suites use the same:
- Test coordinates (San Francisco area)
- Geometry generation patterns
- Matrix parameterization approach
- Assertion helpers
