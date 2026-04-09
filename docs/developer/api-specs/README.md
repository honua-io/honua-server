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

> **Contract note**: the versioned OGC Features snapshot includes Honua's spatial analytics extension paths plus the shared `SpatialAnalyticsFeatureCollection` / `SpatialAnalyticsMetadata` response schema used by those operations. Analytics responses are always `application/geo+json` in WGS 84, and the handlers also accept `application/x-www-form-urlencoded` at runtime via the shared POST-body parser even though `application/json` remains the canonical request content type in the generated OpenAPI.

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

## Server Management API

**Protocol**: REST API
**Base URL**: `/api/v1/admin`
**OpenAPI Spec**: [admin-api.json](admin-api.json)
**Runtime OpenAPI Endpoint**: `https://your-honua-server.com/api/v1/admin/openapi.json`
**Authentication**: API Key, OIDC bearer token, or optional HTTP Basic compatibility mode

> **Note**: The runtime admin OpenAPI endpoint serves this bundled `admin-api.json` contract snapshot.
> Use the [Server Management API guide](../../operator/CONTROL_PLANE_API.md) and `/api/v1/admin/config` for operational guidance.
>
> **Migration scanner note**: `POST /api/v1/admin/import/scan` returns the inventory artifact itself, not the usual admin envelope, and a `200` response can still carry `scanCompleteness.status = "failed"`.
> Request aliases normalize `sourceKind` to `geoserver-rest` or `arcgis-geoservices-rest`, and dependency addresses plus secret-like metadata are sanitized for planning-safe export.
> Stable IDs cross-link `resources[*].styleIds`, `styles[*].resourceIds`, and `externalDependencies[*].resourceId` so review tooling can traverse the artifact without guessing by display name.
>
> **Control-plane direction**: this API is intended to back a Honua-managed control plane. Honua is not positioning Flux or Argo CD as the primary rollout controller.

**What you can do**:
- Manage database connections (create, test, list)
- Publish and configure layers from database tables
- Control layer enabling/disabling and protocol settings
- Manage map styles and layer styling
- Scan GeoServer REST and ArcGIS GeoServices REST FeatureServer/MapServer service roots into deterministic migration inventory artifacts with compatibility rollups
- Monitor system health and observability
- Access recent errors and telemetry status
- Inspect deploy preflight and upgrade-readiness state per Honua instance

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
./scripts/validate-openapi-contracts.sh
./scripts/generate-control-plane-sdks.sh
```

Control-plane SDK governance and contract diff checks:
- `.github/workflows/openapi-contract-governance.yml`
- `.github/workflows/control-plane-sdk-governance.yml`

## Related Documentation

- [**Geospatial Data APIs**](../../gis/STANDARDS_APIS.md) - Protocol overview and selection guide
- [**Server Management API**](../../operator/CONTROL_PLANE_API.md) - Admin API guide and key workflows
- [**Control Plane Versioning Policy**](../CONTROL_PLANE_VERSIONING_POLICY.md) - Breaking-change and deprecation lifecycle
- [**Control Plane Migration Guide**](../CONTROL_PLANE_MIGRATION_GUIDE.md) - SDK quickstart and upgrade steps
- [**API Examples**](../API_EXAMPLES.md) - Code examples for all protocols
- [**Integration Patterns**](../INTEGRATION_PATTERNS.md) - Common integration approaches

---
*Interactive API documentation powered by OpenAPI 3.0 specifications versioned with the Honua Server codebase.*
