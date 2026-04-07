# QGIS Getting Started with Honua

Connect QGIS to Honua Server via OGC API Features and query geospatial data in under 5 minutes.

## Prerequisites

- [QGIS](https://qgis.org/en/site/forusers/download.html) 3.28+ installed
- Docker and Docker Compose (v2+)

## 1. Start Honua Server

```bash
docker compose -f infrastructure/docker-compose/docker-compose.yml up -d
```

Wait for the server to be ready:

```bash
curl -s http://localhost:8080/healthz/ready
# Expected: Ready
```

## 2. Import Sample Data

Upload a GeoJSON file through the Admin API. Save this as `sample.geojson`:

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "properties": { "name": "City Hall", "category": "government", "population": 50000 },
      "geometry": { "type": "Point", "coordinates": [-122.4194, 37.7749] }
    },
    {
      "type": "Feature",
      "properties": { "name": "Central Park", "category": "park", "population": 0 },
      "geometry": { "type": "Point", "coordinates": [-73.9654, 40.7829] }
    },
    {
      "type": "Feature",
      "properties": { "name": "Library", "category": "education", "population": 1200 },
      "geometry": { "type": "Point", "coordinates": [-87.6298, 41.8781] }
    }
  ]
}
```

Import it:

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/upload \
  -F "file=@sample.geojson" \
  -F "TableName=places"
```

## 3. Publish the Imported Layer

The upload creates a database table. To make it available as an OGC collection, register a connection and publish the layer.

**Register a connection** (skip if you already have one). Use the credentials from `infrastructure/docker-compose/docker-compose.yml`:

```bash
curl -X POST http://localhost:8080/api/v1/admin/connections \
  -H "Content-Type: application/json" \
  -d '{
    "name": "local",
    "host": "postgres",
    "port": 5432,
    "databaseName": "honua_dev",
    "username": "honua",
    "password": "honua_dev_password",
    "sslMode": "Prefer"
  }'
# Note the connectionId from the response: { "success": true, "data": { "connectionId": "..." } }
```

**Discover the imported table** to confirm its schema:

```bash
curl -s http://localhost:8080/api/v1/admin/connections/{connectionId}/tables | jq '.data'
# Look for the "places" table — note the schema (typically "honua")
```

**Publish the imported table** (replace `{connectionId}` and `{schema}` with the discovered values):

```bash
curl -X POST http://localhost:8080/api/v1/admin/connections/{connectionId}/layers \
  -H "Content-Type: application/json" \
  -d '{ "schema": "{schema}", "table": "places", "layerName": "places" }'
```

## 4. Connect QGIS to Honua

1. Open QGIS
2. Go to **Layer > Add Layer > Add OGC API Features Layer** (or press `Ctrl+L` and select the **OGC API Features** tab)
3. Click **New** to create a connection:
   - **Name:** `Honua Local`
   - **URL:** `http://localhost:8080/ogc/features`
4. Click **Connect**

QGIS discovers available collections from the Honua landing page.

## 5. Add a Layer

1. In the connection browser, expand the collections list
2. Select your imported collection (e.g., `places`)
3. Click **Add** to load it as a map layer

The features appear on the map canvas and in the attribute table.

## 6. Query Features

### Attribute Filter

1. Right-click the layer > **Filter...**
2. Enter a filter expression:
   ```
   "category" = 'park'
   ```
3. Click **OK** -- the map updates to show only matching features

### Spatial Filter

1. Use the **Sketching tool** or **Select by Rectangle** to define a bounding box
2. Right-click the layer > **Filter...** and add a spatial constraint, or use the **Query Builder** with a bbox expression

### View Attributes

1. Right-click the layer > **Open Attribute Table** (or press `F6`)
2. Browse feature attributes, sort columns, and select individual features

## 7. Export Data

1. Right-click the layer > **Export > Save Features As...**
2. Choose format: GeoPackage, GeoJSON, CSV, or Shapefile
3. Set CRS if needed (default: EPSG:4326)
4. Click **OK**

## Using the QGIS Project Template

A pre-configured QGIS project template source is available in the repository:

```
docs/user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template
```

For repeatable certification runs, prefer the generated workflow pack at `artifacts/client-compat/<service>-<timestamp>/pack/templates/desktop/qgis/`.

To use the repo sources directly:

```bash
mkdir -p /tmp/honua-client-templates/desktop/qgis
cp docs/gis/client-templates/.env.example /tmp/honua-client-templates/.env
cp docs/user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template /tmp/honua-client-templates/desktop/qgis/

cd /tmp/honua-client-templates
# Edit .env with your server URL and collection ID (`HONUA_COLLECTION_ID` is currently the numeric layer id)

set -a; source .env; set +a
envsubst < desktop/qgis/Honua-Desktop-Smoke.qgs.template > desktop/qgis/Honua-Desktop-Smoke.qgs
```

Open the generated `.qgs` file in QGIS, or package it as `.qgz` using the client-template runbook.

## Alternative: Connect via WFS

QGIS also supports WFS 2.0 connections to Honua. This is validated nightly by the automated PyQGIS compatibility suite.

1. Go to **Layer > Add Layer > Add WFS / OGC API - Features Layer** (or press `Ctrl+L` and select the **WFS / OGC API Features** tab)
2. Click **New** to create a connection:
   - **Name:** `Honua WFS`
   - **URL:** `http://localhost:8080/wfs`
3. Click **Connect**
4. Select a feature type and click **Add**

WFS uses GetCapabilities for type discovery and supports attribute and spatial filtering via the QGIS WFS provider.

## Verify OGC API Features Endpoints

These endpoints are used by QGIS under the hood:

| What QGIS Does | Endpoint |
|---|---|
| Discover collections | `GET /ogc/features/collections` |
| Get collection metadata | `GET /ogc/features/collections/{id}` |
| Fetch features | `GET /ogc/features/collections/{id}/items` |
| Get queryable properties | `GET /ogc/features/collections/{id}/queryables` |
| Get API spec | `GET /openapi.json` |

## Troubleshooting

**Connection refused**: Ensure the Docker container is running and port 8080 is accessible.

**No collections found**: Verify data has been imported. Check the admin API:
```bash
curl http://localhost:8080/api/v1/admin/connections
```

**CRS mismatch**: Honua serves data in EPSG:4326 by default. QGIS will reproject to the project CRS automatically.

## Next Steps

- Explore the [API Examples](../../developer/API_EXAMPLES.md) for curl-based access patterns
- Review the [OGC API Features Coverage](../specifications/ogc-api-features-coverage.md) for supported parameters
- Try the [Interactive API Explorer](http://localhost:8080/docs) to test endpoints directly
