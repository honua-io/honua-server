# Server Management API (Control Plane)

The Server Management API powers the Honua Admin UI and supports headless automation (connections, publishing, imports, and operations). It is separate from the geospatial data access APIs.

**Scope**: High-level usage and minimal examples. For endpoint discovery and config metadata, use `/api/v1/admin/config`.

## **When to Use the Management API**

| Scenario | Example Use Case | Benefits |
|----------|------------------|----------|
| **Platform Integration** | Embed Honua into existing platforms | Automated provisioning |
| **CI/CD Automation** | Publish layers from pipelines | Repeatable releases |
| **Headless Operations** | Manage without the UI | Scriptable workflows |
| **Data Orchestration** | Import and republish data | Controlled lifecycle |

---

## **API Structure**

### **Base Endpoints**

| Endpoint | Purpose |
|----------|---------|
| `/api/v1/admin` | Admin API root |
| `/api/v1/admin/config` | Runtime configuration and env var reference |
| `/openapi.json` | OGC API Features OpenAPI schema |
| `/healthz/live` | Liveness check |
| `/healthz/ready` | Readiness check |

### **Common Resource Groups**

```
/api/v1/admin/
|-- config
|-- version, capabilities, manifest
|-- connections/              # Database connections and layers
|-- metadata/resources/       # Metadata resources
|-- metadata/layers/{id}/style
|-- import/                   # Import workflows
|-- operations/               # Long-running operations
|-- performance/              # Query cache statistics
```

Additional metrics endpoints:
```
/api/v1/metrics/
|-- health
|-- performance
|-- database
|-- cache
|-- memory
```

**Note**: Exact endpoints and payloads vary by build. Use `/api/v1/admin/config` for runtime validation, and `docs/api-specs/admin-api.json` for the current admin contract snapshot in this repo.

---

## **Connection Management (Minimal Example)**

```http
POST /api/v1/admin/connections
Content-Type: application/json

{
  "name": "primary-db",
  "host": "localhost",
  "port": 5432,
  "databaseName": "honua",
  "username": "postgres",
  "password": "secure-password",
  "sslMode": "Require"
}
```

---

## **Layer Publishing (Minimal Example)**

```http
POST /api/v1/admin/connections/{connectionId}/layers
Content-Type: application/json

{
  "schema": "public",
  "table": "parcels",
  "layerName": "city-parcels",
  "geometryColumn": "geom",
  "srid": 4326
}
```

---

## **Data Import (Minimal Example)**

```http
POST /api/v1/admin/import/upload
Content-Type: multipart/form-data

file=@parcels.geojson
```

---

## **Layer Style (Minimal Example)**

```http
PUT /api/v1/admin/metadata/layers/{layerId}/style
Content-Type: application/json

{
  "mapLibreStyle": { "version": 8, "sources": {}, "layers": [] }
}
```

---

## **Health Checks**

```http
GET /healthz/ready
GET /healthz/live
```

---

## **Related Documentation**

- [Admin UI](admin-ui.md)
- [Geospatial API Examples](API_EXAMPLES.md)
- [Security](../devops/security.md)
