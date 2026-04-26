# Client Compatibility Testing

Validates that desktop GIS and BI clients can connect to and work with Honua Server. The test environment runs in WSL/Docker while the actual desktop apps run on Windows.

## Prerequisites

- Docker (accessible from WSL)
- One or more desktop clients installed on Windows:
  - [QGIS](https://qgis.org/) 3.34+
  - [ArcGIS Pro](https://www.esri.com/en-us/arcgis/products/arcgis-pro/overview) 3.x
  - [Excel](https://www.microsoft.com/en-us/microsoft-365/excel) (Microsoft 365 / Office 2019+)
  - [Power BI Desktop](https://powerbi.microsoft.com/desktop/)

## Quick Start

```bash
# 1. Start the server (WSL terminal)
./scripts/client-compat/client-compat-server.sh

# 2. Run client tests (Windows PowerShell)
.\scripts\client-compat\run-client-compat-tests.ps1

# 3. Teardown when done (WSL terminal)
./scripts/client-compat/client-compat-server.sh --teardown
```

## What Happens

### WSL: `client-compat-server.sh`

Builds the Honua Server Docker image, starts PostGIS + Honua via `docker/client-compat-compose.yml`, seeds the database with `docker/client-compat-seed.sql`, and verifies all protocol endpoints are responding.

The seed data creates a `compat` service with 9 layers covering all geometry types, CRS variations, and edge cases:

| Layer | Name | Geometry | SRID | Features | Purpose |
|-------|------|----------|------|----------|---------|
| 0 | Cities | Point | 4326 | 1200 | Pagination stress, OData query |
| 1 | Rivers | LineString | 4326 | 50 | WMS line rendering |
| 2 | Counties | Polygon | 4326 | 200 | MapServer identify/find |
| 3 | Sensors | MultiPoint | 4326 | 100 | Multi-geometry handling |
| 4 | Pipelines | MultiLineString | 4326 | 30 | Complex line geometry |
| 5 | Parcels | MultiPolygon | 4326 | 80 | Complex polygon geometry |
| 6 | WebMercPts | Point | 3857 | 50 | CRS transformation |
| 7 | UTMParcels | Polygon | 32610 | 50 | CRS mix |
| 8 | Events | Point | 4326 | 200 | Temporal fields, 5 null-geometry features |

Data is deterministic (`setseed(0.42)` + `generate_series`) so results are reproducible across runs.

### Windows: `run-client-compat-tests.ps1`

For each installed client, the script:

1. **Generates a connection file** pre-configured with the server URL
2. **Launches the desktop app** with that file
3. **Displays verification steps** to check in the app
4. **Prompts for pass/fail** confirmation

| Client | Connection File | How It Opens |
|--------|----------------|-------------|
| QGIS | `.qgs` project (WMS + OGC Features layers) | Opens with layers already loaded |
| ArcGIS Pro | ArcPy script (`add-honua-layers.py`) | Paste in Pro's Python window |
| Excel | `.odc` connection file | Use Data > Get Data > OData Feed |
| Power BI | `.pbids` OData connection file | Navigator opens to the OData feed |

Results are saved to `client-compat-results/`.

## Options

```powershell
# Test a single client
.\scripts\client-compat\run-client-compat-tests.ps1 -Client qgis

# Custom server URL
.\scripts\client-compat\run-client-compat-tests.ps1 -BaseUrl http://192.168.1.50:8080

# Custom output directory
.\scripts\client-compat\run-client-compat-tests.ps1 -OutputDir my-results
```

## Protocol Endpoints

Once the server is running, these endpoints are accessible from Windows at `http://localhost:8080`:

| Protocol | URL |
|----------|-----|
| FeatureServer | `/rest/services/compat/FeatureServer` |
| MapServer | `/rest/services/compat/MapServer` |
| WMS 1.3 | `/rest/services/compat/MapServer/WMS` |
| WMTS 1.0 | `/rest/services/compat/MapServer/WMTS` |
| OGC API Features | `/ogc/features` |
| OData v4 | `/odata` |
| OData $metadata | `/odata/$metadata` |

Admin API key for write operations: `compat-admin-password` (via `X-API-Key` header).

## Files

```
docker/
  client-compat-seed.sql        # Seed data (9 layers, 1960 features)
  client-compat-compose.yml     # PostGIS 17 on port 5437 + Honua on 8080
scripts/
  client-compat/
    client-compat-server.sh       # WSL: start/seed/teardown
    run-client-compat-tests.ps1   # Windows: generate connections, launch apps, record results
    README.md                     # This file
```
