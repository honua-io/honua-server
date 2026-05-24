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
|-- security/client-certificates/ # Native/admin mTLS trust profiles, mappings, revocations, validation
|-- feature-events/           # Feature change event replay
|-- manifest/drift            # Manifest drift, versions
|-- manifest/pending/         # Manifest approval workflows
|-- gitops/                   # Git repository watching and change history
|-- tile-operations/          # Tile operation jobs
|-- compliance/               # SOC 2 / FedRAMP readiness dashboard, residency policy + dry-run, key-version posture rotation, report export
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
./scripts/ci/validate-openapi-contracts.sh
./scripts/sdk/generate-control-plane-sdks.sh
```

- CI contract governance and SDK generation:
- `.github/workflows/openapi-contract-governance.yml`
- `.github/workflows/control-plane-sdk-governance.yml`

- Versioning/deprecation policy:
- [Control Plane Versioning Policy](../developer/CONTROL_PLANE_VERSIONING_POLICY.md)

- Migration and upgrade guidance:
- [Control Plane Migration Guide](../developer/CONTROL_PLANE_MIGRATION_GUIDE.md)

- AI/agent integration:
- [MCP Server](../developer/MCP_SERVER.md)

---

## **Authentication Behavior**

- Supported admin auth schemes:
- `X-API-Key` (default)
- `Authorization: Bearer <jwt>` when OIDC is enabled
- `Authorization: Basic ...` only when Basic compatibility mode is enabled
- Valid mapped client certificate when `Authentication:ClientCertificates:Mode`
  is `Optional`, `RequiredForAdmin`, or `RequiredForEnvironment`

- Precedence:
- Bearer is evaluated first when OIDC is enabled and a Bearer header is present
- A valid mapped client certificate can authenticate a native/admin principal;
  required mTLS modes validate the certificate before normal RBAC
- Otherwise `X-API-Key` is evaluated
- Basic compatibility is evaluated only when enabled and no valid `X-API-Key` is present

- Challenge headers:
- `WWW-Authenticate: ApiKey ...` is always returned for API-key challenges
- `WWW-Authenticate: Basic ...` is added when Basic compatibility mode is enabled
- Bearer failures include standard Bearer challenge headers
- Required mTLS failures return `application/problem+json` with stable
  `client_certificate_*` codes instead of raw TLS or provider errors

### **Client Certificate Trust Management**

Native/admin mTLS policy is configured under
`Authentication:ClientCertificates` and administered through the security
control-plane surface. The Admin API manages public trust metadata only; private
keys stay in operator/client certificate stores.

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/auth/config` | GET | No API-key/OIDC bootstrap metadata, including non-secret mTLS mode, environment, issuer hints, and required surfaces. Required mTLS prefix enforcement can still apply. |
| `/api/v1/admin/security/client-certificates/profiles` | GET/POST | List or create trust profiles |
| `/api/v1/admin/security/client-certificates/profiles/{profileId}` | GET/PUT/DELETE | Read, update, or disable a trust profile |
| `/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings` | GET/POST | List or create principal mappings |
| `/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}` | PUT/DELETE | Update or disable a principal mapping |
| `/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations` | GET/POST | List or add revocation entries |
| `/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{revocationId}` | DELETE | Remove a revocation entry |
| `/api/v1/admin/security/client-certificates/validate` | POST | Validate a PEM, URL-encoded PEM, or base64 DER public client certificate without storing it |

For configuration examples and the full response contract, see
[Client Certificate Authentication](client-certificate-authentication.md).

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
| `/api/v1/admin/services/{serviceName}/layers/{layerId}/metadata` | PUT | Patch layer-level access policy, time info, and raster mosaic defaults |

Layer metadata updates accept `rasterMosaic.mergeStrategy` for ImageServer mosaic defaults. Allowed values are `newest`, `oldest`, `average`, `max`, and `min` (case-insensitive); stored values are normalized to lowercase. An empty string clears the layer default, a missing or `null` field preserves the existing value, and unknown values return `400 Bad Request`.

---

## **Data Import (Minimal Example)**

```http
POST /api/v1/admin/import/upload
Content-Type: multipart/form-data

file=@parcels.geojson
```

FlatGeobuf (`.fgb`) files can be uploaded directly — no archive wrapping needed. If the `.fgb` file does not embed CRS in its header, provide `sourceSrid` on the import request; the server rejects imports when it cannot detect the source coordinate system.

For Esri File Geodatabases, use a `.gdb.zip` archive that contains the `.gdb` directory and preserves the directory structure inside the archive. See [FileGDB Import Workflow](../gis/FILEGDB_IMPORT_WORKFLOW.md).

For GeoParquet files, upload a `.parquet` or `.geoparquet` file directly. The server reads GeoParquet `geo` metadata for CRS detection and requires WKB geometry encoding. Non-WKB encodings are rejected. Nested column types (Struct, List, Map) are skipped with warnings. Rows with null geometry are skipped during both preview and import, and reported as warnings in the import response. Files with more than 100,000 rows in a single Parquet row group are rejected to maintain bounded memory usage; re-export such files with smaller row groups.

For source-system migration planning, use the unified scan endpoint before starting any GeoServer or GeoServices import job. The scan response is a deterministic inventory artifact that records source identity and version, authentication posture, scan completeness, containers, resources, styles or renderers, external dependencies, spatial reference details, and compatibility classifications.

For ArcGIS GeoServices sources that require authentication, send credentials in the request `credentials` object rather than in the service URL. Synchronous discovery and scan requests may carry a plaintext token/password or a secret reference. Queued GeoServices import jobs must use `accessTokenSecretReference` or `passwordSecretReference`; plaintext `accessToken` and `password` values are rejected so job state does not persist secrets.

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
| `/api/v1/admin/import/scan?export=json` | POST | Same scan, returned as an indented JSON attachment for committing to a migration project repository |

The ArcGIS slice of the unified scanner is documented end-to-end in
[ArcGIS Inventory Discovery](arcgis-inventory-discovery.md), including the
deterministic compatibility code namespace and the field metadata surfaced
on each resource.

Request body:

| Field | Required | Notes |
|----------|--------|---------|
| `sourceKind` | Yes | Accepted aliases: `geoserver`, `geoserver-rest`, `geoservices`, `arcgis-geoservices-rest`. The response normalizes this to `geoserver-rest` or `arcgis-geoservices-rest`. |
| `sourceUrl` | Yes | Canonical source URL to scan. GeoServices requires an HTTPS ArcGIS service root ending in `FeatureServer` or `MapServer`; layer or table URLs are rejected. GeoServer and GeoServices reject embedded credentials. GeoServices also rejects private, loopback, or unresolvable addresses. GeoServer follows the same HTTPS and address-safety rules in normal environments; test-only unsafe local URLs can be enabled separately. |
| `username` | No | GeoServer basic-auth username. Both `username` and `password` are required before the scan sends Basic auth; if only one is supplied the scan proceeds anonymously and records a note. Ignored for GeoServices scans. |
| `password` | No | GeoServer basic-auth password. Both `username` and `password` are required before the scan sends Basic auth; if only one is supplied the scan proceeds anonymously and records a note. Ignored for GeoServices scans. |
| `credentials` | No | GeoServices-only credential descriptor. Supported modes are `token`, `oauth`, and `basic`. Discovery and scan requests may use inline `accessToken`/`password` values or secret references; queued imports must use `accessTokenSecretReference` or `passwordSecretReference`. Secret values and references are not echoed into inventory artifacts. |
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

Credentialed GeoServices scan example:

```json
{
  "sourceKind": "geoservices",
  "sourceUrl": "https://example.com/arcgis/rest/services/Private/FeatureServer",
  "credentials": {
    "mode": "token",
    "accessTokenSecretReference": "env:ARCGIS_PRIVATE_TOKEN"
  }
}
```

Successful response contract:

| Field | Notes |
|----------|--------|
| `artifactKind` | Stable artifact identifier: `honua.migration.source-inventory`. |
| `artifactVersion` | Current schema version: `1.0`. |
| `sourceKind` | Canonical source kind: `geoserver-rest` or `arcgis-geoservices-rest`. |
| `source` | Source identity, product, version, build, and service type metadata. |
| `authPosture` | Observed authentication mode (`anonymous`, `token`, `basic`, `oauth`, `auth-required`, `denied`, `expired-token`, `anonymous-or-auth-required`, or `unknown`), whether usable credentials were supplied, whether access was confirmed, and any auth notes. |
| `scanCompleteness` | Scan status (`complete`, `partial`, or `failed`) plus warnings and missing artifact categories. |
| `summary` | Aggregate counts for containers, resources, styles, dependencies, and compatibility tallies. |
| `overallCompatibility` | Roll-up compatibility level (`compatible`, `partial`, `incompatible`) with warnings and manual follow-up steps. |
| `containers` | Deterministically ordered workspaces or services. |
| `resources` | Deterministically ordered layers, tables, or layer groups. |
| `styles` | Deterministically ordered GeoServer styles or GeoServices renderers. |
| `externalDependencies` | Deterministically ordered `datastore`, `coverage-store`, `attachments`, `external-graphic`, or `external-symbol` references with secret-safe addresses for external URLs. |

Compatibility assessments may include a stable, machine-readable `code` (for example `COMPATIBLE`, `MANUAL_REVIEW`, `ARCGIS_UNSUPPORTED_RENDERER`, `ARCGIS_TOKEN_REQUIRED`) alongside `level`, `reason`, `warnings`, and `manualSteps` when the scanner can assign one deterministically. The ArcGIS GeoServices code namespace and remediation table are documented in [ArcGIS Inventory Discovery — Compatibility Codes](arcgis-inventory-discovery.md#compatibility-codes); `code` is omitted for aggregate assessments where no single code applies and for source-specific assessments that do not yet define a stable code.

Artifact item details:

| Section | Key fields | Notes |
|----------|--------|---------|
| `containers[*]` | `id`, `kind`, `name`, `title`, `description`, `isDefault`, `compatibility` | `id` stays stable across display-title changes. `kind` is typically `workspace` or `service`. |
| `resources[*]` | `containerId`, `kind`, `geometryType`, `featureCount`, `hasAttachments`, `capabilities`, `spatialReferences`, `fields`, `styleIds`, `externalDependencyIds`, `compatibility` | `hasAttachments` is omitted when the source does not report attachment state rather than being coerced to `false`. `fields` carries source schema entries for resources that advertise one. |
| `resources[*].fields[*]` | `name`, `alias`, `fieldType`, `nullable`, `domainType`, `domainName`, `domainValues` | `fieldType` is the source-provided token (e.g. ArcGIS `esriFieldType*`). `nullable` is omitted from the artifact when the source does not advertise the property (treat absence as "unknown" rather than `false`). `domainValues` is bounded; coded-value domains exceeding the cap surface as the `ARCGIS_DOMAIN_TRUNCATED` warning rather than silent truncation. |
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
- GeoServices scans ignore the top-level GeoServer `username`/`password` fields and use the GeoServices `credentials` descriptor instead. Successful credentialed scans report `authPosture.mode` as `token`, `basic`, or `oauth`; anonymous scans report `anonymous`. ArcGIS auth failures are deterministic: missing credentials report `auth-required`, invalid or expired ArcGIS tokens report `expired-token`, and forbidden identities report `denied`. The scanner does not refresh tokens; rotate the referenced secret and rerun the scan or queued import.
- The `?export=json` query parameter on the scan endpoint returns the artifact as an indented JSON attachment with `Content-Disposition: attachment; filename="<service-slug>-inventory.json"` and `X-Content-Type-Options: nosniff`. The slug is derived from the source `displayName`, sanitized to alphanumeric, dash, and underscore characters, and capped at 64 characters; credentials supplied in the request body are never echoed into the artifact.
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

### **GeoServer Import Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/import/geoserver/discover` | POST | Discover GeoServer REST configuration and migration compatibility |
| `/api/v1/admin/import/geoserver/start` | POST | Queue a GeoServer dry-run validation or bounded apply job |
| `/api/v1/admin/import/geoserver/jobs` | GET | List active GeoServer import jobs |
| `/api/v1/admin/import/geoserver/jobs/{jobId}` | GET | Get GeoServer import job status |
| `/api/v1/admin/import/geoserver/jobs/{jobId}/cancel` | POST | Cancel a GeoServer import job |

`POST /api/v1/admin/import/geoserver/start` accepts `dryRun=true` for validation
and `dryRun=false` for the first non-dry-run apply slice. Non-dry-run jobs emit
`honua.migration.apply-plan` and `honua.migration.apply-execution` artifacts in
the completed progress/result payload. The plan artifact contains a stable
`replayToken`/`planFingerprint`, ordered `steps`, `manualReviewItems`, and
`unsupportedItems`. The execution artifact records per-step outcomes. Current
catalog mutation is limited to idempotent publication of PostGIS-backed layers
whose source tables already exist in the target Honua database; data copy, layer
groups, service exposure changes, and bulk style persistence remain
manual-review or unsupported records.

Queued GeoServer jobs persist request state before a worker runs, so secret
values must use secret references such as
`"passwordSecretReference": "env:GEOSERVER_PASSWORD"`. Plaintext passwords and
Honua API keys are rejected for queued jobs.

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

Raster import accepts multipart form-data with a primary raster file and optional sidecar files (`.pgw`/`.jgw`/`.tfw`/`.wld` for georeferencing, `.prj` for CRS). GeoTIFF files contain embedded georeferencing; PNG and JPEG formats require a world file. An explicit `srid` field can override CRS detection. Optional `acquisitionDate` stores a per-raster timestamp used by ImageServer and OGC temporal mosaic selection, and `tileZoomLevels` controls which cache levels are pre-generated.

Per-layer mosaic homogeneity is enforced at import: subsequent uploads to a layer must share the SRID and band count of the layer's first raster. Mismatched uploads return `400 Bad Request` with a structured message (`Layer {id} requires raster homogeneity for mosaic compositing. Expected SRID=…, BandCount=…; upload has SRID=…, BandCount=…`) and the transaction is rolled back before commit.

---

## **Layer Style (Minimal Example)**

```http
PUT /api/v1/admin/metadata/layers/42/style
Content-Type: application/json

{
  "mapLibreStyle": {
    "version": 8,
    "sources": {},
    "layers": [
      {
        "id": "parcels-fill",
        "type": "fill",
        "source": "layer-42",
        "source-layer": "layer",
        "paint": {
          "fill-color": "#2D69A5",
          "fill-opacity": 0.4,
          "fill-outline-color": "#1A4775"
        }
      }
    ]
  },
  "changedBy": "ops@example.com",
  "changeSummary": "Initial style for the parcels layer"
}
```

The MapLibre payload must be a valid Style Spec v8 document with at least one layer; missing or empty `layers` arrays are rejected with `400`. Any layer that omits `source` defaults to the auto-injected Honua tile source `layer-{layerId}` (source-layer `layer`).

The successful response carries the persisted style plus revision metadata, the GeoServices `drawingInfo` snapshot the server back-generated from the MapLibre input (so MapServer / FeatureServer legend and renderer endpoints stay in sync), and any symbolizers that the engine could not losslessly translate:

```json
{
  "success": true,
  "data": {
    "mapLibreStyle": {
      "version": 8,
      "sources": { "layer-42": { "type": "vector", "tiles": ["/tiles/42/{z}/{x}/{y}.mvt"], "minzoom": 0, "maxzoom": 22 } },
      "layers": [
        {
          "id": "parcels-fill",
          "type": "fill",
          "source": "layer-42",
          "source-layer": "layer",
          "paint": { "fill-color": "#2D69A5", "fill-opacity": 0.4, "fill-outline-color": "#1A4775" }
        }
      ]
    },
    "drawingInfo": {
      "renderer": {
        "type": "simple",
        "symbol": { "type": "esriSFS", "style": "esriSFSSolid", "color": [45, 105, 165, 102], "outline": { "type": "esriSLS", "style": "esriSLSSolid", "color": [26, 71, 117, 255], "width": 1 } }
      }
    },
    "styleVersion": 4,
    "revisedAt": "2026-05-01T17:42:11Z",
    "revisedBy": "ops@example.com",
    "changeSummary": "Initial style for the parcels layer",
    "unsupportedSymbolizers": []
  }
}
```

`changeSummary` is operator-supplied free text capped at 1000 characters; longer values return `400`. `revisedAt` is set to the server's UTC clock on every canonical write. `unsupportedSymbolizers[]` is populated when a `drawingInfo` payload contains renderer or symbol types outside the supported set; each entry has stable `code` (`RENDERER_TYPE_UNSUPPORTED`, `SYMBOL_TYPE_UNSUPPORTED`, `PICTURE_MARKER_PARTIAL`, `RENDERER_PAYLOAD_INCOMPLETE`), `symbolizerType`, and operator `guidance` fields. The request still succeeds and the engine returns a best-effort MapLibre fallback so style intent is never silently dropped.

The public `GET /api/styles/{layerId}.json` endpoint accepts an optional `?theme=default|dark|colorblind-safe|print` query parameter for deterministic theme transforms. The output cache varies by `theme`; the admin update endpoint invalidates every variant on each revision. See [Style Engine: Cross-Protocol Consumption](../gis/style-engine-protocol-consumption.md) for the full contract and how MVT, MapServer, and WMS consume the canonical document.

### **Metadata and Style Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/version` | GET | Get current control-plane and metadata schema version info |
| `/api/v1/admin/capabilities` | GET | Get admin metadata capabilities and the SDK compatibility contract |
| `/api/v1/admin/manifest` | GET | Export metadata manifest |
| `/api/v1/admin/manifest/apply` | POST | Apply metadata manifest (supports dry-run/prune controls) |
| `/api/v1/admin/metadata/resources` | GET | List metadata resources |
| `/api/v1/admin/metadata/resources` | POST | Create metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | GET | Get metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | PUT | Update metadata resource |
| `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` | DELETE | Delete metadata resource |
| `/api/v1/admin/metadata/layers/{layerId}/style` | GET | Get layer style payload (MapLibre + cached drawingInfo + revision metadata) |
| `/api/v1/admin/metadata/layers/{layerId}/style` | PUT | Update layer style payload; accepts optional `changedBy` and `changeSummary` for revision tracking and reports `unsupportedSymbolizers[]` with stable codes |
| `/api/v1/admin/metadata/layers/{layerId}/style/import-sld` | POST | Convert an SLD/SE 1.0 or 1.1 XML document to MapLibre style JSON and store it (admin only, Community edition; 1 MiB body cap). See [SLD Migration Reference](sld-migration.md). |
| `/api/v1/admin/metadata/layers/{layerId}/style/export-sld` | GET | Export the stored MapLibre style as an `application/xml` SLD 1.0 document. Diagnostic count surfaces in the `X-Sld-Diagnostic-Count` response header. |
| `/api/styles/{layerId}.json` | GET | Public MapLibre style fetch with optional `?theme=default\|dark\|colorblind-safe\|print` deterministic transform; output cache varies per theme. |

`Group` and `SourceDescriptor` are stable `honua.io/v1alpha1` metadata resource kinds on the generic CRUD surface. SDKs can list them with `GET /api/v1/admin/metadata/resources?kind=Group` and `GET /api/v1/admin/metadata/resources?kind=SourceDescriptor` instead of probing undocumented catalog endpoints.

`Group` resources use `metadata.name` and `metadata.namespace` as the stable group identity. `spec.description` is optional and should contain the human-readable group summary when present.

`SourceDescriptor` resources must store the SDK descriptor in `spec.sourceDescriptor`. The descriptor object must include non-empty `id` and `protocol` strings aligned with `Honua.Sdk.Abstractions.Features.SourceDescriptor`; optional `locator`, `capabilities`, `schema`, and `attribution` fields follow the SDK shape.

### **SDK Compatibility Handshake**

SDKs should call `GET /api/v1/admin/capabilities` once per authenticated session and cache the `data.compatibility` object.

- `controlPlaneApi.major`: reject unsupported majors without guessing path shape.
- `metadataSchemas`: prefer non-deprecated metadata schema versions when sending resource documents.
- `features`: branch on coarse capabilities such as manifest support instead of probing extra endpoints.
- `serverVersion` and `releaseChannel`: log or surface for diagnostics, rollout targeting, and support.

Focused guidance and a concrete JSON example:
- [SDK Compatibility Metadata](../developer/SDK_COMPATIBILITY_METADATA.md)

### **Operations and Monitoring Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/operations/{operationId}` | GET | Get operation progress/status |
| `/api/v1/admin/operations/{operationId}/cancel` | POST | Cancel operation |
| `/api/v1/admin/operations/active` | GET | List active operations |
| `/api/v1/admin/operations/type/{operationType}` | GET | List operations by type |
| `/api/v1/admin/jobs` | GET | List durable execution jobs with cursor pagination and queue/status/resource filters |
| `/api/v1/admin/jobs/{jobId}` | GET | Get durable execution job detail |
| `/api/v1/admin/jobs/{jobId}/logs` | GET | Page structured execution logs |
| `/api/v1/admin/jobs/{jobId}/artifacts` | GET | Page artifact references with availability state |
| `/api/v1/admin/jobs/{jobId}/actions` | GET | List available job control actions |
| `/api/v1/admin/jobs/{jobId}/cancel` | POST | Cancel a queued, provisioning, or running job |
| `/api/v1/admin/jobs/{jobId}/retry` | POST | Retry a failed or cancelled job when policy allows |

Supported `operationType` values: `Upload`, `Import`, `Ingest`, `ExternalImport`,
`TileCache`, `PMTilesArchive`, `PMTilesPublish`, `Export`, `RasterImport`, `Print`,
`Geoprocessing`, `Publishing`, `Orchestration`.

Geoprocessing operations report workflow-specific progress including the current
deterministic stage and plan step counts. Cancellation is supported through the
cancel endpoint. Operations that have already reached a terminal state
(`Completed` or `Failed`) return `409 Conflict`; already-cancelled operations
return `200` idempotently. The server re-reads progress before writing the
cancellation and checks the durable job store (when present) to mitigate TOCTOU
races with worker-owned state transitions.

Jobs submitted through the durable job orchestration substrate (via
`ProcessService.SubmitPlanJob` or OGC API Processes `/execute`) surface
through these same operations endpoints using the
`Geoprocessing` operation type. The execution-job reconciler bridges
progress from pluggable batch-compute backends into `IUniversalProgressStore`
so all jobs — local and remote — appear through the operations surface.
The substrate tracks additional claim, heartbeat, and retry state internally
through `IExecutionJobStore`. Console job endpoints expose the same durable
records for historical query, detail, logs, artifacts, actions, cancellation,
and retry. Structured execution logs are stored via `IExecutionLogStore` and
are available through `GET /api/v1/admin/jobs/{jobId}/logs` with cursor
pagination.
See [Operations — Job Orchestration](operations.md#job-orchestration) for
lifecycle and tuning details.

Workflow runs produced by the declarative orchestration layer surface through
the same endpoints using the `Orchestration` operation type. The progress
payload is a `WorkflowProgress` record keyed by the workflow run identifier
(`wf-<guid>`) and reports `runStatus`, `workflowId`, `stepsCompleted`, and
`totalSteps`. Cancellation on an `Orchestration` operation is routed to the
`WorkflowOrchestrationEngine`, which records the cancellation on the durable
workflow run. The next reconcile tick cascades `CancelJobAsync` to any queued
or running child jobs; callers should poll the same endpoint for the terminal
status. When the engine is not registered (e.g., Redis-less deployments) the
endpoint returns `503`. See
[Operations — Workflow Orchestration](operations.md#workflow-orchestration)
for run lifecycle, scheduler semantics, and tuning details.

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
| `/api/v1/admin/alerts/zones/{zoneId}` | GET | Read alert zone |
| `/api/v1/admin/alerts/zones/{zoneId}` | PUT | Update alert zone |
| `/api/v1/admin/alerts/zones/{zoneId}` | DELETE | Delete alert zone |
| `/api/v1/admin/alerts/rules` | GET | List alert rules |
| `/api/v1/admin/alerts/rules` | POST | Create alert rule |
| `/api/v1/admin/alerts/rules/test` | POST | Validate a draft alert rule and delivery-channel bindings without persisting it |
| `/api/v1/admin/alerts/rules/{ruleId}` | GET | Read alert rule |
| `/api/v1/admin/alerts/rules/{ruleId}` | PUT | Update alert rule |
| `/api/v1/admin/alerts/rules/{ruleId}/enabled` | PUT | Enable or disable alert rule |
| `/api/v1/admin/alerts/rules/{ruleId}/health` | GET | Inspect rule evaluation state, active incidents, recent triggers, delivery failures, and dead-letter state |
| `/api/v1/admin/alerts/rules/{ruleId}` | DELETE | Delete alert rule |

Alert management endpoints are admin-only and return the standard
`ApiResponse<T>` envelope. Successful create, update, enable/disable, test,
health, and delete operations currently return HTTP `200`. Persisted
create/update/enable validation failures return `400`; the draft test endpoint
returns `200` with validation details for invalid drafts. Missing rule/zone
identifiers return `404`, and unauthenticated or non-admin callers return
`401`/`403`.

Zone list requests accept `?serviceId=`. Zone create/update payloads use
camel-case JSON:

```json
{
  "serviceId": "harbor-ops",
  "zoneName": "Honolulu Harbor",
  "wkt": "POLYGON((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29))",
  "srid": 4326,
  "metadata": { "owner": "operations" },
  "isActive": true
}
```

`wkt` is optional for placeholder zones; when supplied it must be `Polygon` or
`MultiPolygon` WKT. `srid` defaults to `4326`. Polygon inputs are normalized to
`MULTIPOLYGON` WKT in responses. Zone responses include `zoneId`, `serviceId`,
`zoneName`, `wkt`, `srid`, `metadata`, and `isActive`.

Rule list requests accept `?serviceId=&layerId=`. Rule create/update payloads
use this contract:

```json
{
  "serviceId": "harbor-ops",
  "layerId": 1,
  "zoneId": 12,
  "ruleName": "Harbor Entry",
  "triggerType": "enter",
  "conditionsJson": "{}",
  "cooldownSeconds": 60,
  "severity": "warning",
  "editionRequired": "pro",
  "channels": ["webhook"],
  "isActive": true
}
```

`triggerType` accepts `enter`, `exit`, `dwell`, or `threshold`; `severity`
accepts `info`, `warning`, or `critical`; `editionRequired` accepts `pro` or
`enterprise`; `cooldownSeconds` must be zero or greater. Persisted geofence
triggers (`enter`, `exit`, `dwell`) require `zoneId`. `zoneId` is only valid for
those geofence triggers; threshold rules must omit it. `dwell` conditions must
be a JSON object with positive `dwellSeconds`. `threshold` conditions must be a
JSON object with non-empty `field`, an `operator` of `>`, `>=`, `<`, `<=`, `==`,
or `!=`, and a numeric `value`.

Delivery channel names are canonical snake-case tokens: `webhook`,
`websocket`, `email`, `digest`, `aws_sns`, `azure_eventgrid`, `slack`,
`microsoft_teams`, `aws_sqs`, and `azure_eventhub`. Some compatibility aliases
parse on write (`teams`, `awssns`, `awssqs`, `azureeventgrid`,
`azureeventhub`), but responses always use canonical names. `channels` may be
omitted or empty for rules that should not dispatch to external sinks. Pro
alerting allows `enter`/`exit` rules and `webhook`; Enterprise allows all
implemented trigger types and channels when the channel is configured.

`POST /api/v1/admin/alerts/rules/test` validates a draft without persisting it:

```json
{
  "rule": {
    "serviceId": "harbor-ops",
    "layerId": 1,
    "ruleName": "Draft Threshold",
    "triggerType": "threshold",
    "conditionsJson": "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
    "cooldownSeconds": 30,
    "severity": "warning",
    "editionRequired": "enterprise",
    "channels": ["webhook"],
    "isActive": true
  },
  "zone": null
}
```

The test endpoint always uses the success envelope for draft validation results;
invalid drafts return `200` with `data.isValid=false`, `errors`, `warnings`,
per-channel `deliveryChannels`, and `evaluatedAt`. Delivery validation and rule
health statuses are `configured`, `unconfigured`, `disabled`, and
`unauthorized`; rule health can additionally report `rate_limited` or `failing`
for channels with recent delivery errors. Delivery channel `lastError` values are
sanitized summaries, such as `Delivery rate limited.` or `Delivery failed.`;
raw provider exception text is not returned. The optional draft `zone` is only
accepted for `enter`, `exit`, and `dwell` rules, must use the same `serviceId`
as the rule, and is used for geometry validation without persistence. If both a
draft `zone` and `zoneId` are supplied on a geofence draft, validation uses the
draft zone geometry and returns a warning.

`GET /api/v1/admin/alerts/rules/{ruleId}/health` returns the rule state snapshot:
`lastEvaluatedAt`, `lastTriggeredAt`, `activeIncidentCount`,
`recentTriggerCount`, `coolingDownFeatureCount`, `nextCooldownExpiresAt`,
`deliveryFailureCount`, `deadLetterCount`, `linkedEventIds`,
`deliveryChannels`, and up to 10 `recentTriggers`. Recent trigger summaries use
`resourceRef` values of the form `alert/{eventId}` so Console Operate can link
to the normalized alert event. `activeIncidentCount` is derived from the current
evaluator state, not from historical alert events: `enter` and `dwell` rules
count state rows where the feature is currently inside the zone, `threshold`
rules count state rows where the threshold is currently breached, and `exit`
transition events do not keep an active incident open after the feature has
left. `recentTriggerCount` counts alert events from the previous 24 hours.
Alert zone/rule changes write config-change audit events with actions
`alert_zone.create`, `alert_zone.update`,
`alert_zone.delete`, `alert_rule.create`, `alert_rule.update`,
`alert_rule.enable`, `alert_rule.disable`, and `alert_rule.delete`.

### **License Endpoints**

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/admin/license` | GET | Get active license status. |
| `/api/v1/admin/license` | POST | Upload raw signed license bytes when `Licensing:AllowAdminUpload=true` and `Licensing:LicensePath` is configured. Disabled by default. |
| `/api/v1/admin/license/entitlements` | GET | Get the active/inactive entitlement inventory as a flat list. |
| `/api/v1/admin/license/status` | GET | Platform-admin license status (same status contract as `GET /api/v1/admin/license`). |
| `/api/v1/admin/license/features` | GET | Platform-admin feature entitlement view with catalog category and minimum-edition metadata. |
| `/api/v1/admin/license/upload` | POST | Platform-admin upload alias. Uses the same validator and upload settings as `POST /api/v1/admin/license`, but returns a compact upload-result response. |

Runtime licensing loads an offline Ed25519-signed JSON envelope from
`Licensing:LicensePath`. `Licensing:TrustedKeys:<keyId>` supplies trusted
raw Ed25519 public keys as `base64url:<32-byte-key>`, unprefixed Base64URL, or
`base64:<32-byte-key>`. With no configured path the server runs in Community
mode; missing, malformed, unknown-key, invalid-signature, and expired files
leave the server in a safe Community state and emit structured licensing
diagnostics. License files are bounded to 64 KiB. The license status also
appears in `/healthz/metrics` and `/monitoring/health/production` payloads.

The ticket #338 runtime envelope is:

```json
{
  "version": 1,
  "keyId": "honua-2026-q2",
  "payload": "<base64url-encoded UTF-8 JSON payload bytes>",
  "signature": "<base64url Ed25519 signature over the payload bytes>"
}
```

The decoded payload uses camel-case JSON:

```json
{
  "schema": "honua.license/v1",
  "licenseId": "lic_123",
  "licensedTo": "Example Operator",
  "edition": "Pro",
  "issuedAt": "2026-05-06T00:00:00Z",
  "expiresAt": "2027-05-06T00:00:00Z",
  "entitlements": ["analytics.clustering"],
  "metadata": {
    "source": "byol"
  }
}
```

`schema`, `licenseId`, `licensedTo`, `edition`, and `issuedAt` are required.
`edition` accepts `Community`, `Pro`, `Enterprise`, and `Professional`
(`Professional` maps to `Pro`). `expiresAt` is optional; when present it must be
in the future. `metadata` is an optional string-valued map. Unknown entitlement
keys are ignored for activation, and the active entitlement set always includes
Community-tier catalog entries. Paid features are active only when their catalog
key is present in the signed `entitlements` array; the `edition` value is the
operator-facing bundle label and does not by itself activate every Pro or
Enterprise feature.

Status responses from `GET /api/v1/admin/license`,
`GET /api/v1/admin/license/status`, and successful
`POST /api/v1/admin/license` calls use `ApiResponse<LicenseStatusResponse>`.
The `entitlements` array is the known catalog inventory with active/inactive
state, not only the keys present in the signed payload:

```json
{
  "success": true,
  "data": {
    "edition": "Pro",
    "expiresAt": "2027-05-06T00:00:00Z",
    "isValid": true,
    "daysUntilExpiry": 365,
    "expiryWarning": false,
    "validationState": "Valid",
    "licensedTo": "Example Operator",
    "licenseId": "lic_123",
    "issuedAt": "2026-05-06T00:00:00Z",
    "entitlements": [
      { "key": "analytics.clustering", "name": "Spatial Clustering", "isActive": true }
    ]
  },
  "timestamp": "2026-05-06T00:00:00Z"
}
```

Validation states are `NoLicenseConfigured`, `Valid`, `MissingFile`,
`Malformed`, `UnknownKey`, `InvalidSignature`, and `Expired`. No configured path
reports `Community` with `isValid=true`; every failed configured-file state
reports `Community` with `isValid=false`.

`POST /api/v1/admin/license/upload` returns `ApiResponse<LicenseUploadResponse>`
instead of the full status response:

```json
{
  "success": true,
  "data": {
    "success": true,
    "message": "License applied."
  },
  "timestamp": "2026-05-06T00:00:00Z"
}
```

When upload is disabled, `Licensing:LicensePath` is unset, the file is empty,
oversized, malformed, unknown-key, invalid-signature, or expired, upload
returns HTTP `400`. `/api/v1/admin/license/upload` includes the rejection
message in `data.message`; `/api/v1/admin/license` includes the rejection
message in the top-level `message`.

`GET /api/v1/admin/license/entitlements` returns
`ApiResponse<IReadOnlyList<EntitlementResponse>>` as a flat catalog inventory:

```json
{
  "success": true,
  "data": [
    { "key": "temporal.filtering", "name": "Temporal Query Filtering", "isActive": true },
    { "key": "analytics.clustering", "name": "Spatial Clustering", "isActive": false }
  ],
  "timestamp": "2026-05-06T00:00:00Z"
}
```

`GET /api/v1/admin/license/features` returns `ApiResponse<LicenseEntitlementsResponse>`:

```json
{
  "success": true,
  "data": {
    "edition": "Community",
    "features": [
      {
        "key": "analytics.clustering",
        "displayName": "Spatial Clustering",
        "category": "Analytics",
        "isEnabled": false,
        "minimumEdition": "Pro",
        "upgradeRequired": true
      }
    ]
  },
  "timestamp": "2026-05-06T00:00:00Z"
}
```

Paid-feature gates return HTTP `402 Payment Required` through the shared
protocol error formatter. Admin/OGC/generic routes use problem details with
title `Payment Required`; GeoServices routes use the standard GeoServices error
envelope with `error.code=402`; gRPC maps the same missing-entitlement condition
to `FAILED_PRECONDITION`.

The broader unified license architecture, BYOL and marketplace issuance flows,
multi-key rotation, and AWS/Azure marketplace adapter contracts are defined in
[ADR-0033](../contributor/adr/0033-unified-license-format.md) and the companion
[unified license and entitlement architecture](../contributor/architecture/unified-license-and-entitlement.md).
Operational procedures live in the licensing runbooks:
[License Migration](runbooks/LICENSE_MIGRATION.md),
[License Key Rotation](runbooks/LICENSE_KEY_ROTATION.md), and
[Marketplace Operations](runbooks/MARKETPLACE_OPERATIONS.md).

The broader licensing architecture also defines routes that land with child
tickets per the ADR-0033 § "Bounded Child Tickets" decomposition. They are
listed here so the canonical route set in the ADR, the architecture doc, and
this contract agree:

| Endpoint | Method | Visibility | Land with |
|----------|--------|------------|-----------|
| `/api/v1/admin/license/upload` | POST | Every instance. Uses the same validator as startup load. Returns `400` when admin upload is disabled or validation fails. | Landed with ticket #338 |
| `/api/v1/admin/license/keys` | GET | Every instance | Key rotation / public-key inspection child ticket |
| `/api/v1/admin/license/mint` | POST | Mint host only — `404` on customer instances | Mint host endpoints child ticket |
| `/api/v1/admin/license/refresh` | POST | Mint host only — `404` on customer instances | Mint host endpoints child ticket |
| `/api/v1/admin/license/signing/status` | GET | Mint host only — `404` on customer instances | Mint host endpoints child ticket |
| `/api/v1/admin/marketplace/aws/reconcile` | POST | When `Aws:Marketplace:Enabled=true` | AWS marketplace adapter child ticket |
| `/api/v1/admin/marketplace/azure/reconcile` | POST | When `Azure:Marketplace:Enabled=true` | Azure marketplace adapter child ticket |
| `/api/v1/marketplace/azure/webhook` | POST | When `Azure:Marketplace:Enabled=true`. Public — Azure AD JWT bearer | Azure marketplace adapter child ticket |
| `/api/v1/marketplace/azure/landing` | GET | When `Azure:Marketplace:Enabled=true`. Public — Microsoft redirects the purchaser's browser here with `?token=<marketplace-token>`; handler calls Microsoft's Resolve API server-to-server (`x-ms-marketplace-token` header). | Azure marketplace adapter child ticket |
| `/api/v1/marketplace/azure/activate` | POST | When `Azure:Marketplace:Enabled=true`. Public — backend POST from the landing page after the purchaser confirms; handler calls Microsoft's Activate API server-to-server. | Azure marketplace adapter child ticket |

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
| `manifestPath` | string | `"manifests/"` | Relative exact manifest file path or directory path. Directory paths resolve to `honua-manifest.json`, then `manifest.json`; slashless paths without a file extension are normalized as directories. Glob patterns are rejected. |
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

Supported `operation` values on the start request: `seed`, `warm`, `invalidate`,
`purge`, `archive`, `publish`. The `publish` operation produces a durable PMTiles
artifact whose descriptor (provider, bucket, object key, content type, size, URL
strategy, browser-usable access URL, MapLibre source hints) is returned on the
job-status response as `publishedArtifact`. See
[Tile Operations Runbook](tile-operations-runbook.md) and the
[PMTiles Publishing](pmtiles-publishing.md) guide for request/response details
and storage configuration.

### **Analysis Report Endpoints**

These routes live alongside the management API at `/api/v1/analysis/...` and
require admin authorization. They render the canonical `AnalysisReport`
envelope and the rendered Markdown / HTML body for a completed geoprocessing
job; the same envelope is mirrored on the MCP resource
`honua://jobs/{jobId}/report` (see [MCP Server](../developer/MCP_SERVER.md)).

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/v1/analysis/reports/{jobId}` | GET | Retrieve the structured `AnalysisReport` JSON envelope. Auth and terminal-state semantics are inherited from the underlying job-results path. |
| `/api/v1/analysis/reports/{jobId}/render?format=md\|html` | GET | Render the report body. `format=md` returns `text/markdown; charset=utf-8`; `format=html` returns `text/html; charset=utf-8` as a self-contained document with inline CSS and inline SVG charts (no external CDN). Unsupported formats return `400 Bad Request`. |

Notes:

- Routes are gated by `Reporting:Enabled` (default `true`). When disabled
  the endpoints (and the paired MCP resource) are not registered.
- Reports are versioned via `reportContractVersion` (`honua.report.v1`).
  Render requests against an unsupported contract version return
  `409 Conflict` with the stable `report.contract.unsupported` error code.
- Rendered bodies are cached server-side keyed by
  `(jobId, contractVersion, format, resultPackageId)` for `Reporting:Cache:TtlMinutes`
  (default `60`, capped at 24 h) so repeat renders for the same package
  do not re-run the renderer.
- Narrative blocks degrade cleanly: when the LLM provider is disabled or
  fails, the response carries `narrativeMode = "deterministic"` or
  `"fallback-from-llm-error"` and the deterministic provider authors the
  prose. HTML output is fully offline (no external script or font
  references) so on-prem operators can serve reports without internet
  egress.
- 401 / 403 / 404 / 409 / 503 responses use `application/problem+json` and
  match the geoprocessing job-service exception taxonomy
  (`unauthenticated`, `permission_denied`, `not_found`, `failed_precondition`,
  `unavailable`).

---

## **Health Checks**

```http
GET /healthz/ready
GET /healthz/live
```

---

## **Related Documentation**

- [Operator Guide](README.md)
- [Geospatial API Examples](../developer/API_EXAMPLES.md)
- [FileGDB Import Workflow](../gis/FILEGDB_IMPORT_WORKFLOW.md)
- [Security](security.md)
- [Control Plane Versioning Policy](../developer/CONTROL_PLANE_VERSIONING_POLICY.md)
- [Control Plane Migration Guide](../developer/CONTROL_PLANE_MIGRATION_GUIDE.md)
- [Upgrade and Rollback Runbook](runbooks/UPGRADE_AND_ROLLBACK.md) — deploy backend configuration for AWS Lambda, Azure Functions, Azure Container Apps, AWS ECS + ALB canary, and Kubernetes
