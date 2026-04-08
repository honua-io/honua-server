# Honua Integration Test Suite (Python)

Python-based integration tests for Honua Server, covering OGC API Features, GeoServices REST/FeatureServer, STAC client compatibility, and GDAL/OGR interoperability.

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
pytest -m stac           # STAC client compatibility only

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
├── stac_client/             # STAC client compatibility tests
│   ├── conftest.py          # Snapshot-seeded runtime + evidence writer
│   └── test_client_compat.py
├── pyqgis/                  # PyQGIS desktop client compatibility tests
│   ├── conftest.py          # PyQGIS runtime + cert evidence writer
│   ├── test_oapif_client_compat.py
│   ├── test_wfs_client_compat.py
│   └── test_render_path.py
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
5. **PySTAC tooling** - Installed from `requirements.txt` and used by the STAC compatibility lane.
6. **PyQGIS** (optional) - Requires a system QGIS installation (3.28+). PyQGIS is **not** installable via pip; it is provided by the QGIS installation. Tests are skipped automatically when `qgis.core` cannot be imported. See the [PyQGIS lane](#pyqgis-lane) section below.

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
| `HONUA_STAC_COMPAT_BASE_URL` | Reuse an already-hosted STAC endpoint instead of starting a local server | unset |
| `HONUA_STAC_COMPAT_SEED_PATH` | SQL snapshot applied by the STAC compatibility lane when it starts its own database | `tests/seed/client-compat-v1.sql` |
| `HONUA_STAC_COMPAT_SEED_SNAPSHOT` | Snapshot name recorded in STAC compatibility evidence | `client-compat-v1.sql` |

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
| `@pytest.mark.stac` | STAC client compatibility tests using PySTAC and PySTAC-Client |
| `@pytest.mark.pyqgis` | PyQGIS desktop client compatibility tests (require QGIS installation) |
| `@pytest.mark.cert` | CERT-\* certification case marker (argument: CERT-ID string) |

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

The STAC lane emits both machine-readable and human-readable evidence files at the end of each run:

- `tests/python/stac-client-compat-results*.json`
- `tests/python/stac-client-compat-results*.md`

Each report includes the runtime server version, local git commit SHA, and the seed snapshot name used for the run.

## PyQGIS Lane

The `tests/python/pyqgis/` package exercises OGC API Features and WFS endpoints with real QGIS providers. It auto-skips when PyQGIS is not available.

### Running locally

```bash
# Requires QGIS installed with python3-qgis
QT_QPA_PLATFORM=offscreen pytest tests/python/pyqgis -m pyqgis --tb=short
```

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `HONUA_PYQGIS_BASE_URL` | Target an already-running server | unset (starts local) |
| `HONUA_PYQGIS_SERVICE_ID` | Service ID in the compatibility seed | `test_service` |
| `HONUA_PYQGIS_COLLECTION_ID` | Collection ID in the seed | `0` |
| `HONUA_PYQGIS_SEED_PATH` | SQL snapshot for the local runtime | `tests/seed/client-compat-v1.sql` |
| `HONUA_PYQGIS_PORT` | Base server port (worker index added for xdist) | `5575` |
| `HONUA_PYQGIS_TIMEOUT` | Server startup timeout (seconds) | `120` |
| `QGIS_PREFIX_PATH` | QGIS installation prefix | auto-detected |

### Evidence Output

The PyQGIS lane produces per-protocol `.cert.json` certification envelopes under `tests/TestResults/`:

- `<run-id>-desktop-qgis-ogc-features.cert.json`
- `<run-id>-desktop-qgis-wfs.cert.json`

Each envelope follows the [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) schema with the actual QGIS version captured from the runtime.

The [visual / style certification slice](../gis/visual-style-certification-slice.md) (ticket [`#478`](https://github.com/honua-io/honua-server/issues/478)) is substantiated by `tests/python/pyqgis/test_render_path.py`, which drives the `render_layer_headless_with_symbol` fixture in `tests/python/pyqgis/conftest.py` to assert `CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, and `CERT-RNDR-FIL-01` against the declared RGB constants and flows those results into the `desktop-qgis-ogc-features` envelope. Rendering is locked to **EPSG:3857 (Web Mercator)** via `QgsMapSettings.setDestinationCrs`; because `QgsMapSettings.setExtent` stores its argument in the destination CRS, the fixture also projects `layer.extent()` through a `QgsCoordinateTransform` before handing it to `setExtent` so the rendered region matches the projected feature footprint rather than collapsing near the prime meridian. Unlike the JS lane collectors, `CertificationEvidenceCollector` in `tests/python/pyqgis/conftest.py` does not seed unexercised core IDs, so the PyQGIS envelope only carries the slice IDs that are explicitly recorded — today that is `CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, and `CERT-RNDR-FIL-01` (the two pixel-threshold variants emit `skip` on threshold miss). `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01`, and `CERT-RNDR-URL-01` are tracked in the slice spec's pending-fixture table rather than seeded into the PyQGIS envelope.

### Nightly CI

The `pyqgis-client-compat-nightly.yml` workflow runs this lane daily at 7:30 AM UTC against `ubuntu-24.04` with PostGIS, QGIS/PyQGIS, and `xvfb`. Evidence artifacts are uploaded automatically.

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
- The STAC compatibility lane writes one JSON/Markdown evidence pair per worker when `pytest-xdist` is enabled.
- The PyQGIS lane uses worker-scoped ports (`HONUA_PYQGIS_PORT` + worker index) to avoid port collisions under xdist.

### Database connection errors
- Verify PostGIS container is healthy
- Check Docker resource limits

### Server startup failures
- Run `dotnet build` first
- Check server logs for errors
