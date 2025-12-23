# Honua Integration Test Suite (Python)

Python-based integration tests for Honua Server, covering OGC API Features and GeoServices REST/FeatureServer protocols.

## Overview

This test suite provides comprehensive integration testing using:
- **pytest** with parallel execution (pytest-xdist)
- **Testcontainers** for PostGIS database
- **shapely/geopandas** for geometry validation
- **httpx** for HTTP requests

## Quick Start

```bash
# Install dependencies
pip install -r requirements.txt

# Run all tests
pytest

# Run with parallelization
pytest -n auto

# Run specific protocol tests
pytest -m ogc          # OGC API Features only
pytest -m featureserver # GeoServices REST only

# Run smoke tests (quick validation)
pytest -m smoke
```

## Test Structure

```
tests/python/
├── conftest.py              # Shared fixtures
├── pytest.ini               # Pytest configuration
├── requirements.txt         # Python dependencies
├── shared/                  # Shared infrastructure
│   ├── geometry.py          # Geometry generators
│   ├── postgis.py           # PostGIS Testcontainers
│   └── server.py            # Honua server management
├── ogc_features/            # OGC API Features tests
│   ├── test_landing_page.py
│   ├── test_conformance.py
│   ├── test_collections.py
│   └── test_items.py
└── feature_server/          # GeoServices REST tests
    ├── test_metadata.py
    ├── test_query.py
    ├── test_apply_edits.py
    ├── test_related_records.py
    ├── test_attachments.py
    └── test_tiles.py
```

## Prerequisites

1. **Docker** - Required for Testcontainers (PostGIS)
2. **Python 3.11+** - Required for type hints and features
3. **Built Honua Server** - Run `dotnet build` before tests

## Configuration

The test suite automatically:
- Starts a PostGIS container using Testcontainers
- Starts the Honua server process
- Creates isolated database schemas per test
- Cleans up after tests complete

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `HONUA_TEST_PORT` | Server port | 5555 |
| `HONUA_TEST_TIMEOUT` | Server startup timeout (seconds) | 60 |

## Test Markers

| Marker | Description |
|--------|-------------|
| `@pytest.mark.integration` | Requires database and server |
| `@pytest.mark.ogc` | OGC API Features protocol |
| `@pytest.mark.featureserver` | GeoServices REST protocol |
| `@pytest.mark.geometry` | Geometry validation tests |
| `@pytest.mark.slow` | Long-running tests |
| `@pytest.mark.smoke` | Quick sanity checks |

## Geometry Coverage

The test suite validates all GeoJSON geometry types:
- Point, MultiPoint
- LineString, MultiLineString
- Polygon (simple, with holes)
- MultiPolygon (with mixed configurations)
- GeometryCollection
- Null geometries

## Coverage Goals

- **API Surface**: 100% - Every implemented endpoint has tests
- **Query Parameters**: Comprehensive coverage of filtering, pagination
- **Geometry Types**: All GeoJSON types validated
- **Error Cases**: Invalid inputs return appropriate errors

## Running in CI

For CI pipelines:

```bash
# Install dependencies
pip install -r requirements.txt

# Build Honua server (required before tests)
dotnet build Honua.sln

# Run smoke tests for PRs
pytest -m smoke --tb=short

# Run full suite for nightly builds
pytest -n auto --tb=short
```

## Adding New Tests

1. Create test file in appropriate directory
2. Use shared fixtures from `conftest.py`
3. Apply appropriate markers (`@pytest.mark.integration`, etc.)
4. Follow naming convention: `test_<feature>_<scenario>`

Example:
```python
@pytest.mark.integration
@pytest.mark.ogc
def test_items_returns_geojson(http_client, test_collection_id):
    response = http_client.get(f"/ogc/features/collections/{test_collection_id}/items")
    assert response.status_code == 200
    data = response.json()
    assert data["type"] == "FeatureCollection"
```

## Troubleshooting

### Tests hanging on startup
- Ensure Docker is running
- Check if ports 5432 and 5555 are available
- Increase `HONUA_TEST_TIMEOUT` if needed

### Database connection errors
- Verify PostGIS container is healthy
- Check Docker resource limits

### Server startup failures
- Run `dotnet build` first
- Check server logs for errors
