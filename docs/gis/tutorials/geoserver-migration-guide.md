# GeoServer to Honua Migration Guide

Migrate from GeoServer to Honua Server, covering endpoint equivalence, migration-manifest generation, dry-run import, and key configuration differences.

## Overview

Honua provides built-in GeoServer migration tooling that discovers your existing GeoServer configuration, translates supported resources into a deterministic migration manifest, and supports dry-run validation of the unfinished executor path. After migration, clients that consumed GeoServer WFS/WMS/WMTS endpoints can connect to Honua's equivalent OGC and GeoServices REST endpoints.

## Endpoint Equivalence Mapping

### Feature Data Access

| GeoServer Endpoint | Honua Equivalent | Notes |
|---|---|---|
| `GET /geoserver/wfs?service=WFS&request=GetFeature` | `GET /wfs?service=WFS&request=GetFeature` | WFS 2.0 compatible |
| `GET /geoserver/ows?service=WFS&request=GetCapabilities` | `GET /wfs?service=WFS&request=GetCapabilities` | WFS capabilities |
| GeoServer REST `/workspaces/{ws}/datastores/{ds}/featuretypes` | `GET /ogc/features/collections` | OGC API Features discovery |
| GeoServer WFS `GetFeature` with CQL filter | `GET /ogc/features/collections/{id}/items?filter=...` | CQL2 filtering |
| GeoServer WFS `GetFeature` with bbox | `GET /ogc/features/collections/{id}/items?bbox=...` | Spatial filtering |
| GeoServer WFS `GetPropertyValue` | `GET /ogc/features/collections/{id}/items?properties=...` | Property selection |

### Map Rendering

| GeoServer Endpoint | Honua Equivalent | Notes |
|---|---|---|
| `GET /geoserver/wms?service=WMS&request=GetMap` | `GET /rest/services/{id}/MapServer/export` | Dynamic map rendering |
| `GET /geoserver/wms?request=GetMap` | `GET /ogc/services/{id}/wms` | WMS 1.3 compatible |
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

## Automated Migration

Honua provides admin API endpoints that discover GeoServer resources, generate reviewable migration manifests, and validate the current dry-run import path.

### Step 1: Discover Your GeoServer

Assess your GeoServer instance before importing. The discover endpoint analyzes workspaces, layers, and styles and returns a compatibility report.

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/geoserver/discover \
  -H "Content-Type: application/json" \
  -d '{
    "geoServerRestUrl": "http://geoserver-host:8080/geoserver/rest",
    "username": "admin",
    "password": "geoserver",
    "includeCompatibilityAnalysis": true
  }'
```

The response includes:
- Workspace and layer inventory
- Data store types and connection parameters
- Style formats and compatibility assessment
- Feature type counts and geometry types

Review the compatibility report before proceeding. Layers backed by PostGIS data stores have the highest migration fidelity.

### Step 2: Generate a Migration Manifest

Use the translate endpoint to generate a deterministic manifest that can be reviewed, saved externally, and used as the source artifact for later replay work.

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/geoserver/translate \
  -H "Content-Type: application/json" \
  -d '{
    "geoServerRestUrl": "http://geoserver-host:8080/geoserver/rest",
    "username": "admin",
    "password": "geoserver",
    "workspaceNames": ["transport"],
    "importStyles": true,
    "includeStyleContent": false
  }'
```

The manifest includes:
- Source provenance and a stable `manifestHash`
- Sanitized PostGIS connection drafts without source secrets
- Publish-plan entries for supported vector layers
- Metadata resources ready for later apply workflows
- Style-plan entries and explicit diagnostics for unsupported/manual work

Initial automatic translation is intentionally limited to PostGIS-backed vector layers. Non-PostGIS datastores, coverage stores, layer groups, ambiguous SRIDs, and SLD styles are surfaced as explicit diagnostics and manual follow-up steps.

### Step 3: Start a Dry-Run Import

> **Note:** The GeoServer import endpoint currently supports **dry-run mode only**. A dry run validates connectivity, discovers resources, and reports what would be imported without making changes.

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/geoserver/start \
  -H "Content-Type: application/json" \
  -d '{
    "geoServerRestUrl": "http://geoserver-host:8080/geoserver/rest",
    "username": "admin",
    "password": "geoserver",
    "dryRun": true
  }'
```

This returns a job ID for tracking progress.

### Step 4: Monitor Progress

```bash
# Check specific job status
curl http://localhost:8080/api/v1/admin/import/geoserver/jobs/{jobId}

# List all active import jobs
curl http://localhost:8080/api/v1/admin/import/geoserver/jobs
```

### Step 5: Cancel (if needed)

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

GeoServer uses SLD (Styled Layer Descriptor) or CSS styling. Honua uses MapLibre GL Style JSON. The current GeoServer translation endpoint preserves style intent in the migration manifest, but it does not automatically convert SLD into MapLibre JSON yet. Expect SLD and other unsupported style formats to be flagged for manual conversion.

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

- [ ] **WFS clients**: Point to `http://honua-host:8080/wfs` (WFS 2.0) or migrate to `http://honua-host:8080/ogc/features` (OGC API Features)
- [ ] **WMS clients**: Point to `http://honua-host:8080/rest/services/{id}/MapServer/WMS` or `http://honua-host:8080/ogc/services/{id}/wms`
- [ ] **WMTS clients**: Point to `http://honua-host:8080/rest/services/{id}/MapServer/WMTS` or `http://honua-host:8080/ogc/services/{id}/wmts`
- [ ] **REST API consumers**: Map GeoServer REST paths to Honua Admin API equivalents (see table above)
- [ ] **Authentication**: Replace GeoServer credentials with Honua API keys or OIDC tokens
- [ ] **Styles**: Review imported styles and adjust MapLibre JSON as needed
- [ ] **CRS configuration**: Verify required SRIDs exist in PostGIS `spatial_ref_sys`
- [ ] **Tile consumers**: Update tile URLs to Honua vector tile or WMTS endpoints

## GeoServer vs Honua Protocol Support

| Protocol | GeoServer | Honua |
|---|---|---|
| WFS 2.0 | Native | Supported |
| WMS 1.3 | Native | Via MapServer |
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
- Review the [API Examples](../API_EXAMPLES.md) for protocol-specific request patterns
- See the [Protocols Overview](../STANDARDS_APIS.md) for choosing the right protocol per client
- Check the [Admin API Reference](../CONTROL_PLANE_API.md) for connection and layer management
