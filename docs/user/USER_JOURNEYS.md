# User Journeys

Pick the path that matches your role and goal. Each journey is intentionally short and links to deeper docs.

---

## **GIS Professional Journey**

**Goal**: Connect ArcGIS Pro, QGIS, or other GIS desktop tools to Honua data.

### Quick Start Path

1. **Deploy Honua**
   - Use Docker Compose for local or small-team setups.
2. **Add Data**
   - Upload files or publish existing PostGIS tables via the Admin UI.
3. **Connect a GIS Client**
   - ArcGIS: FeatureServer or MapServer endpoint (data vs maps)
   - QGIS: OGC API Features endpoint
4. **Validate a Query**
   - Run a simple filter or bbox query to confirm data access.

**Minimal local setup:**
```bash
docker compose up -d
curl http://localhost:8080/healthz/ready
```

**GIS client endpoints:**
- ArcGIS Pro (data): `http://<host>/rest/services/{id}/FeatureServer`
- ArcGIS Pro (maps): `http://<host>/rest/services/{id}/MapServer`
- QGIS (OGC API Features): `http://<host>/ogc/features`

**Next Steps:**
- [QGIS Getting Started Tutorial](tutorials/qgis-getting-started.md)
- [Geospatial API Examples](API_EXAMPLES.md)
- [Interactive API Explorer](http://localhost:8080/docs) *(requires running server)*
- [Client Templates + Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
- [MapServer Coverage Matrix](map-server-matrix.md)
- [Protocols Overview](STANDARDS_APIS.md)

---

## **Data Analyst Journey**

**Goal**: Access spatial data in Excel, Power BI, Tableau, or other BI tools via OData.

### Quick Start Path

1. **Connect to OData**
   - Use the OData feed URL: `http://<host>/odata`
2. **Select Tables and Fields**
   - Choose your layers and attributes.
3. **Apply Filters**
   - Use `$filter`, `$select`, and `$top` to limit data.

**Example OData query:**
```text
http://<host>/odata/Features?$select=id,name,population&$filter=population%20gt%2010000&$top=50
```

**GeoParquet export for analytics pipelines:**
```bash
curl "http://<host>/rest/services/1/FeatureServer/0/query?where=1%3D1&outFields=*&f=parquet" --output features.parquet
```
Use `f=parquet` to export query results as GeoParquet 1.1.0 for direct use with DuckDB, pandas/geopandas, or other columnar analytics tools. Results are subject to `maxRecordCount`; compare the returned row count against the service limit to detect truncation.

**Next Steps:**
- [OData API Examples](API_EXAMPLES.md#odata-v4-api)
- [GeoParquet Export Examples](API_EXAMPLES.md#geoservices-rest-api)
- [Client Templates + Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
- [Data Modeling Guide](DATA_MODELING_GUIDE.md)

---

## **Web Developer Journey**

**Goal**: Build interactive web maps and spatial web apps.

### Quick Start Path

1. **Pick a mapping library** (MapLibre, Leaflet, OpenLayers)
2. **Use vector tiles for maps** and OGC API Features for queries
3. **Ship a minimal map**, then iterate on styling and UX

**Minimal MapLibre setup:**
```javascript
import maplibregl from 'maplibre-gl';

new maplibregl.Map({
  container: 'map',
  style: {
    version: 8,
    sources: {
      honua: {
        type: 'vector',
        tiles: ['http://<host>/tiles/0/{z}/{x}/{y}.mvt']
      }
    },
    layers: [{ id: 'layer', type: 'fill', source: 'honua', 'source-layer': 'layer' }]
  }
});
```

**Next Steps:**
- [Interactive API Explorer](http://localhost:8080/docs) *(requires running server)* — test endpoints in the browser
- [Vector Tiles API Examples](API_EXAMPLES.md#vector-tiles-mvt)
- [OGC API Examples](API_EXAMPLES.md#ogc-api-features)
- [Integration Patterns](INTEGRATION_PATTERNS.md)

---

## **DevOps Engineer Journey**

**Goal**: Deploy, configure, and monitor Honua Server in production.

### Quick Start Path

1. **Choose a deployment model**
   - Docker Compose for single-node, Kubernetes for production.
2. **Configure secrets and TLS**
   - Use your platform's secret manager and edge TLS termination.
3. **Enable health checks and monitoring**
   - `/healthz/ready` and `/healthz/live` should be part of your probes.
4. **Scale and optimize**
   - Tune database connection pools and add caching where appropriate.

**Next Steps:**
- [Deployment Scenarios](../devops/DEPLOYMENT_SCENARIOS.md)
- [Security](../devops/security.md)
- [Monitoring](../devops/monitoring.md)
