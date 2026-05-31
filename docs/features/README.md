# Honua Server Feature Map

This map summarizes source-backed runtime capabilities in `honua-server`.

## Data Plane

- GeoServices-style FeatureServer query, applyEdits, attachments, related records, MapServer export/identify/legend/find/query, ImageServer raster routes, Geometry Service operations, and NAServer-style routing hooks.
- OGC API Features, Tiles, Maps, Coverages, Processes, WFS 2.0, WMS 1.3, WMTS 1.0, WCS 2.0.1, OData v4, STAC catalog/search/items, COG registration, vector tiles, Terrain-RGB tiles, and elevation value/profile APIs.
- Output formats and negotiation for JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, GeoArrow, and native GeoBuf when supported by the feature store.
- Server-owned field form packages with versioned drafts, immutable published versions, offline policy discovery, and idempotent published-package submissions through the shared edit and attachment pipelines.
- File import for GeoJSON, Shapefile, GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase zips, GeoParquet, and raster import; ArcGIS GeoServices REST layer import and migration inventory; GeoServer REST migration inventory, dry-run validation, and bounded PostGIS-backed catalog apply; and cross-server consume probes.
- Streaming feature change/events endpoints, async geoprocessing over the canonical process runtime, and durable analysis content for saved-query/package versions plus reusable result artifacts.
- ArcGIS-compatible Portal Sharing token issuance at `POST`/`GET /sharing/rest/generateToken`, so Esri clients can exchange username/password credentials for an opaque bearer token and reuse it against `/rest/services/*` via `?token=`, `Authorization: Bearer`, or `X-Esri-Authorization: Bearer` (Community-tier, gated by the `identity.portal-token` entitlement; see [Security](../operator/security.md#authentication)).

## Control Plane

- Public capability manifest at `/api/v1/capabilities/manifest` for Console, MCP, QGIS plugins, native hosts, and SDK clients to discover package family support, temporal/sync/realtime/jobs/GitOps/transport/mTLS states, runtime limits, policy hints, license/entitlement decisions, environment/workspace availability, and related capability links without probing individual endpoints.
- Admin APIs for auth, capabilities, version, connections, service settings, layer publishing, Metadata v2 environment inventory, release packages, compatibility prevalidation, release operation lifecycle/rollback state, metadata resources, styles, SLD import/export, style suggestions, imports, manifests, GitOps watch/drift/approval, deployment control, observability, geofence zones and alert rules, form package authoring/publishing, cache, rate limits, license, identity/OIDC, users, roles, geocoding, tile operations, and scene datasets.
- Console/Studio APIs for server-owned content metadata, action checks, workflow node registry data, mutable workflow and Studio package drafts, immutable content versions, validation, dry-run and preview plans, publication requests, runs, provenance, server-owned map/dashboard/report/generated-app publication records, public route resolution, share/embed/public-link policy, reopen, comparison, and rollback for query, analysis, map, dashboard, report, form, app, workflow, GP, and ETL packages.
- Share admin APIs for scheduled export definitions, append-mostly export run history with nullable Operate `jobRunId` links, destination support badges, and aggregate/per-item Share traffic summaries and time series.
- Analysis content APIs for saving query/package content versions, previewing saved queries, submitting/rerunning analysis packages, resolving artifact bindings, and exposing safe failed-job diagnostics.
- Package validation and read-only preview planning for generated plans, publish candidates, workflows, ETL candidates, app packages, and map packages through one shared response contract for admin HTTP, MCP, SDK, CI, and generated-app clients.
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
- Capability manifest: `src/Honua.Server/Features/Capabilities/`
- Console workflow packages: `src/Honua.Server/Features/WorkflowPackages/`
- Content publication registry: `src/Honua.Core/Features/Publishing/Content/`, `src/Honua.Server/Features/Console/Publications/`, `src/Honua.Postgres/Features/Publishing/`
- Import/migration: `src/Honua.Server/Features/Import/`
- Portal Sharing token issuer + auth handler: `src/Honua.Core/Features/Authorization/Abstractions/IPortalTokenIssuer.cs`, `src/Honua.Hosting/Features/Authentication/PortalTokenIssuer.cs`, `src/Honua.Hosting/Features/Authentication/PortalTokenAuthentication*.cs`, `src/Honua.Protocols.GeoServices/Sharing/SharingRestEndpoints.cs`
- Forms package/submission contracts: `src/Honua.Core/Features/Forms/Packages/`, `src/Honua.Server/Features/Forms/`, `src/Honua.Postgres/Features/Forms/`
- Monitoring and health: `src/Honua.Server/Features/Infrastructure/Monitoring/`, `src/Honua.Server/Features/HealthCheck/`

## Release Risk

The server has the broadest surface and most backlog. Release readiness should be judged by a narrow acceptance path, not by waiting for every protocol, enterprise, and 3D backlog item to close.

## Planned Capabilities

- **GeoETL** (epic `#361`): scheduled, repeatable, multi-source spatial extract-transform-load pipelines as a Pro/Enterprise capability layered on the durable job substrate. See the [GeoETL Roadmap](../contributor/geoetl-roadmap.md) for child-ticket decomposition, runtime boundary (lean `honua-server` API + dedicated `honua-worker-etl` profile), and edition gating, and [ADR-0038](../contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md) for the binding architectural decisions. Community continues to ship one-shot file import unchanged.
