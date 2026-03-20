# Server Management API (Control Plane)

The Server Management API powers the Honua Admin UI and supports headless automation (connections, publishing, imports, and operations). It is separate from the geospatial data access APIs.

**Scope**: High-level usage and minimal examples. For endpoint discovery and config metadata, use `/api/v1/admin/config`.

## **GitOps Direction**

The Honua Admin UI is intended to operate as a UI on top of this control-plane API.

- This API is the foundation for a Honua-managed control plane and related automation surfaces.
- Honua is not positioning Flux or Argo CD as its primary control plane.
- Helm and Terraform remain infrastructure and packaging surfaces around the platform.
- Deploy coordination, upgrade readiness, and change-management workflows are expected to be expressed through the Honua control plane.
- This public API is intentionally broader than any single operator product. Base control-plane primitives remain documented here even when higher-level AI DevOps/operator tooling is delivered through private enterprise surfaces.

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
| `/api/v1/admin/openapi.json` | Admin API OpenAPI schema snapshot served by runtime |
| `/openapi.json` | OGC API Features OpenAPI schema |
| `/healthz/live` | Liveness check |
| `/healthz/ready` | Readiness check |

### **Common Resource Groups**

```
/api/v1/admin/
|-- config
|-- version, capabilities, manifest
|-- connections/              # Secure connections + table/layer publishing
|-- services/                 # Service-level protocol and MapServer settings
|-- metadata/resources/       # Metadata resources
|-- metadata/layers/{id}/style
|-- deploy/                   # Instance-local deploy preflight and rollout primitives
|-- import/                   # Import workflows
|-- operations/               # Long-running operations
|-- performance/database/     # Query cache statistics
|-- observability/            # Recent errors and telemetry status
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

**Note**: Some admin surfaces vary by build. SDK-facing compatibility should not guess. Use `GET /api/v1/admin/capabilities` for the stable runtime handshake, `/api/v1/admin/config` for runtime validation details, and `/api/v1/admin/openapi.json` for the bundled `docs/api-specs/admin-api.json` contract snapshot used for SDK generation.

## **SDKs and Contract Governance**

- Runtime SDK handshake:
- Use `GET /api/v1/admin/capabilities` as the canonical compatibility document.
- Treat `data.compatibility` as the stable SDK-facing shape for server version, control-plane major, release channel, deprecation markers, and coarse feature flags.
- Do not infer feature support from `serverVersion` alone, and do not probe multiple endpoints when `data.compatibility` is present.

- Generate control-plane SDK artifacts locally:

```bash
./scripts/validate-openapi-contracts.sh
./scripts/generate-control-plane-sdks.sh
```

- CI contract governance and SDK generation:
- `.github/workflows/openapi-contract-governance.yml`
- `.github/workflows/control-plane-sdk-governance.yml`

- Versioning/deprecation policy:
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)

- Migration and upgrade guidance:
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)

- AI/agent integration:
- [MCP Server](MCP_SERVER.md)

---

## **Authentication Behavior**

- Supported admin auth schemes:
- `X-API-Key` (default)
- `Authorization: Bearer <jwt>` when OIDC is enabled
- `Authorization: Basic ...` only when Basic compatibility mode is enabled

- Precedence:
- Bearer is evaluated first when OIDC is enabled and a Bearer header is present
- Otherwise `X-API-Key` is evaluated
- Basic compatibility is evaluated only when enabled and no valid `X-API-Key` is present

- Challenge headers:
- `WWW-Authenticate: ApiKey ...` is always returned for API-key challenges
- `WWW-Authenticate: Basic ...` is added when Basic compatibility mode is enabled
- Bearer failures include standard Bearer challenge headers

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

### **Secure Connection Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/connections` | GET | List secure connections (credential material never returned) |
| `/api/v1/admin/connections` | POST | Create secure connection |
| `/api/v1/admin/connections/{id}` | GET | Get secure connection details |
| `/api/v1/admin/connections/{id}` | PUT | Update secure connection |
| `/api/v1/admin/connections/{id}` | DELETE | Delete secure connection |
| `/api/v1/admin/connections/test` | POST | Test a draft connection before saving |
| `/api/v1/admin/connections/{id}/test` | POST | Test health of an existing saved connection |
| `/api/v1/admin/connections/encryption/validate` | POST | Validate encryption service status |
| `/api/v1/admin/connections/encryption/rotate-key` | POST | Trigger key rotation workflow (runtime operation may be rejected by policy) |

### **Secure Connection Validation Rules**

- Supply either `password` or `secretReference` (+ `secretType`), but not both.
- Supported `sslMode` values: `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`.
- If `sslRequired=true`, `sslMode=Disable` is rejected.

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

### **Layer and Table Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/connections/{id}/tables` | GET | Discover PostGIS tables for a connection (`id` can be GUID or name) |
| `/api/v1/admin/connections/{id}/layers` | GET | List published layers for a connection |
| `/api/v1/admin/connections/{id}/layers` | POST | Publish a new layer from a table |
| `/api/v1/admin/connections/{id}/layers/{layerId}/enabled` | PUT | Enable/disable a specific published layer |
| `/api/v1/admin/connections/{id}/layers/enabled` | PUT | Enable/disable all layers in a service (bulk) |

### **Service Settings Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/services` | GET | List available services |
| `/api/v1/admin/services/{serviceName}/settings` | GET | Get protocol + MapServer settings for a service |
| `/api/v1/admin/services/{serviceName}/protocols` | PUT | Update enabled protocols for a service |
| `/api/v1/admin/services/{serviceName}/mapserver` | PUT | Update MapServer defaults/limits for a service |
| `/api/v1/admin/services/{serviceName}/access-policy` | PUT | Update service access policy (read/write role + anonymous controls) |
| `/api/v1/admin/services/{serviceName}/timeinfo` | PUT | Update service-level temporal metadata |
| `/api/v1/admin/services/{serviceName}/layers/{layerId}/metadata` | PUT | Patch layer-level access policy and time info |

---

## **Data Import (Minimal Example)**

```http
POST /api/v1/admin/import/upload
Content-Type: multipart/form-data

file=@parcels.geojson
```

FlatGeobuf (`.fgb`) files can be uploaded directly — no archive wrapping needed. If the `.fgb` file does not embed CRS in its header, provide `sourceSrid` on the import request; the server rejects imports when it cannot detect the source coordinate system.

For Esri File Geodatabases, use a `.gdb.zip` archive that contains the `.gdb` directory and preserves the directory structure inside the archive. See [FileGDB Import Workflow](FILEGDB_IMPORT_WORKFLOW.md).

For GeoParquet files, upload a `.parquet` or `.geoparquet` file directly. The server reads GeoParquet `geo` metadata for CRS detection and requires WKB geometry encoding. Non-WKB encodings are rejected. Nested column types (Struct, List, Map) are skipped with warnings. Rows with null geometry are skipped during both preview and import, and reported as warnings in the import response. Files with more than 100,000 rows in a single Parquet row group are rejected to maintain bounded memory usage; re-export such files with smaller row groups.

### **Import Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/formats` | GET | List supported import file formats |
| `/api/v1/admin/import/preview` | POST | Preview uploaded file content before import |
| `/api/v1/admin/import/preview-url` | POST | Preview data from a supported public object URL |
| `/api/v1/admin/import/upload` | POST | Upload and import data |
| `/api/v1/admin/import/upload-url` | POST | Import data from a supported public object URL |
| `/api/v1/admin/import/uploads` | GET | List active uploads |
| `/api/v1/admin/import/uploads/{uploadId}/progress` | GET | Get upload progress |
| `/api/v1/admin/import/uploads/{uploadId}/cancel` | POST | Cancel an upload |
| `/api/v1/admin/import/jobs` | GET | List active import jobs |
| `/api/v1/admin/import/jobs/{jobId}` | GET | Get import job status |
| `/api/v1/admin/import/jobs/{jobId}/cancel` | POST | Cancel an import job |
| `/api/v1/admin/import/limits` | GET | Get import limits/configuration |

### **GeoServices Import Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/geoservices/discover` | POST | Discover layers from an ArcGIS service URL |
| `/api/v1/admin/import/geoservices/start` | POST | Start ArcGIS layer import job |
| `/api/v1/admin/import/geoservices/jobs` | GET | List active GeoServices import jobs |
| `/api/v1/admin/import/geoservices/jobs/{jobId}` | GET | Get GeoServices import job status |
| `/api/v1/admin/import/geoservices/jobs/{jobId}/cancel` | POST | Cancel GeoServices import job |

---

## **Layer Style (Minimal Example)**

```http
PUT /api/v1/admin/metadata/layers/{layerId}/style
Content-Type: application/json

{
  "mapLibreStyle": { "version": 8, "sources": {}, "layers": [] }
}
```

### **Metadata and Style Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/version` | GET | Get legacy server + metadata API version info |
| `/api/v1/admin/capabilities` | GET | Get admin metadata capabilities and the SDK compatibility contract |
| `/api/v1/admin/manifest` | GET | Export metadata manifest |
| `/api/v1/admin/manifest/apply` | POST | Apply metadata manifest (supports dry-run/prune controls) |
| `/api/v1/admin/deploy/preflight` | GET | Get instance-local deploy preflight and upgrade-readiness state |
| `/api/v1/admin/metadata/resources` | GET | List metadata resources |
| `/api/v1/admin/metadata/resources` | POST | Create metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | GET | Get metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | PUT | Update metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | DELETE | Delete metadata resource |
| `/api/v1/admin/metadata/layers/{layerId}/style` | GET | Get layer style payload |
| `/api/v1/admin/metadata/layers/{layerId}/style` | PUT | Update layer style payload |

### **SDK Compatibility Handshake**

SDKs should call `GET /api/v1/admin/capabilities` once per authenticated session and cache the `data.compatibility` object.

- `controlPlaneApi.major`: reject unsupported majors without guessing path shape.
- `metadataSchemas`: prefer non-deprecated metadata schema versions when sending resource documents.
- `features`: branch on coarse capabilities such as manifest support instead of probing extra endpoints.
- `serverVersion` and `releaseChannel`: log or surface for diagnostics, rollout targeting, and support.

Focused guidance and a concrete JSON example:
- [SDK Compatibility Metadata](SDK_COMPATIBILITY_METADATA.md)

### **Operations and Monitoring Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/operations/{operationId}` | GET | Get operation progress/status |
| `/api/v1/admin/operations/{operationId}/cancel` | POST | Cancel operation |
| `/api/v1/admin/operations/active` | GET | List active operations |
| `/api/v1/admin/operations/type/{operationType}` | GET | List operations by type |
| `/api/v1/admin/performance/database/query-cache/statistics` | GET | Query cache performance statistics |
| `/api/v1/admin/observability/errors` | GET | Recent in-memory error buffer |
| `/api/v1/admin/observability/telemetry` | GET | Tracing/OTLP telemetry status |

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
- [FileGDB Import Workflow](FILEGDB_IMPORT_WORKFLOW.md)
- [Security](../devops/security.md)
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)
