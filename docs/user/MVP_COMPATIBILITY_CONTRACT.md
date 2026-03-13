# MVP Compatibility and Limitations (Launch Contract)

This page is the launch-facing compatibility contract for Honua open-core MVP.
Use this page first, then drill into the linked protocol matrices/spec docs.

## Launch Summary

| Protocol | MVP status | Supported now | Partial / unsupported highlights | Deep reference |
|---|---|---|---|---|
| GeoServices REST FeatureServer | Supported with partial parity | Query, edits, attachments, related records, domains, replica endpoints, calculate, validateSQL, append, GeoParquet query export | Advanced Esri operations remain partial/unsupported (query bins/top features, advanced SQL options, full offline parity) | [FeatureServer Coverage Matrix](feature-server-matrix.md) |
| GeoServices REST MapServer | Supported with partial parity | Export/identify/legend/find/query/tiles, WMS 1.3, WMTS 1.0 (KVP + RESTful) | WMTS scope limited to WebMercatorQuad; some Esri operations unsupported (generateKml) | [MapServer Coverage Matrix](map-server-matrix.md) |
| GeoServices REST ImageServer | Supported | Service metadata, exportImage, identify, tile | Raster serving for ArcGIS image workflows | — |
| GeoServices REST Geometry Service | Supported | Buffer, simplify, project, intersect, union, clip, difference, area, length | 9 geometry operations via PostGIS | [Geometry Service Coverage](specifications/geometry-service-coverage.md) |
| OGC API Features | Supported (CITE certified) | Core collections/items, transactions, CQL2 filtering, CRS, OpenAPI | 137/137 CITE tests passing; coverage varies by optional extensions | [OGC API Features Coverage](specifications/ogc-api-features-coverage.md) |
| OGC API Tiles | Supported (CITE certified) | Landing/conformance/collections, tilesets, vector/raster tiles | 16/16 CITE tests passing; 7 conformance classes | [OGC API Tiles Coverage](specifications/ogc-api-tiles-coverage.md) |
| OGC API Maps | Supported | Conformance, dataset map, collection map, styled map, map tiles | 32/32 conformance tests passing | — |
| WMS 1.3 | Supported (CITE certified) | GetCapabilities, GetMap, GetFeatureInfo | 227/227 CITE tests passing | [MapServer Coverage Matrix](map-server-matrix.md) |
| WMTS 1.0 | Supported (CITE certified) | GetCapabilities, GetTile, GetFeatureInfo (KVP + RESTful) | 118/118 CITE tests passing; WebMercatorQuad only | [MapServer Coverage Matrix](map-server-matrix.md) |
| OData v4 | Supported with partial parity | Core entities/metadata/query, `$batch`, `$apply`, `$search`, `$skiptoken`, `$deltatoken`, spatial functions | Delta change-tracking is timestamp-based (MVP-level); PUT not supported | [OData v4 Coverage](specifications/odata-v4-coverage.md) |
| Vector Tiles (MVT) | Supported | PostGIS-native `ST_AsMVT` generation, TileJSON metadata, auto-generated MapLibre styles | — | — |

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

- **Control-plane/admin APIs**: Path-versioned with deprecation lifecycle, preview channels, and OpenAPI contract governance. See [CONTROL_PLANE_VERSIONING_POLICY.md](CONTROL_PLANE_VERSIONING_POLICY.md).
- **Standards APIs**: Stable protocol paths defined by external specifications, not path-versioned by Honua. See [STANDARDS_APIS.md — Versioning and Compatibility Policy](STANDARDS_APIS.md#versioning-and-compatibility-policy).

## Release Ownership

Each release must update compatibility notes and caveats:
- [Release Checklist](../contributor/RELEASE_CHECKLIST.md)

This checklist requires:
- refreshed supported/partial/unsupported status
- tested client versions from compatibility certification
- known caveats and workarounds
- client template validation via [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
