# Honua Integration Test Suite (Python)

Python-based integration tests for Honua Server, covering OGC API Features, GeoServices REST/FeatureServer, and GDAL/OGR interoperability.

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
pytest -m gdal           # GDAL/OGR interop only

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
├── feature_server/          # GeoServices REST tests
│   ├── test_metadata.py
│   ├── test_query.py
│   ├── test_apply_edits.py
│   ├── test_related_records.py
│   ├── test_attachments.py
│   └── test_tiles.py
└── gdal_ogr/                # GDAL/OGR interoperability tests
    ├── conftest.py           # GDAL fixtures, evidence collector
    ├── test_oapif_discovery.py
    ├── test_oapif_read.py
    ├── test_oapif_query.py
    ├── test_oapif_export.py
    ├── test_wfs_discovery.py
    ├── test_wfs_read.py
    ├── test_wfs_query.py
    └── test_wfs_export.py
```

## Prerequisites

1. **Docker** - Required for Testcontainers (PostGIS)
2. **Python 3.11+** - Required for type hints and features
3. **Built Honua Server** - Run `dotnet build` before tests
4. **GDAL tools** (optional) - Install `gdal-bin` to run GDAL/OGR interoperability tests. Requires GDAL 3.4+. Tests are skipped automatically when `ogrinfo` is not found.

## Configuration

The test suite automatically:
- Starts a PostGIS container using Testcontainers
- Starts the Honua server process
- Creates isolated database schemas per worker for parallel execution
- Cleans up after tests complete

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `HONUA_TEST_PORT` | Base server port (worker index is added) | 5555 |
| `HONUA_TEST_TIMEOUT` | Server startup timeout (seconds) | 60 |
| `HONUA_TEST_CONFIGURATION` | dotnet build configuration for the test server | Debug |
| `HONUA_TEST_DB_URL` | Use external PostGIS database (opt-in) | unset |
| `HONUA_TEST_DB_SEED_PATH` | Auto-apply YAML seed to new schemas | unset |
| `HONUA_TEST_DB_SEED_PROFILE` | Seed profile name | unset |

## Test Markers

| Marker | Description |
|--------|-------------|
| `@pytest.mark.integration` | Requires database and server |
| `@pytest.mark.ogc` | OGC API Features protocol |
| `@pytest.mark.featureserver` | GeoServices REST protocol |
| `@pytest.mark.geometry` | Geometry validation tests |
| `@pytest.mark.slow` | Long-running tests |
| `@pytest.mark.smoke` | Quick sanity checks |
| `@pytest.mark.gdal` | GDAL/OGR interoperability tests (require gdal-bin) |

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

## Using Docker Compose Test Profile

```bash
# Start the opt-in PostGIS test database
docker compose --profile test up -d postgis-test

# Reuse the database for tests
export HONUA_TEST_DB_URL="postgres://test:test@localhost:5433/honua_test"
pytest
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

### Running with pytest-xdist
- Each worker uses a separate schema and increments the base port (`HONUA_TEST_PORT`)
- For a shared PostGIS container, set `HONUA_TEST_DB_URL` so all workers connect to the same DB

### Database connection errors
- Verify PostGIS container is healthy
- Check Docker resource limits

### Server startup failures
- Run `dotnet build` first
- Check server logs for errors
