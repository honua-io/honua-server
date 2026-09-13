---
type: reference
title: "Supported clients and known limitations"
description: "This page lists the clients Honua Server is tested against, the protocol each one uses, and the honest list of current gaps."
---
# Supported clients and known limitations

This page lists the clients Honua Server is tested against, the protocol each one
uses, and the honest list of current gaps. Tested versions come from the pinned
[client template version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) and the
[cross-client certification matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md);
rows without checked-in evidence are marked accordingly rather than claimed.

## Client × protocol matrix

| Client | Protocols | Tested version | Evidence and guides |
|---|---|---|---|
| ArcGIS Pro | GeoServices REST FeatureServer, MapServer | 3.4.0 (2026-04-02 evidence) | [Connect ArcGIS Pro](../../guides/connect/arcgis-pro.md); [version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) |
| QGIS | OGC API Features, WFS 2.0, WMS, WMTS | 3.40.0 (manual smoke) + nightly automated PyQGIS runs | [Connect QGIS](../../guides/connect/qgis.md); [version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) |
| MapLibre GL JS | Vector tiles (MVT), TileJSON, auto-generated styles | 4.7.1 baseline; re-certified per CI run at the installed version | [Version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md); [publish tiles](../../guides/publish/publish-tiles.md) |
| Esri Leaflet | FeatureServer, MapServer | 3.0.19 (lockfile-resolved, automated Playwright suite) | [Certification matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md#esri-leaflet-browser-sub-lane) |
| CesiumJS | WMS, WMTS, OGC API Tiles, OGC API Maps (imagery providers), hosted 3D Tiles | Automated Playwright suite; version not pinned in the evidence matrix | [Certification matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md); [publish 3D scenes](../../guides/publish/publish-3d-scenes.md) |
| Power BI Desktop | OData v4 | 2.142.1053.0 (2026-04-02 evidence) | [Connect Excel and Power BI](../../guides/connect/excel-power-bi.md); [version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) |
| Excel | OData v4 | 2402 (Build 17328.20174) (2026-04-02 evidence) | [Connect Excel and Power BI](../../guides/connect/excel-power-bi.md); [version matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) |
| Tableau | OData v4 | Not verified — no checked-in certification evidence; the OData v4 surface Tableau consumes is the same one certified for Power BI/Excel | [OData v4 coverage](../protocols/odata.md) |
| GeoPandas / Python | OGC API Features, FeatureServer (incl. GeoParquet/GeoArrow query export), STAC | Automated pytest suite (server-side validation); client version not pinned | [Integration patterns](../integration-patterns.md) |
| GDAL/OGR (`ogrinfo`/`ogr2ogr`) | OGC API Features, WFS 2.0 | GDAL 3.4+ | [Migrating from GeoServer](../../guides/migrate/from-geoserver.md) |
| gRPC SDKs | `Geospatial.V1` gRPC surface | Generated from the stable proto contract; no per-client version matrix | [gRPC reference](../protocols/grpc.md) |
| AI agents / MCP | MCP over admin + query surfaces | — | [Connect AI agents](../../guides/connect/ai-agents-mcp.md) |

ArcGIS Pro, QGIS (manual lane), Power BI, and Excel evidence is the checked-in
2026-04-02 immutable certification snapshot; PyQGIS, MapLibre, Esri Leaflet, and
Cesium lanes are re-certified automatically in CI. See the
[certification matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md) for the
per-lane test-case coverage (connection, auth, discovery, schema, query, paging,
geometry fidelity, error handling, rendering).

## Public URL and host validation

Set `Public:BaseUrl` (or its `Public__BaseUrl` environment form) or
`PUBLIC_BASE_URL` to the public HTTP(S) origin used by desktop clients. A blank
`Public:BaseUrl` falls back to `PUBLIC_BASE_URL`; a nonblank primary setting takes
precedence. Request host validation, strict startup validation
(`HostValidation:RequireExplicitHosts=true`) and generated links use the same URL resolution.
An explicit host allowlist still takes precedence over the public URL, and
unrelated request hosts remain rejected when host validation is enabled.

## Managed API keys and Portal token exchange

Managed API-key expiry rejects the credential while retaining its registry
metadata for administrative list, effective-permissions and revoke operations.
An expired or revoked record does not authorize a desktop request. Internal
approval replay credentials still expire from Redis. Older registry entries
need a successful validation, rotation or revocation before their original Redis
expiry to adopt the new retention behavior; already removed records cannot be
restored. See the [managed-key lifecycle guide](../../guides/secure/authentication.md).

The default local admin bridge for `/sharing/rest/generateToken` accepts bootstrap
admin credentials and managed keys with full administrative authority. Scoped
service/layer keys, narrow admin or operations keys, and approved-operation replay
keys receive an Esri `400` issuance error rather than an elevated admin token.
The restriction applies with both `admin` and other supplied usernames. Direct
API-key authorization retains the key's existing grants; configured OIDC token
issuance uses its existing identity projection.

This is an explicit unsupported exchange, not evidence that scoped Portal-token
authentication works in a desktop client. Native acquisition, storage, lifecycle
and protocol authorization still require separate client evidence. See
[authentication setup and token revocation during upgrades](../../guides/secure/authentication.md#4-issue-arcgis-compatible-tokens).

## Realtime credentials and reconnects

Protected FeatureServer and SensorThings SSE/WebSocket subscriptions revalidate
their admitted credential every second in a fresh authentication scope. Expiry,
issuer-observed revocation, changed identity/tenant/role claims, or validation
failure ends the subscription. SSE sends `event: status` with
`{"status":"error","code":"authorization-ended"}` and closes; WebSocket closes
with code `1008` and reason `authorization-ended`. Clients must obtain a valid
replacement credential before reconnecting. OData polling authenticates each
request and rejects expired or revoked portal credentials with HTTP 401.

The five-second qualification bound requires zero issuer expiry leeway. Portal
tokens have no expiry leeway; OIDC deployments qualifying this bound must set
`TokenValidation.ClockSkew` to zero (the default is five minutes). Revocation is
observable through the configured authenticator's policy and backing store.

Feature-stream clients retain their last delivered cursor and reconnect with the
same authorized tenant scope; access policies are evaluated again before replay.
Changing credentials does not grant access to another tenant's resources.
SensorThings subscriptions require an explicit tenant claim for non-admin users
and are live-only: cursor or `Last-Event-ID` resume attempts return HTTP 400.
A SensorThings subscription refused by an admission cap never opens a stream:
HTTP 429 means the caller's own credential or tenant already holds its share of
concurrent subscriptions (close one or wait), and HTTP 503 means the node is at
capacity. Both carry `Retry-After`; clients must honour it rather than reconnect
in a tight loop.
These local regression guarantees do not certify a release candidate; exact-image
live issuer/SDK evidence is still required by the qualification gate.

## Known limitations

Current gaps, stated as fact. Protocol-level Esri parity detail lives in
[GeoServices REST parity](geoservices-parity.md); OGC pass rates live in
[OGC conformance](ogc-conformance.md).

- **FeatureServer replication is MVP-scoped.** `createReplica`, `extractChanges`,
  `synchronizeReplica`, and `unRegisterReplica` are implemented, but the first sync
  reports the full add set and later syncs do not provide DB-level incremental
  change tracking. Suitable for short-lived sync and client validation, not a full
  ArcGIS offline-geodatabase replacement.
- **GeoServices 64-bit fields require client support.** Ordinary BigInteger
  fields use Esri's `esriFieldTypeBigInteger` in layer metadata and JSON queries,
  and field enum 13 in PBF; object identifiers retain `esriFieldTypeOID`.
  Stock QGIS 3.40.15's ArcGIS REST provider does not recognize BigInteger fields.
  Native table/export testing retained a missing-column failure; correcting
  Honua's former invalid `esriFieldTypeInteger64` token does not establish a
  QGIS type-preservation pass. See [the server defect](https://github.com/honua-io/honua-server/issues/4551)
  and [the version-specific QGIS converter](https://github.com/qgis/QGIS/blob/final-3_40_15/src/core/providers/arcgis/qgsarcgisrestutils.cpp#L59).
- **WMS 1.1.1 passes its CITE profile.** It is served (with `SRS`, `X`/`Y`, and
  lon/lat EPSG:4326 BBOX order); both WMS 1.1.1 and WMS 1.3 have current
  all-pass CITE evidence.
- **WMTS scope is WebMercatorQuad only** on the GeoServices `MapServer/WMTS` alias
  and the `/ogc` classic surface.
- **SensorThings discovery and navigation cover the five exposed entity sets.**
  Both `/sta/v1.1` and `/sta/v1.1/` list Things, Sensors, ObservedProperties,
  Datastreams and Observations. Follow links in either direction between a
  Datastream and its Thing/Sensor/ObservedProperty, and between an Observation
  and its Datastream. Related Datastream collections apply query options within
  the relationship, including counts and continuation links.
- **SensorThings query options are honoured or refused, never ignored.** `$filter`,
  `$orderby`, `$select`, `$top`, `$skip` and `$count` work on every entity set, and
  `$expand` works on Datastreams for `Thing`, `Sensor`, `ObservedProperty` and
  `Observations` (including nested `$top`/`$skip`/`$filter`/`$orderby`). Anything
  outside that surface fails the request rather than returning unfiltered data:
  expanding `Datastreams` from a Thing/Sensor/ObservedProperty, or `Datastream`/
  `FeatureOfInterest` from an Observation, returns HTTP 501. Follow the entity's
  `@iot.navigationLink` for supported relationships instead. FeaturesOfInterest
  and Locations are not exposed; observations omit FeatureOfInterest links even
  when an ingested row carries an opaque FeatureOfInterest identifier. A `$filter` naming an unknown property, or carrying
  a literal of the wrong type for its property, returns HTTP 400. `$filter` and
  `$orderby` on a single-entity route return HTTP 400 because they cannot apply.
- **OGC API Processes negotiates sync and async execution.** Omission runs a process
  synchronously when it advertises `sync-execute`; `Prefer: respond-async` requests a
  durable job. Document-mode JSON is the default, while synchronous single-output
  values may request raw mode. All execution modes currently submit through the durable
  job pipeline, so a Redis-backed job store is required for synchronous, asynchronous,
  and job routes.
- **OGC API Maps does not claim the styled-map conformance class**, and temporal
  raster mosaics use newest-batch semantics — layers with mixed-date scenes can show
  coverage gaps under a `datetime` filter until per-pixel temporal mosaicking lands.
- **WCS 2.0.1 is a thin slice over the primary raster.** Range subset/band
  selection, scaling/interpolation extensions, XML POST, NetCDF, and
  temporal/multidimensional slicing are not implemented.
- **ImageServer metadata retains native mosaic resolution.** `pixelSizeX` and
  `pixelSizeY` advertise the finest finite positive source geotransform scale
  on each axis. Aggregate extent rounding or offsets between source rasters
  do not change these values. If an axis has no usable geotransform scale,
  metadata retains the aggregate-extent/primary-dimension fallback. This
  contract is covered by HTTP regressions and does not establish native
  QGIS or ArcGIS Pro raster certification.
- **OGC API Coverages is MVP-scoped**: GeoTIFF/PNG retrieval with bbox/CRS/scale
  parameters; `datetime`, `subset`, CoverageJSON, NetCDF, and tiled coverage
  delivery are not implemented. Collection discovery emits each accessible
  storage-layer identifier once even when feature and raster resources share
  that storage binding. Access filtering precedes deduplication, with a primary
  publication preferred among accessible aliases. Numeric collection detail URLs
  use the same storage identity and accessible-publication selection as discovery,
  including when publication IDs differ from the storage-layer ID. Other protocols
  retain their existing publication-identifier routing.
- **OData v4 delta tracking uses durable authorized query snapshots.** Clients
  apply key-preserving `@removed` entries for deletes and filter exits. Legacy
  timestamp tokens require a new baseline after typed 410 recovery. Tracking
  requires PostgreSQL snapshot storage, expires after 24 hours, and has bounded
  query shapes and capacity; see [OData change tracking](../protocols/odata.md#change-tracking).
- **GeoServices GPServer synchronous `execute` is limited to deterministic
  single-geometry tasks** (the `geometry.*` family and `conversion.geometry-format`,
  run inline over the canonical job runtime); heavyweight/layer-scoped tasks stay
  async-only and reject `execute` with a 400 pointing at `submitJob`. GP
  environment controls (`env:*`) are rejected on `submitJob` (sync `execute`
  honors `env:outSR`); heavyweight `surface.*` / `raster.*` processes are
  catalog/validation-only pending executor wiring.
- **I3S / ArcGIS Scene Layer is a bounded Enterprise adapter, not a certification claim.**
  Service/layer descriptors, node pages, statistics, geometry buffers, and
  attribute buffers are served under the canonical `SceneServer` routes and
  `/scenes` aliases. The entitlement gate returns `402`. Broader I3S
  geometry/client certification remains 2026.2 work. CesiumJS-oriented 3D
  Tiles hosting and generation are also served; generation uses tiled LOD for
  larger inputs and the server-side feature limit, rather than the obsolete
  single-tile/50,000-feature description.
- **Terrain/elevation v1 limits**: one numeric elevation band and one usable CRS
  per dataset; no quantized mesh, hydrology, or terrain analysis; elevation queries
  are band-1, GET-only, with no vertical datum transformation.
- **STAC API has no transaction extensions**; `bbox`/`intersects` remain CRS84.
- **OpenUSD/Omniverse export is a preview manifest only** (Pro-gated): no USD
  geometry conversion, USDZ packaging, or Nucleus publishing.

Each release re-validates this page via the
[release checklist](../../internal/contributor/RELEASE_CHECKLIST.md), which requires
refreshed supported/partial status, tested client versions, and certification
evidence per the [evidence specification](../../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md).
