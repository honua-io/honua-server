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
   - ArcGIS: FeatureServer endpoint
   - QGIS: OGC API Features endpoint
4. **Validate a Query**
   - Run a simple filter or bbox query to confirm data access.

**Minimal local setup:**
```bash
docker compose up -d
curl http://localhost:8080/healthz/ready
```

**GIS client endpoints:**
- ArcGIS Pro: `http://<host>/rest/services/{id}/FeatureServer`
- QGIS (OGC API Features): `http://<host>/ogc/features`

**Next Steps:**
- [Geospatial API Examples](API_EXAMPLES.md)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
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

**Next Steps:**
- [OData API Examples](API_EXAMPLES.md#odata-v4-api)
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
        tiles: ['http://<host>/tiles/parcels/{z}/{x}/{y}.mvt']
      }
    },
    layers: [{ id: 'parcels', type: 'fill', source: 'honua', 'source-layer': 'parcels' }]
  }
});
```

**Next Steps:**
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
- [Security Configuration](../devops/SECURITY_CONFIGURATION.md)
- [Performance Monitoring](../devops/performance-monitoring.md)
