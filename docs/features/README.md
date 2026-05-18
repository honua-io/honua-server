# Honua Server Feature Map

This map summarizes source-backed runtime capabilities in `honua-server`.

## Data Plane

- GeoServices-style FeatureServer query, applyEdits, attachments, related records, MapServer export/identify/legend/find/query, ImageServer raster routes, Geometry Service operations, and NAServer-style routing hooks.
- OGC API Features, Tiles, Maps, Coverages, Processes, WFS 2.0, WMS 1.3, WMTS 1.0, WCS 2.0.1, OData v4, STAC catalog/search/items, COG registration, vector tiles, Terrain-RGB tiles, and elevation value/profile APIs.
- Output formats and negotiation for JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, GeoArrow, and native GeoBuf when supported by the feature store.
- File import for GeoJSON, Shapefile, GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase zips, GeoParquet, and raster import; ArcGIS GeoServices REST layer import and migration inventory; GeoServer REST migration inventory, dry-run validation, and bounded PostGIS-backed catalog apply; and cross-server consume probes.
- Streaming feature change/events endpoints and async geoprocessing over the canonical process runtime.

## Control Plane

- Admin APIs for auth, capabilities, version, connections, service settings, layer publishing, metadata resources, styles, SLD import/export, style suggestions, imports, manifests, GitOps watch/drift/approval, deployment control, observability, alerts, cache, rate limits, license, identity/OIDC, users, roles, geocoding, tile operations, and scene datasets.
- Spec workspace endpoints for validate, plan, apply, cancel, artifacts, and grounding helpers.
- Runtime licensing loads offline Ed25519-signed JSON envelopes, publishes active edition/entitlement status through admin and health surfaces, and gates paid features by entitlement key with HTTP 402 or gRPC `FAILED_PRECONDITION`.
- Configuration discovery, production monitoring, performance metrics, query-cache stats, health endpoints, OpenTelemetry, structured logs, Redis/in-memory caching, and output caching.

## 3D and Scene Status

- Implemented: scene dataset registry and resolution APIs, scene protocol endpoints, Terrain-RGB metadata/tiles, elevation APIs, vector tile/MapLibre style support, and scene metadata consumed by SDKs/mobile.
- Not yet complete: demo-grade 3D Tiles generation, point-cloud/reality-capture ingest (spike recommends pre-tiled 3D Tiles registered through the existing scene dataset registry as the first slice, with bounded follow-ups for COPC streaming and CPU/GPU PDAL conversion; see [`gis/point-cloud-reality-capture-ingest.md`](../gis/point-cloud-reality-capture-ingest.md)), native USD geometry conversion/Omniverse/Unreal integration beyond the Pro preview USDA stage manifest, and a unified construction world-model fixture remain backlog work.

## Source Evidence

- Endpoint inventory: `src/Honua.Server/Features/**/*Endpoints.cs`
- Scene APIs: `src/Honua.Server/Features/Admin/SceneDatasetEndpoints.cs`, `src/Honua.Server/Features/Protocols/Scene/SceneEndpoints.cs`
- Terrain/elevation/vector tiles: `src/Honua.Server/Features/Protocols/Terrain/`, `Elevation/`, `Tiles/`
- Admin/control plane: `src/Honua.Server/Features/Admin/`
- Import/migration: `src/Honua.Server/Features/Import/`
- Monitoring and health: `src/Honua.Server/Features/Infrastructure/Monitoring/`, `src/Honua.Server/Features/HealthCheck/`

## Release Risk

The server has the broadest surface and most backlog. Release readiness should be judged by a narrow acceptance path, not by waiting for every protocol, enterprise, and 3D backlog item to close.

## Planned Capabilities

- **GeoETL** (epic `#361`): scheduled, repeatable, multi-source spatial extract-transform-load pipelines as a Pro/Enterprise capability layered on the durable job substrate. See the [GeoETL Roadmap](../contributor/geoetl-roadmap.md) for child-ticket decomposition, runtime boundary (lean `honua-server` API + dedicated `honua-worker-etl` profile), and edition gating, and [ADR-0038](../contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md) for the binding architectural decisions. Community continues to ship one-shot file import unchanged.
