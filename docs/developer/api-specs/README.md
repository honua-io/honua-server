# Interactive API Documentation

Honua Server provides OpenAPI specifications for OGC APIs and a curated, versioned Admin API contract snapshot. These interactive docs allow you to explore and test the APIs directly.

## Available API Specifications

### **OGC API Features**
**Protocol**: OGC API Features Parts 1-3
**Base URL**: `/ogc/features`
**OpenAPI Spec**: [ogc-api-features.json](ogc-api-features.json)
**Runtime Spec**: `https://your-honua-server.com/openapi.json`

**What you can do**:
- Browse collections (layers)
- Query features with spatial and attribute filters
- Access individual features
- Perform CRUD operations
- Use advanced CQL2 filtering
- Use the Honua spatial analytics extensions (`/clusters`, `/spatial-join`, `/buffer-aggregate`, `/density`)

> **Contract note**: the versioned OGC Features snapshot includes Honua's POST-only spatial analytics extension paths plus the shared `SpatialAnalyticsFeatureCollection` / `SpatialAnalyticsMetadata` response schema used by those operations. Analytics responses are always `application/geo+json` in WGS 84; per-feature cluster and spatial-join rows keep `properties.objectId` plus nested `properties.attributes`, while aggregate outputs use operation-specific summary fields such as `featureCount`, `cellId`, and optional `weight`. The handlers also accept `application/x-www-form-urlencoded` at runtime via the shared POST-body parser even though `application/json` remains the canonical request content type in the generated OpenAPI.

{% swagger src="ogc-api-features.json" %}
{% endswagger %}

### **OGC API Tiles**
**Protocol**: OGC API Tiles
**Base URL**: `/ogc/tiles`
**OpenAPI Spec**: [ogc-api-tiles.json](ogc-api-tiles.json)
**Runtime Spec**: `https://your-honua-server.com/ogc/tiles/openapi.json`

**What you can do**:
- Access tilesets metadata
- Retrieve vector tiles (MVT)
- Configure tile parameters
- Access tile matrices

{% swagger src="ogc-api-tiles.json" %}
{% endswagger %}

### **OGC API Coverages**
**Protocol**: OGC API Coverages
**Base URL**: `/ogc/coverages`
**OpenAPI Spec**: [ogc-api-coverages.json](ogc-api-coverages.json)
**Runtime Spec**: `https://your-honua-server.com/ogc/coverages/openapi.json`

**What you can do**:
- Discover raster coverage collections
- Inspect collection spatial extent, CRS, grid/domain metadata, and selectable band fields
- Negotiate metadata as JSON or HTML with `f=json|html` or `Accept`
- Retrieve coverage bytes as GeoTIFF by default or PNG by negotiation
- Spatially subset with `bbox` and reproject with `crs`
- Select bands with `properties=band_1,band_3`
- Resize with one of `resolution`, `scale-factor`, or `scale-size`; derived outputs are capped at 8192 pixels per axis
- Follow coverage response `Link` alternates that preserve the request query while switching between GeoTIFF and PNG

See the [OGC API Coverages Coverage](../../gis/specifications/ogc-api-coverages-coverage.md) document for supported parameters and MVP deferrals.

{% swagger src="ogc-api-coverages.json" %}
{% endswagger %}

### **OGC API Processes**
**Protocol**: OGC API Processes Part 1 — Core
**Base URL**: `/ogc/processes`
**Runtime Spec**: `https://your-honua-server.com/ogc/processes/openapi.json`

**What you can do**:
- Discover available processes (`GET /processes`)
- Describe a process and its JSON Schema inputs/outputs (`GET /processes/{processId}`)
- Submit async execution with `Prefer: respond-async` (`POST /processes/{processId}/execution`)
- List, poll, and dismiss jobs (`GET /jobs`, `GET /jobs/{jobId}`, `DELETE /jobs/{jobId}`)
- Retrieve results when available (`GET /jobs/{jobId}/results`)

> **V1 notes**: Async-only (sync returns `501`). Single canonical process (`honua-geoprocessing`). Job lifecycle and execution routes require Redis-backed durable storage (`503` when unavailable). Results endpoint returns `200 OK` with a document-mode JSON body on success — empty `{}` until the canonical process declares value-typed outputs and the execution engine populates result storage. See the [OGC API Processes Coverage](../../gis/specifications/ogc-api-processes-coverage.md) for conformance classes, endpoint details, and V1 limitations.

---

## Runtime Capability Manifest

**Protocol**: Honua runtime capability discovery
**Endpoint**: `/api/v1/capabilities/manifest`
**Authentication**: Optional; anonymous callers receive the public/default tenant view
**Response Types**: `application/json`, `application/vnd.honua.capability-manifest+json`

Use the capability manifest when Console, MCP, QGIS plugins, native hosts, or SDK clients need to decide whether package families, temporal features, offline sync, realtime streams, jobs, GitOps, native transports, gRPC, mTLS, uploads, edits, or analysis controls should be enabled for the current request scope. The response is generated per request, is marked `no-store`, and is informational only; operation endpoints remain the source of truth for authorization. See [Capability Manifest](../capability-manifest.md) for the field contract and stable capability ids.

## Server Management API

**Protocol**: REST API
**Base URL**: `/api/v1/admin`
**OpenAPI Spec**: [admin-api.json](admin-api.json)
**Runtime OpenAPI Endpoint**: `https://your-honua-server.com/api/v1/admin/openapi.json`
**Authentication**: API Key, OIDC bearer token, or optional HTTP Basic compatibility mode

> **Note**: The runtime admin OpenAPI endpoint serves this bundled `admin-api.json` contract snapshot.
> Use the [Server Management API guide](../../operator/CONTROL_PLANE_API.md) and `/api/v1/admin/config` for operational guidance.
> The saved-query and analysis-package content surface lives beside the admin
> API under `/api/v1/analysis/**`; its current markdown contract is
> [Analysis Content](../../admin-api/analysis-content.md) until it is promoted
> into the generated control-plane OpenAPI snapshot.
>
> **Migration scanner note**: `POST /api/v1/admin/import/scan` returns the inventory artifact itself, not the usual admin envelope, and a `200` response can still carry `scanCompleteness.status = "failed"`.
> Request aliases normalize `sourceKind` to `geoserver-rest` or `arcgis-geoservices-rest`, and dependency addresses plus secret-like metadata are sanitized for planning-safe export.
> Stable IDs cross-link `resources[*].styleIds`, `styles[*].resourceIds`, and `externalDependencies[*].resourceId` so review tooling can traverse the artifact without guessing by display name.
>
> **Control-plane direction**: this API is intended to back a Honua-managed control plane. Honua is not positioning Flux or Argo CD as the primary rollout controller.
>
> **Sibling control-plane surfaces**: Console (`/api/v1/console/**`) and Studio
> (`/api/v1/studio/**`) require the same admin authorization posture but are not
> part of this `/api/v1/admin` OpenAPI snapshot. The Studio package lifecycle
> contract is maintained in
> [Studio Package Lifecycle API](../../admin-api/studio-package-lifecycle.md)
> until a dedicated Studio OpenAPI document is published.
>
> **Runtime capability discovery**: `GET /api/v1/capabilities/manifest` is a
> public, request-scoped discovery contract outside the admin OpenAPI snapshot.
> It accepts optional `environment` and `workspaceId` hints and returns package,
> transport, policy, entitlement, and limit state for Console, SDK, MCP, QGIS,
> and native-host clients. See [Capability Manifest](../capability-manifest.md).

**What you can do**:
- Manage database connections (create, test, list)
- Publish and configure layers from database tables
- Control layer enabling/disabling and protocol settings
- Manage map styles and layer styling
- Create Metadata v2 release packages and prevalidate release compatibility against target environments before GitOps promotion
- Scan GeoServer REST and ArcGIS GeoServices REST FeatureServer/MapServer service roots into deterministic migration inventory artifacts with compatibility rollups
- Monitor system health and observability
- Inspect durable background jobs, structured logs, artifacts, control actions, and Operate event correlations
- Access recent errors and telemetry status
- Inspect deploy preflight and upgrade-readiness state per Honua instance
- Manage geofence alert zones, realtime alert rules, draft validation, enable/disable state, and delivery health for Console Operate workflows
- Inspect runtime license status, upload signed license files when enabled, and read the active feature/entitlement inventory
- Save and reopen analysis content under `/api/v1/analysis/**` using the
  markdown contract while the OpenAPI snapshot catches up

{% swagger src="admin-api.json" %}
{% endswagger %}

## GeoServices REST Services

**Protocol**: Esri-compatible REST API
**Base URL**: `/rest/services`
**Compatibility**: Esri-compatible subset (see coverage matrices for exact operation support)

> **Note**: FeatureServer, MapServer, and ImageServer endpoints follow Esri REST conventions and provide service-specific self-describing metadata. Geometry Service currently exposes operation endpoints only; Honua does not implement the root GeometryServer metadata resource.
>
> For detailed endpoint reference, start with the [GeoServices REST Parity landing page](../../gis/geoservices-rest-parity.md), use the [machine-readable parity JSON](../../gis/data/geoservices-rest-parity.json) when tooling needs the same contract, and then drill into the [FeatureServer](../../gis/feature-server-matrix.md), [MapServer](../../gis/map-server-matrix.md), [ImageServer](../../gis/image-server-matrix.md), and [Geometry Service](../../gis/geometry-service-matrix.md) matrices.

## Vector Tiles (MVT)

**Protocol**: Mapbox Vector Tiles
**Base URL**: `/tiles`
**Format**: TileJSON metadata + MVT tiles

> **Note**: Vector tile endpoints provide TileJSON metadata for client configuration.
>
> For usage examples, see the [API Examples Guide](../API_EXAMPLES.md#vector-tiles-mvt).

## FieldCollection Mobile Sync API

**Protocol**: REST API
**Base URL**: `/api/v1/fieldcollection`
**Authentication**: API Key (`X-API-Key`)

> **Note**: The FieldCollection mobile sync endpoints (`generation`,
> `sync-cursor` GET/POST, `changes` GET/POST) back the `honua-mobile`
> offline sync clients. The pull endpoint is a pure read; the per-client
> cursor is advanced only by an explicit `POST /sync-cursor` after local
> persistence succeeds. The contract is documented in
> [FieldCollection Mobile Sync API](../fieldcollection-mobile-sync-api.md) and
> registered in the [public interface proof](../../gis/data/public-interface-proof.json)
> under the `fieldcollection-mobile-sync` surface.

## Form Package API

**Protocol**: REST API
**Base URLs**: `/api/v1/admin/forms/packages`, `/api/v1/forms/packages`
**Authentication**: Admin route authorization for the shipped slice; submissions also require target layer data-editor authorization.

> **Note**: The Forms contract covers versioned package drafts, validation,
> immutable publish, reopened drafts, published-package readback, offline-policy
> discovery, and idempotent JSON-compatible or multipart submissions with attachment outcomes. The
> bundled `admin-api.json` snapshot does not yet include the Forms routes or schemas, so the
> response and usage contract is documented in
> [Form Package API](../form-package-api.md). Offline-policy responses point to
> the existing FeatureServer replica and FieldCollection mobile sync routes
> instead of defining a separate form-only sync protocol.

## Testing the APIs

### **Using the Interactive Docs**
1. **Try it out**: Use the "Try it out" buttons in the specs above
2. **Authentication**: Add API keys or tokens as needed
3. **Live server**: Point to your running Honua instance

### **Getting API Specs at Runtime**
```bash
# OGC API Features
curl https://your-honua-server.com/openapi.json

# OGC API Tiles
curl https://your-honua-server.com/ogc/tiles/openapi.json

# OGC API Coverages
curl https://your-honua-server.com/ogc/coverages/openapi.json

# OGC API Processes
curl https://your-honua-server.com/ogc/processes/openapi.json

# Server Management API
curl https://your-honua-server.com/api/v1/admin/openapi.json
```

### **Generate SDK Clients**
```bash
# Generate Python client from OGC API Features
openapi-generator generate \
  -i https://your-honua-server.com/openapi.json \
  -g python \
  -o ./honua-python-client

# Generate control-plane SDK artifacts (TypeScript + Python + .NET)
./scripts/ci/validate-openapi-contracts.sh
./scripts/sdk/generate-control-plane-sdks.sh
```

Control-plane SDK governance and contract diff checks:
- `.github/workflows/openapi-contract-governance.yml`
- `.github/workflows/control-plane-sdk-governance.yml`

## Related Documentation

- [**Geospatial Data APIs**](../../gis/STANDARDS_APIS.md) - Protocol overview and selection guide
- [**Server Management API**](../../operator/CONTROL_PLANE_API.md) - Admin API guide and key workflows
- [**Console Job Observability**](../../admin-api/console-job-observability.md) - Durable job viewer contract for Console and admin integrations
- [**Analysis Content**](../../admin-api/analysis-content.md) - Saved-query and analysis-package versions, preview artifacts, runs/reruns, artifact bindings, and safe failed-job diagnostics
- [**Control Plane Versioning Policy**](../CONTROL_PLANE_VERSIONING_POLICY.md) - Breaking-change and deprecation lifecycle
- [**Control Plane Migration Guide**](../CONTROL_PLANE_MIGRATION_GUIDE.md) - SDK quickstart and upgrade steps
- [**API Examples**](../API_EXAMPLES.md) - Code examples for the major shipped protocols
- [**Integration Patterns**](../INTEGRATION_PATTERNS.md) - Common integration approaches

---
*Interactive API documentation powered by OpenAPI 3.0 specifications versioned with the Honua Server codebase.*
