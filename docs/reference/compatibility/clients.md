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

## Known limitations

Current gaps, stated as fact. Protocol-level Esri parity detail lives in
[GeoServices REST parity](geoservices-parity.md); OGC pass rates live in
[OGC conformance](ogc-conformance.md).

- **FeatureServer replication is MVP-scoped.** `createReplica`, `extractChanges`,
  `synchronizeReplica`, and `unRegisterReplica` are implemented, but the first sync
  reports the full add set and later syncs do not provide DB-level incremental
  change tracking. Suitable for short-lived sync and client validation, not a full
  ArcGIS offline-geodatabase replacement.
- **WMS 1.1.1 passes its CITE profile.** It is served (with `SRS`, `X`/`Y`, and
  lon/lat EPSG:4326 BBOX order); both WMS 1.1.1 and WMS 1.3 have current
  all-pass CITE evidence.
- **WMTS scope is WebMercatorQuad only** on the GeoServices `MapServer/WMTS` alias
  and the `/ogc` classic surface.
- **OGC API Processes is async-only.** Synchronous execution returns `501`; results
  are document-mode JSON; a Redis-backed job store is required for execution and
  job routes.
- **OGC API Maps does not claim the styled-map conformance class**, and temporal
  raster mosaics use newest-batch semantics — layers with mixed-date scenes can show
  coverage gaps under a `datetime` filter until per-pixel temporal mosaicking lands.
- **WCS 2.0.1 is a thin slice over the primary raster.** Range subset/band
  selection, scaling/interpolation extensions, XML POST, NetCDF, and
  temporal/multidimensional slicing are not implemented.
- **OGC API Coverages is MVP-scoped**: GeoTIFF/PNG retrieval with bbox/CRS/scale
  parameters; `datetime`, `subset`, CoverageJSON, NetCDF, and tiled coverage
  delivery are not implemented.
- **OData v4 delta tracking is timestamp-based** (MVP-level) and `PUT` is not
  supported.
- **GeoServices GPServer synchronous `execute` is limited to deterministic
  single-geometry tasks** (the `geometry.*` family and `conversion.geometry-format`,
  run inline over the canonical job runtime); heavyweight/layer-scoped tasks stay
  async-only and reject `execute` with a 400 pointing at `submitJob`. GP
  environment controls (`env:*`) are rejected on `submitJob` (sync `execute`
  honors `env:outSR`); heavyweight `surface.*` / `raster.*` processes are
  catalog/validation-only pending executor wiring.
- **I3S / ArcGIS Scene Layer is not implemented** (Enterprise roadmap). Unlicensed
  `SceneServer` routes return `402` with an entitlement message. CesiumJS-oriented
  3D Tiles hosting and v1 generation are supported instead — generation emits a
  single tile per job (no LOD), drops polygon inner rings, and caps at 50,000
  features by default.
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
