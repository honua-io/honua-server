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
|-- deploy/                   # Deploy preflight, plan, operations, submit, rollback
|-- import/                   # Import workflows
|-- operations/               # Long-running operations
|-- performance/database/     # Query cache statistics
|-- observability/            # Recent errors and telemetry status
|-- alerts/                   # Alert zones and rules
|-- license/                  # License status, upload, entitlements
|-- roles/                    # Role CRUD and permissions
|-- users/                    # User management and effective permissions
|-- oidc/providers/           # OIDC provider CRUD and testing
|-- feature-events/           # Feature change event replay
|-- manifest/drift            # Manifest drift, versions
|-- manifest/pending/         # Manifest approval workflows
|-- gitops/                   # Git repository watching and change history
|-- tile-operations/          # Tile operation jobs
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

**Note**: Some admin surfaces vary by build. SDK-facing compatibility should not guess. Use `GET /api/v1/admin/capabilities` for the stable runtime handshake, `/api/v1/admin/config` for runtime validation details, and `/api/v1/admin/openapi.json` for the bundled `docs/developer/api-specs/admin-api.json` contract snapshot used for SDK generation.

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

For source-system migration planning, use the unified scan endpoint before starting any GeoServer or GeoServices import job. The scan response is a deterministic inventory artifact that records source identity and version, authentication posture, scan completeness, containers, resources, styles or renderers, external dependencies, spatial reference details, and compatibility classifications.

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

### **Migration Scanner Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/scan` | POST | Scan a supported source environment and return a deterministic migration inventory artifact |

Request body:

| Field | Required | Notes |
|----------|--------|---------|
| `sourceKind` | Yes | Accepted aliases: `geoserver`, `geoserver-rest`, `geoservices`, `arcgis-geoservices-rest`. The response normalizes this to `geoserver-rest` or `arcgis-geoservices-rest`. |
| `sourceUrl` | Yes | Canonical source URL to scan. GeoServices requires an HTTPS ArcGIS service root ending in `FeatureServer` or `MapServer`; layer or table URLs are rejected. GeoServer and GeoServices reject embedded credentials. GeoServices also rejects private, loopback, or unresolvable addresses. GeoServer follows the same HTTPS and address-safety rules in normal environments; test-only unsafe local URLs can be enabled separately. |
| `username` | No | GeoServer basic-auth username. Both `username` and `password` are required before the scan sends Basic auth; if only one is supplied the scan proceeds anonymously and records a note. Ignored for GeoServices scans. |
| `password` | No | GeoServer basic-auth password. Both `username` and `password` are required before the scan sends Basic auth; if only one is supplied the scan proceeds anonymously and records a note. Ignored for GeoServices scans. |
| `timeoutSeconds` | No | Defaults to `120` for GeoServer scans and `30` for GeoServices scans. |
| `includeStyleContent` | No | GeoServer-only. Fetches SLD documents for deeper classification and external graphic detection. Raw style documents are not returned in the artifact. |

GeoServer example:

```json
{
  "sourceKind": "geoserver",
  "sourceUrl": "https://example.com/geoserver/rest",
  "username": "admin",
  "password": "geoserver",
  "includeStyleContent": true
}
```

GeoServices example:

```json
{
  "sourceKind": "geoservices",
  "sourceUrl": "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
  "timeoutSeconds": 10
}
```

Successful response contract:

| Field | Notes |
|----------|--------|
| `artifactKind` | Stable artifact identifier: `honua.migration.source-inventory`. |
| `artifactVersion` | Current schema version: `1.0`. |
| `sourceKind` | Canonical source kind: `geoserver-rest` or `arcgis-geoservices-rest`. |
| `source` | Source identity, product, version, build, and service type metadata. |
| `authPosture` | Observed authentication mode (`anonymous`, `basic`, `auth-required`, `anonymous-or-auth-required`, or `unknown`), whether usable credentials were supplied, whether access was confirmed, and any auth notes. |
| `scanCompleteness` | Scan status (`complete`, `partial`, or `failed`) plus warnings and missing artifact categories. |
| `summary` | Aggregate counts for containers, resources, styles, dependencies, and compatibility tallies. |
| `overallCompatibility` | Roll-up compatibility level (`compatible`, `partial`, `incompatible`) with warnings and manual follow-up steps. |
| `containers` | Deterministically ordered workspaces or services. |
| `resources` | Deterministically ordered layers, tables, or layer groups. |
| `styles` | Deterministically ordered GeoServer styles or GeoServices renderers. |
| `externalDependencies` | Deterministically ordered `datastore`, `coverage-store`, `attachments`, `external-graphic`, or `external-symbol` references with secret-safe addresses for external URLs. |

Artifact item details:

| Section | Key fields | Notes |
|----------|--------|---------|
| `containers[*]` | `id`, `kind`, `name`, `title`, `description`, `isDefault`, `compatibility` | `id` stays stable across display-title changes. `kind` is typically `workspace` or `service`. |
| `resources[*]` | `containerId`, `kind`, `geometryType`, `featureCount`, `hasAttachments`, `capabilities`, `spatialReferences`, `styleIds`, `externalDependencyIds`, `compatibility` | `hasAttachments` is omitted when the source does not report attachment state rather than being coerced to `false`. |
| `styles[*]` | `kind`, `format`, `resourceIds`, `externalDependencyIds`, `metadata`, `compatibility` | `kind` is `style` for GeoServer and `renderer` for GeoServices. `metadata` carries deterministic planning details, not raw style documents. |
| `externalDependencies[*]` | `resourceId`, `kind`, `dependencyType`, `address`, `metadata`, `spatialReferences`, `compatibility` | `resourceId` can point at a layer/table or the owning style/renderer. External addresses are sanitized and secret-like metadata values are redacted. |
| `spatialReferences[*]` | `role`, `sourceValue`, `srid`, `crsUri`, `datum`, `unit`, `axisOrder`, `isGeographic` | Entries are emitted only when the scanner has meaningful CRS data to report. |

The response body is the artifact itself, not a `success/data` admin envelope.

Behavior notes:
- `200 OK` means Honua produced an inventory artifact. Use `scanCompleteness.status` and `overallCompatibility.level` as the planning gate before import or cutover decisions.
- A `200 OK` artifact can still report `scanCompleteness.status = "failed"`. GeoServer uses that path for reachability, timeout, auth, and other discovery failures. GeoServices also uses it when anonymous discovery is blocked or the ArcGIS API returns a source-reported discovery error.
- Failed GeoServer artifacts keep `authPosture.mode = "basic"` when both credentials were supplied; otherwise they use `anonymous-or-auth-required` and record failure details in `authPosture.notes`, `scanCompleteness.warnings`, and `overallCompatibility.manualSteps`.
- GeoServer scans only send Basic auth when both `username` and `password` are present. Supplying only one credential field leaves the scan in anonymous mode and adds an explanatory auth note.
- Sensitive connection metadata is redacted before serialization. Password-, token-, API-key-, and secret-like values are returned as `[redacted]`.
- External URL dependencies strip embedded credentials, query strings, and fragments before serialization, and the corresponding dependency IDs use stable hashed fingerprints instead of raw URLs.
- GeoServer `includeStyleContent=true` deepens classification and dependency discovery only. The artifact still returns metadata, compatibility, and external dependency references rather than raw SLD payloads.
- GeoServices scans currently classify anonymous discovery only. `username` and `password` are accepted by the request model for contract stability but are not used by the GeoServices scanner. Successful GeoServices artifacts therefore report `authPosture.mode = "anonymous"`, while failed artifacts can report `auth-required` or `unknown`.
- GeoServer can emit a synthetic `workspace:global` container when global styles or layer groups are discovered.
- Stable artifact IDs are keyed from canonical source names rather than display text, so changing a source description does not churn container, resource, style, or dependency identifiers. Arrays and compatibility note collections are normalized for repeatable output so unchanged sources produce materially stable planning artifacts. Nullable scalar properties are omitted when the scanner has no value to emit.

Failure semantics:
- `400 Bad Request`: invalid JSON body, missing required fields, unsupported `sourceKind`, non-positive `timeoutSeconds`, invalid HTTPS requirements, invalid GeoServices service-root paths, embedded credentials in the URL, or disallowed private or loopback targets.
- `200 OK` with `scanCompleteness.status = "failed"`: the scanner produced an artifact but could not complete discovery cleanly. GeoServer uses this for auth-required, reachability, timeout, or unusable-metadata cases. GeoServices uses it for auth-required and source-reported ArcGIS discovery errors.
- `502 Bad Gateway`: the scanner failed to connect to the source service. This is surfaced primarily for GeoServices; GeoServer transport failures are normalized into a failed artifact with HTTP `200`.
- `504 Gateway Timeout`: the scanner exceeded the request timeout. This is surfaced primarily for GeoServices; GeoServer timeout failures are normalized into a failed artifact with HTTP `200`.

The artifact includes:
- source identity and version
- auth posture and scan completeness
- workspaces or services, layers or tables, styles or renderers, and external dependencies
- CRS, datum, and unit details needed for migration planning
- per-resource and overall compatibility assessments, warnings, and manual follow-up steps

### **GeoServices Import Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/geoservices/discover` | POST | Discover layers from an ArcGIS service URL |
| `/api/v1/admin/import/geoservices/start` | POST | Start ArcGIS layer import job |
| `/api/v1/admin/import/geoservices/jobs` | GET | List active GeoServices import jobs |
| `/api/v1/admin/import/geoservices/jobs/{jobId}` | GET | Get GeoServices import job status |
| `/api/v1/admin/import/geoservices/jobs/{jobId}/cancel` | POST | Cancel GeoServices import job |

### **Raster Import Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/raster` | POST | Import a raster file (GeoTIFF, PNG world-file, JPEG world-file) into PostGIS |
| `/api/v1/admin/import/raster/formats` | GET | List supported raster file formats and extensions |

Raster import accepts multipart form-data with a primary raster file and optional sidecar files (`.pgw`/`.jgw`/`.tfw`/`.wld` for georeferencing, `.prj` for CRS). GeoTIFF files contain embedded georeferencing; PNG and JPEG formats require a world file. An explicit `srid` field can override CRS detection.

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

Supported `operationType` values: `Upload`, `Import`, `Ingest`, `ExternalImport`,
`TileCache`, `PMTilesArchive`, `Export`, `RasterImport`, `Print`, `Geoprocessing`.

Geoprocessing operations report workflow-specific progress including the current
deterministic stage and plan step counts. Cancellation is supported through the
cancel endpoint. Operations that have already reached a terminal state
(`Completed` or `Failed`) return `409 Conflict`; already-cancelled operations
return `200` idempotently. The server re-reads progress before writing the
cancellation and checks the durable job store (when present) to mitigate TOCTOU
races with worker-owned state transitions.

Jobs submitted through the durable job orchestration substrate (via
`ProcessService.SubmitPlanJob`) surface through these same operations endpoints
using the `Geoprocessing` operation type. The substrate tracks additional
claim, heartbeat, and retry state internally through `IExecutionJobStore`;
structured execution logs are stored via `IExecutionLogStore` and are not yet
exposed through a public API endpoint. See
[Operations — Job Orchestration](operations.md#job-orchestration) for
lifecycle and tuning details.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/performance/database/query-cache/statistics` | GET | Query cache performance statistics |
| `/api/v1/admin/observability/errors` | GET | Recent in-memory error buffer |
| `/api/v1/admin/observability/telemetry` | GET | Tracing/OTLP telemetry status |

### **Deploy Control Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/deploy/preflight` | GET | Get instance-local deploy preflight and upgrade-readiness state |
| `/api/v1/admin/deploy/plan` | POST | Plan a deploy operation |
| `/api/v1/admin/deploy/operations` | POST | Create a deploy operation |
| `/api/v1/admin/deploy/operations/{operationId}` | GET | Get deploy operation status |
| `/api/v1/admin/deploy/operations/{operationId}/submit` | POST | Submit a deploy operation for execution |
| `/api/v1/admin/deploy/operations/{operationId}/rollback` | POST | Rollback a deploy operation |

### **Alert Management Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/alerts/zones` | GET | List alert zones |
| `/api/v1/admin/alerts/zones` | POST | Create alert zone |
| `/api/v1/admin/alerts/zones/{zoneId}` | PUT | Update alert zone |
| `/api/v1/admin/alerts/zones/{zoneId}` | DELETE | Delete alert zone |
| `/api/v1/admin/alerts/rules` | GET | List alert rules |
| `/api/v1/admin/alerts/rules` | POST | Create alert rule |
| `/api/v1/admin/alerts/rules/{ruleId}` | PUT | Update alert rule |
| `/api/v1/admin/alerts/rules/{ruleId}` | DELETE | Delete alert rule |

### **License Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/license` | GET | Get license status |
| `/api/v1/admin/license` | POST | Upload license |
| `/api/v1/admin/license/entitlements` | GET | Get entitlements |

### **Role Management Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/roles` | GET | List roles |
| `/api/v1/admin/roles` | POST | Create role |
| `/api/v1/admin/roles/{id}` | GET | Get role |
| `/api/v1/admin/roles/{id}` | PUT | Update role |
| `/api/v1/admin/roles/{id}` | DELETE | Delete role |
| `/api/v1/admin/roles/{id}/permissions` | GET | Get role permissions |
| `/api/v1/admin/roles/{id}/permissions` | PUT | Set role permissions |

### **User Management Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/users` | GET | List users |
| `/api/v1/admin/users/{id}` | GET | Get user |
| `/api/v1/admin/users/{id}/roles` | PUT | Update user roles |
| `/api/v1/admin/users/{id}` | DELETE | Deprovision user |
| `/api/v1/admin/users/{id}/effective-permissions` | GET | Get user effective permissions |

### **OIDC Provider Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/oidc/providers` | GET | List OIDC providers |
| `/api/v1/admin/oidc/providers` | POST | Create OIDC provider |
| `/api/v1/admin/oidc/providers/{id}` | GET | Get OIDC provider |
| `/api/v1/admin/oidc/providers/{id}` | PUT | Update OIDC provider |
| `/api/v1/admin/oidc/providers/{id}` | DELETE | Delete OIDC provider |
| `/api/v1/admin/oidc/providers/{id}/test` | POST | Test OIDC provider connection |

### **Manifest Approval Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/manifest/pending` | GET | List pending manifest changes awaiting approval |
| `/api/v1/admin/manifest/pending/{id}` | GET | Get details of a pending manifest change |
| `/api/v1/admin/manifest/pending/{id}/approve` | POST | Approve a pending manifest change |
| `/api/v1/admin/manifest/pending/{id}/reject` | POST | Reject a pending manifest change |
| `/api/v1/admin/manifest/pending/history` | GET | List historical approval decisions |

### **GitOps Watch Endpoints**

Git repository watching is an **enterprise edition** feature. When enabled, the server polls a git repository for manifest changes and applies them automatically or queues them for approval.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/gitops/watch` | POST | Configure a new git repository watch |
| `/api/v1/admin/gitops/watch` | PUT | Update existing watch configuration |
| `/api/v1/admin/gitops/watch` | GET | Get current watch configuration |
| `/api/v1/admin/gitops/watch` | DELETE | Remove watch configuration |
| `/api/v1/admin/gitops/changes` | GET | List change history from watched repository (`?limit=&offset=`) |
| `/api/v1/admin/gitops/changes/{id}` | GET | Get details of a specific change record |
| `/api/v1/admin/gitops/changes/{id}/diff` | GET | Get manifest diff (before/after) for a change |

**Watch configuration fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `repositoryUrl` | string | — | Git repository URL (HTTPS, SSH, or git protocol) |
| `branch` | string | `"main"` | Branch to watch |
| `manifestPath` | string | `"manifests/"` | Relative path for manifest files |
| `pollIntervalSeconds` | int | `60` | Poll interval (floored to server minimum) |
| `approvalRequired` | bool | `false` | Queue changes for approval instead of auto-applying |
| `pruneEnabled` | bool | `false` | Delete server resources absent from the repository manifest |
| `enabled` | bool | `true` | Whether the watch is active |
| `configuredBy` | string? | — | Identity of the configuring actor |

**Change record statuses:** `applied`, `pending_approval`, `failed`, `skipped`.

When `approvalRequired` is `true`, detected changes create a pending approval record (see Manifest Approval Endpoints) and the change record includes a `pendingApprovalId` reference.

### **Manifest Drift Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/manifest/drift` | GET | Get manifest drift report |
| `/api/v1/admin/manifest/versions` | GET | List manifest versions |
| `/api/v1/admin/manifest/versions/{versionId}` | GET | Get manifest version detail |

### **Feature Change Events Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/feature-events/replay` | GET | Replay feature change events |

### **Tile Operations Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/tile-operations/jobs` | POST | Start tile operation job |
| `/api/v1/admin/tile-operations/jobs` | GET | List tile operation jobs |
| `/api/v1/admin/tile-operations/jobs/{jobId}` | GET | Get tile operation job status |
| `/api/v1/admin/tile-operations/jobs/{jobId}/cancel` | POST | Cancel tile operation job |
| `/api/v1/admin/tile-operations/jobs/{jobId}/retry` | POST | Retry tile operation job |

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
- [Upgrade and Rollback Runbook](../devops/runbooks/UPGRADE_AND_ROLLBACK.md) — deploy backend configuration for Azure Functions, Azure Container Apps, and Kubernetes
