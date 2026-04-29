# MVP Compatibility and Limitations (Launch Contract)

This page is the launch-facing compatibility contract for Honua open-core MVP.
Use this page first, then drill into the linked protocol matrices/spec docs.
For GeoServices REST specifically, start with [GeoServices REST Parity](geoservices-rest-parity.md), use [data/geoservices-rest-parity.json](data/geoservices-rest-parity.json) for machine-readable review, and then drill into the service-specific matrices.

## Launch Summary

| Protocol | MVP status | Supported now | Partial / unsupported highlights | Deep reference |
|---|---|---|---|---|
| GeoServices REST FeatureServer | Supported with partial parity | Query, edits, attachments, related records, domains, replica endpoints, calculate, validateSQL, append, query bins/top features/date bins, estimates, GeoParquet and GeoArrow IPC query export | Advanced Esri operations remain partial/unsupported (asset management, 3D queries, advanced SQL options, full offline parity) | [FeatureServer Coverage Matrix](feature-server-matrix.md) |
| GeoServices REST MapServer | Supported with partial parity | Export/identify/legend/find/query/tiles, generateKml, WMS 1.1.1/1.3, WMTS 1.0 (KVP + RESTful) | WMS 1.1.1 CITE Basic evidence pending; WMTS scope limited to WebMercatorQuad; some optional response properties not populated | [MapServer Coverage Matrix](map-server-matrix.md) |
| GeoServices REST ImageServer | Supported with partial parity | Service metadata (with `timeInfo`), exportImage, identify, tile, raster catalog `query`, `computeStatisticsHistograms`, `legend`, `computeClass` raster-function chain validation | Catalog mutation, export tiles, find/queryBoundary, measure/project, AOI clipping for stats/histograms, full raster catalog child resources, WMTS, richer metadata resources | [ImageServer Coverage Matrix](image-server-matrix.md) |
| WCS 2.0.1 | Supported with partial parity | KVP `GetCapabilities`, `DescribeCoverage`, and `GetCoverage` for WCS-enabled raster layers at `/rest/services/{id}/ImageServer/WCS` and `/ogc/services/{serviceId}/wcs`; `image/tiff`, `image/png`, `image/jpeg`; `SUBSET`/`BBOX` spatial trim; optional `SUBSETTINGCRS`/`BBOXCRS` and `OUTPUTCRS` | Thin slice over the primary raster only; capabilities CRS advertisement is minimal (`EPSG:4326`) and `DescribeCoverage` is authoritative for native CRS; range subset/band selection, scaling/interpolation extensions, XML POST, NetCDF, temporal/multidimensional slicing, and WCS-specific mosaic selection are not implemented | [WCS 2.0.1 Coverage](specifications/wcs-2.0.1-coverage.md) |
| GeoServices REST Geometry Service | Supported with partial parity | Buffer, simplify, project, intersect, union, clip, difference, supplemental `area`/`length` routes | No GeometryServer root metadata endpoint; most Esri geometry operations remain unimplemented; Honua `area`/`length` routes are not Esri canonical paths | [Geometry Service Matrix](geometry-service-matrix.md) |
| GeoServices REST GPServer | Supported with partial parity | PrintingTools (Export Web Map, Layout Templates); generic GPServer adapter: catalog-backed service info and task metadata, async `submitJob`, job status, job cancel, and per-parameter result retrieval over canonical process runtime. Plan validation uses the built-in `IProcessCatalog` (34 seeded processes across `geometry.*`, `analytics.*`, `surface.*`, `raster.*`, `conversion.*`, `generalization.*`, `data-management.*`) | Generic built-in tasks are async-only and do not publish a generic `execute` route yet; result packages are synthesized from terminal job state when a persisted package is absent; heavyweight `surface.*` / `raster.*` work stays on the canonical worker boundary pending per-task projection into the GPServer surface; GP environment controls (`env:*`) are rejected; `context` is preserved as protocol metadata but not interpreted yet | [Geoprocess Framework Analysis](geoprocess-framework-analysis.md) |
| OGC API Features | Supported (CITE certified) | Core collections/items, transactions, CQL2 filtering, CRS, OpenAPI | 137/137 CITE tests passing; coverage varies by optional extensions | [OGC API Features Coverage](specifications/ogc-api-features-coverage.md) |
| OGC API Tiles | Supported (CITE certified) | Landing/conformance/collections, tilesets, vector/raster tiles | 16/16 CITE tests passing; 7 conformance classes | [OGC API Tiles Coverage](specifications/ogc-api-tiles-coverage.md) |
| OGC API Maps | Supported | Conformance, dataset map, collection map, map tiles, temporal raster mosaic via the `datetime` query parameter accepting RFC 3339 instants and intervals — `start/end`, `../end`, `start/..` (Pro+ editions; Community returns 403). Temporal selection uses newest effective acquisition batch before bbox windowing. | 32/32 conformance tests passing; `…/conf/datetime` advertised | Styled-map conformance class not claimed; `/ogc/maps/collections/{id}/styles/{styleId}/map` is not implemented; mixed-date raster scenes can leave coverage gaps until per-pixel temporal mosaicking is implemented |
| OGC API Processes | Supported (async-only) | Landing/conformance, process list/describe, async execution, job list/status/dismiss, job results (empty document on success); plan submissions are validated against the built-in `IProcessCatalog` (34 seeded processes across `geometry.*`, `analytics.*`, `surface.*`, `raster.*`, `conversion.*`, `generalization.*`, `data-management.*`) at the adapter boundary; heavyweight `surface.*` / `raster.*` entries are catalog- and validation-only today (handler/executor wiring that would dispatch them into `ISurfaceAnalysisService` / `IRasterStore.ComputeZonalStatisticsAsync` on the canonical worker boundary, optionally via the #727 executor adapters, is follow-on work); destructive `data-management.*` plans route through the operator approval gate before execution | V1: async-only (sync returns 501), single canonical process projection (`honua-geoprocessing`) pending per-process OGC surface projection, results endpoint returns document-mode JSON (empty `{}` on success until the canonical process declares value-typed outputs; `404` non-terminal, `500` failed, `410` dismissed), `limit`-only job list filter, Redis-backed job store required for execution and job routes | [OGC API Processes Coverage](specifications/ogc-api-processes-coverage.md) |
| WMS 1.3 | Supported (CITE certified) | GetCapabilities, GetMap, GetFeatureInfo | 227/227 CITE tests passing | [MapServer Coverage Matrix](map-server-matrix.md) |
| WMS 1.1.1 | Supported, CITE pending | GetCapabilities, GetMap, GetFeatureInfo | Uses `SRS`, `X/Y`, 1.1.1 service exceptions, and lon/lat `EPSG:4326` BBOX order; Basic CITE evidence pending | [MapServer Coverage Matrix](map-server-matrix.md) |
| WMTS 1.0 | Supported (CITE certified) | GetCapabilities, GetTile, GetFeatureInfo (KVP + RESTful) | 118/118 CITE tests passing; WebMercatorQuad only | [MapServer Coverage Matrix](map-server-matrix.md) |
| WFS 2.0 | Supported with partial parity | GetCapabilities, DescribeFeatureType, GetFeature, query/export paths exercised by GDAL/OGR and QGIS | Optional operations and full transaction parity remain partial/unsupported | [WFS 2.0 CITE Guide](../contributor/cite-wfs20-conformance-testing.md) |
| WFS 1.1.0 | Supported, CITE pending | GetCapabilities, DescribeFeatureType, GetFeature over GET and POST | Read-only compatibility; Basic CITE evidence pending | [Legacy OGC CITE Guide](../contributor/cite-legacy-ogc-conformance-testing.md) |
| WFS 1.0.0 | Supported, CITE pending | GetCapabilities, DescribeFeatureType, GetFeature | Read-only compatibility; Basic CITE evidence pending | [Legacy OGC CITE Guide](../contributor/cite-legacy-ogc-conformance-testing.md) |
| OData v4 | Supported with partial parity | Core entities/metadata/query, `$batch`, `$apply`, `$search`, `$skiptoken`, `$deltatoken`, spatial functions | Delta change-tracking is timestamp-based (MVP-level); PUT not supported | [OData v4 Coverage](specifications/odata-v4-coverage.md) |
| Vector Tiles (MVT) | Supported | PostGIS-native `ST_AsMVT` generation, TileJSON metadata, auto-generated MapLibre styles; automated browser render proof via Playwright (merge-blocking) | — | — |
| STAC API | Supported | Catalog, collections, items, item lookup, GET/POST search with fields, sortby, CQL2 filtering | ETag on catalog/collection metadata only; `bbox`/`intersects` remain CRS84 while CQL2 `filter-crs` and explicit geometry CRS accept registry-backed EPSG identifiers; no STAC transaction extensions | [STAC API (STANDARDS_APIS.md)](STANDARDS_APIS.md#stac-api) |
| GDAL/OGR (ogrinfo / ogr2ogr) | Supported | OAPIF and WFS 2.0 discovery, read, query, and export workflows through GDAL/OGR clients | Tested with GDAL 3.4+ against OGC API Features and WFS 2.0 endpoints | — |
| KML 2.2 (format) | CITE validated | `MapServer/generateKml` output validated against OGC KML 2.2 schema | Format-level conformance; always EPSG:4326 | [KML 2.2 CITE Guide](../contributor/cite-kml22-conformance-testing.md) |
| GML 3.2 (format) | CITE validated | OGC API Features GML content negotiation validated against OGC GML 3.2 schema | Format-level conformance via `Accept: application/gml+xml; version=3.2` | [GML 3.2 CITE Guide](../contributor/cite-gml32-conformance-testing.md) |
| GeoPackage 1.2 (format) | CITE validated | Admin layer export GeoPackage validated against OGC GeoPackage 1.2 spec | Format-level conformance; requires admin auth for export | [GeoPackage 1.2 CITE Guide](../contributor/cite-gpkg12-conformance-testing.md) |

## FeatureServer Replication Limitations (MVP)

Replication endpoints are available, but MVP replication is limited and should be treated as preview behavior:

- `createReplica`, `extractChanges`, `synchronizeReplica`, `unRegisterReplica` are implemented.
- Replica registration state uses distributed cache when available, with in-memory fallback.
- `extractChanges` uses MVP semantics:
- first sync reports full add set
- subsequent syncs do not provide full DB-level incremental change tracking parity
- Sync upload/download/bidirectional flows are supported, but this is not a full ArcGIS enterprise offline geodatabase replacement.

Recommended use during MVP:
- short-lived sync workflows
- client compatibility validation
- controlled operational scenarios where full historical change tracking is not required

## Versioning Policies

- **Control-plane/admin APIs**: Path-versioned with deprecation lifecycle, preview channels, and OpenAPI contract governance. See [CONTROL_PLANE_VERSIONING_POLICY.md](../developer/CONTROL_PLANE_VERSIONING_POLICY.md).
- **Standards APIs**: Stable protocol paths defined by external specifications, not path-versioned by Honua. See [STANDARDS_APIS.md — Versioning and Compatibility Policy](STANDARDS_APIS.md#versioning-and-compatibility-policy).

## Release Ownership

Each release must update compatibility notes and caveats:
- [Release Checklist](../contributor/RELEASE_CHECKLIST.md)

This checklist requires:
- refreshed supported/partial/unsupported status
- validated [Public Interface Proof Ledger](data/public-interface-proof.json) against the shipped runtime surface (see [Quality Model](../contributor/public-interface-quality-model.md))
- tested client versions from compatibility certification
- known caveats and workarounds
- client template validation via [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
- cross-client certification evidence per the [Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
