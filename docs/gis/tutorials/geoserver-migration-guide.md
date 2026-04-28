# GeoServer to Honua Migration Guide

Migrate from GeoServer to Honua Server, covering endpoint equivalence, inventory scanning, dry-run import, and key configuration differences.

## Overview

Honua provides GeoServer migration tooling for discovery, compatibility classification, and dry-run import validation. The migration scanner is discovery-only: it returns a deterministic planning artifact before any connection, service, layer, or style changes are applied. GeoServer import remains a separate dry-run workflow, and style conversion still requires manual follow-up. After migration, clients that consumed GeoServer WFS/WMS/WMTS endpoints can connect to Honua's equivalent OGC and GeoServices REST endpoints.

## Endpoint Equivalence Mapping

### Feature Data Access

| GeoServer Endpoint | Honua Equivalent | Notes |
|---|---|---|
| `GET /geoserver/wfs?service=WFS&request=GetFeature` | `GET /wfs?service=WFS&request=GetFeature` | WFS 2.0, 1.1.0, and 1.0.0 compatible (read-only) |
| `GET /geoserver/ows?service=WFS&request=GetCapabilities` | `GET /wfs?service=WFS&request=GetCapabilities&version={2.0.0\|1.1.0\|1.0.0}` | Capabilities for any supported WFS version |
| GeoServer REST `/workspaces/{ws}/datastores/{ds}/featuretypes` | `GET /ogc/features/collections` | OGC API Features discovery |
| GeoServer WFS `GetFeature` with CQL filter | `GET /ogc/features/collections/{id}/items?filter=...` | CQL2 filtering |
| GeoServer WFS `GetFeature` with bbox | `GET /ogc/features/collections/{id}/items?bbox=...` | Spatial filtering |
| GeoServer WFS `GetPropertyValue` | `GET /ogc/features/collections/{id}/items?properties=...` | Property selection |

### Map Rendering

| GeoServer Endpoint | Honua Equivalent | Notes |
|---|---|---|
| `GET /geoserver/wms?service=WMS&request=GetMap` | `GET /rest/services/{id}/MapServer/export` | Dynamic map rendering |
| `GET /geoserver/wms?request=GetMap` | `GET /ogc/services/{id}/wms` | WMS 1.3.0 and 1.1.1 compatible (read-only). Use the matching `VERSION=`; 1.1.1 expects `SRS`/`X`/`Y` and lon/lat `EPSG:4326` BBOX, 1.3.0 expects `CRS`/`I`/`J` and lat/lon `EPSG:4326` BBOX. |
| `GET /geoserver/gwc/service/wmts` | `GET /rest/services/{id}/MapServer/WMTS` | WMTS tile access |
| `GET /geoserver/gwc/service/wmts` | `GET /ogc/services/{id}/wmts` | OGC WMTS endpoint |
| GeoServer tile layer | `GET /tiles/{layerId}/{z}/{x}/{y}.mvt` | Vector tiles (MapLibre-ready) |
| GeoServer legend graphic | `GET /rest/services/{id}/MapServer/legend` | Layer legend |

### Metadata and Discovery

| GeoServer Endpoint | Honua Equivalent | Notes |
|---|---|---|
| `GET /geoserver/rest/about/version` | `GET /api/v1/admin/version` | Server version |
| `GET /geoserver/rest/workspaces` | `GET /api/v1/admin/services` | Service listing |
| `GET /geoserver/rest/layers` | `GET /ogc/features/collections` | Layer discovery |
| `GET /geoserver/rest/styles` | `GET /api/v1/admin/metadata/layers/{id}/style` | Layer styles (MapLibre JSON) |

## Discovery And Dry-Run Import

Honua provides admin API endpoints for GeoServer discovery and a dry-run import workflow. Use the scanner artifact as the review contract for migration planning; the import endpoint currently validates and previews the work without applying configuration changes.

### Step 1: Scan And Classify Your GeoServer

Assess your GeoServer instance before importing. The unified migration scanner returns a deterministic planning artifact for review and can be used consistently across GeoServer REST and ArcGIS GeoServices REST sources.

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/scan \
  -H "Content-Type: application/json" \
  -d '{
    "sourceKind": "geoserver",
    "sourceUrl": "https://geoserver-host/geoserver/rest",
    "username": "admin",
    "password": "geoserver",
    "includeStyleContent": true
  }'
```

Contract notes:
- `sourceKind: "geoserver"` is normalized to `sourceKind: "geoserver-rest"` in the response artifact.
- `includeStyleContent: true` fetches SLD documents for deeper compatibility analysis and external graphic detection, but the artifact does not echo raw SLD bodies.
- GeoServer basic auth is only used when both `username` and `password` are supplied. Providing only one field falls back to anonymous discovery and records a note in `authPosture.notes`.
- `timeoutSeconds` is optional for GeoServer scans and defaults to `120`.
- The response body is the artifact itself, not a `success/data` admin envelope.
- HTTP `200` only means the scanner returned an artifact. Review `scanCompleteness.status` and `overallCompatibility.level` before treating the source as ready for migration planning. Failed GeoServer artifacts can report `authPosture.mode = "basic"` when both credentials were supplied, or `anonymous-or-auth-required` when discovery ran without full credentials.

The response includes:
- Stable artifact fields: `artifactKind = "honua.migration.source-inventory"` and `artifactVersion = "1.0"`
- Source identity and reported version
- Authentication posture and scan completeness
- Workspace and layer inventory
- Synthetic `workspace:global` container entries when GeoServer exposes global styles or layer groups
- Datastore and coverage-store types with sanitized connection metadata and secret-safe addresses
- Style formats, deterministic `styles[*].metadata`, and compatibility assessment
- CRS, datum, and unit details for migration planning
- External dependencies with sanitized addresses, stable cross-links (`styleIds`, `resourceIds`, `resourceId`), and manual follow-up steps

Review the inventory artifact before proceeding. Start with `summary`, `scanCompleteness`, and `overallCompatibility`, then drill into per-item blockers using the stable IDs shared across `resources`, `styles`, and `externalDependencies`. Arrays are deterministically ordered for repeatable diffs, sensitive datastore values are redacted before serialization, and nullable scalar fields are omitted when the scanner has no value to emit. Layers backed by PostGIS data stores have the highest migration fidelity.

### Step 2: Start a Dry-Run Import

> **Note:** The GeoServer import endpoint currently supports **dry-run mode only**. A dry run validates connectivity, discovers resources, and reports what would be imported without making changes.

```bash
export GEOSERVER_PASSWORD='geoserver'

curl -X POST http://localhost:8080/api/v1/admin/import/geoserver/start \
  -H "Content-Type: application/json" \
  -d '{
    "geoServerRestUrl": "https://geoserver-host/geoserver/rest",
    "username": "admin",
    "passwordSecretReference": "env:GEOSERVER_PASSWORD",
    "dryRun": true
  }'
```

Queued GeoServer imports no longer accept plaintext credentials because the request is persisted in distributed job state before the worker runs. Use a secret reference such as `env:GEOSERVER_PASSWORD` for the GeoServer password and `honuaApiKeySecretReference` when a future non-dry-run workflow needs a Honua API key.

This returns a job ID for tracking progress.

> **Unified scanner note:** the same `POST /api/v1/admin/import/scan` endpoint also accepts `sourceKind: "geoservices"` or `sourceKind: "arcgis-geoservices-rest"` for ArcGIS GeoServices REST inventory scans. Use an HTTPS ArcGIS service root ending in `FeatureServer` or `MapServer`, not a layer or table URL. GeoServices discovery currently uses anonymous access only, normalizes `sourceKind` to `arcgis-geoservices-rest`, and emits the same top-level artifact sections, with renderers surfacing in `styles[]` and external symbol URLs surfacing as sanitized `externalDependencies[]` entries. Failed GeoServices artifacts can report `authPosture.mode = "auth-required"` or `"unknown"` when discovery is blocked or the ArcGIS API reports an error; transport failures and request timeouts still surface as `502` or `504`.

### Step 3: Monitor Progress

```bash
# Check specific job status
curl http://localhost:8080/api/v1/admin/import/geoserver/jobs/{jobId}

# List all active import jobs
curl http://localhost:8080/api/v1/admin/import/geoserver/jobs
```

### Step 4: Cancel (if needed)

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/geoserver/jobs/{jobId}/cancel
```

## Configuration Differences

### Security

| GeoServer | Honua |
|---|---|
| Spring Security with role-based access | API key authentication + OIDC |
| `.properties` file-based user management | Admin API user/role management |
| Per-workspace security rules | Per-service access policies |
| GeoFence integration | Built-in RBAC with edition gating |

Honua supports API key authentication for programmatic access and OIDC providers (Azure AD, Google, Okta, Auth0, generic) for interactive users. Configure OIDC via the admin API or environment variables.

### Coordinate Reference Systems

| GeoServer | Honua |
|---|---|
| EPSG database bundled with GeoTools | PostGIS `spatial_ref_sys` table |
| `SRS handling` policy per layer | CRS negotiation per request |
| Native SRS + declared SRS per feature type | OGC API Features CRS parameter |
| Reprojection via GeoTools | Reprojection via PostGIS `ST_Transform` |

Honua stores data in the source CRS and reprojects on request using PostGIS. Ensure your PostGIS instance has the required SRID definitions in `spatial_ref_sys`.

### Layer Publishing

| GeoServer | Honua |
|---|---|
| Workspace > DataStore > FeatureType | Connection > Service > Layer |
| Manual layer configuration in web admin | Admin API + Admin UI (Blazor) |
| SLD/CSS styling | MapLibre GL Style JSON |
| Layer groups | Service-level layer aggregation |

In Honua, you register a database connection, then publish layers from discovered tables. The Admin UI provides a visual workflow, or use the Admin API:

```bash
# List tables from a connection
curl http://localhost:8080/api/v1/admin/connections/{connectionId}/tables

# Publish a layer (one request per layer)
curl -X POST http://localhost:8080/api/v1/admin/connections/{connectionId}/layers \
  -H "Content-Type: application/json" \
  -d '{ "schema": "public", "table": "parcels", "layerName": "parcels", "serviceName": "my-service" }'
```

### Tile Caching

| GeoServer (GeoWebCache) | Honua |
|---|---|
| Integrated GWC with seeding UI | Output caching with optional Redis |
| Disk-based tile cache | In-memory + Redis distributed cache |
| Per-layer gridset configuration | Automatic TileJSON + OGC Tiles tiling schemes |
| Manual seed/truncate operations | Tile operation jobs via Admin API |

Honua uses output caching (in-memory or Redis) rather than a dedicated tile cache. For high-throughput tile serving, enable Redis:

```
HONUA_REDIS_URL=redis:6379
```

### Styling

GeoServer uses SLD (Styled Layer Descriptor) or CSS styling. Honua uses MapLibre GL Style JSON. The scanner inventories style metadata, compatibility warnings, and external graphic references, but the current GeoServer import flow does not convert or apply styles. Recreate target styles through the Admin API after data import, using the scanner artifact and its manual follow-up steps as the migration checklist.

Manage styles via the Admin API:

```bash
# Get current style
curl http://localhost:8080/api/v1/admin/metadata/layers/{layerId}/style

# Update style
curl -X PUT http://localhost:8080/api/v1/admin/metadata/layers/{layerId}/style \
  -H "Content-Type: application/json" \
  -d '{ "version": 8, "layers": [...] }'
```

## Client Migration Checklist

After migrating server configuration, update client applications:

- [ ] **WFS clients**: Point to `http://honua-host:8080/wfs` (WFS 2.0, 1.1.0, or 1.0.0 read-only) or migrate to `http://honua-host:8080/ogc/features` (OGC API Features). Pin `VERSION=` for clients that cannot negotiate.
- [ ] **WMS clients**: Point to `http://honua-host:8080/rest/services/{id}/MapServer/WMS` or `http://honua-host:8080/ogc/services/{id}/wms` (WMS 1.3.0 or 1.1.1; legacy clients pinned to 1.1.1 connect without changes)
- [ ] **WMTS clients**: Point to `http://honua-host:8080/rest/services/{id}/MapServer/WMTS` or `http://honua-host:8080/ogc/services/{id}/wmts`
- [ ] **REST API consumers**: Map GeoServer REST paths to Honua Admin API equivalents (see table above)
- [ ] **Authentication**: Replace GeoServer credentials with Honua API keys or OIDC tokens
- [ ] **Styles**: Recreate target styles from the scanner artifact and adjust MapLibre JSON as needed
- [ ] **CRS configuration**: Verify required SRIDs exist in PostGIS `spatial_ref_sys`
- [ ] **Tile consumers**: Update tile URLs to Honua vector tile or WMTS endpoints

## GeoServer vs Honua Protocol Support

| Protocol | GeoServer | Honua |
|---|---|---|
| WFS 2.0 | Native | Supported |
| WFS 1.1.0 | Native | Supported (read-only; CITE Basic evidence pending) |
| WFS 1.0.0 | Native | Supported (read-only; CITE Basic evidence pending) |
| WMS 1.3 | Native | Via MapServer |
| WMS 1.1.1 | Native | Via MapServer (read-only; CITE Basic evidence pending) |
| WMTS 1.0 | Via GeoWebCache | Via MapServer |
| OGC API Features | Plugin (community) | Native |
| OGC API Tiles | Plugin (community) | Native |
| OGC API Maps | Not supported | Native |
| GeoServices REST (FeatureServer) | Not supported | Native |
| GeoServices REST (MapServer) | Not supported | Native |
| OData v4 | Not supported | Native |
| Vector Tiles (MVT) | Via GeoWebCache | Native |
| gRPC | Not supported | Native |

## Next Steps

- Explore the [Interactive API Explorer](http://localhost:8080/docs) to test migrated endpoints
- Review the [API Examples](../../developer/API_EXAMPLES.md) for protocol-specific request patterns
- See the [Protocols Overview](../STANDARDS_APIS.md) for choosing the right protocol per client
- Check the [Admin API Reference](../../operator/CONTROL_PLANE_API.md) for connection and layer management
