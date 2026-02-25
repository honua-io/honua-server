# MVP Compatibility and Limitations (Launch Contract)

This page is the launch-facing compatibility contract for Honua open-core MVP.
Use this page first, then drill into the linked protocol matrices/spec docs.

## Launch Summary

| Protocol | MVP status | Supported now | Partial / unsupported highlights | Deep reference |
|---|---|---|---|---|
| GeoServices REST FeatureServer | Supported with partial parity | Query, edits, attachments, related records, domains, replica endpoints | Advanced Esri operations remain partial/unsupported (for example query bins/top features, advanced SQL options, full offline parity) | [FeatureServer Coverage Matrix](feature-server-matrix.md) |
| GeoServices REST MapServer | Supported with partial parity | Export/identify/legend/find/query, WMS 1.3, WMTS 1.0 (KVP) | WMTS scope is limited (for example matrix-set/encoding constraints), some Esri operations remain unsupported | [MapServer Coverage Matrix](map-server-matrix.md) |
| OGC API Features | Supported with partial parity | Core collections/items, transactions, filtering support, OpenAPI | Full standards coverage varies by conformance class and optional extensions | [OGC API Features Coverage](specifications/ogc-api-features-coverage.md) |
| OGC API Tiles | Supported with partial parity | Landing/conformance/collections, tilesets, vector/raster tile endpoints | Full parity depends on conformance class and matrix-set/profile support | [OGC API Tiles Coverage](specifications/ogc-api-tiles-coverage.md) |
| OData v4 | Supported with partial parity | Core OData entities/metadata/query, `$batch`, paging tokens, spatial functions | Delta/change-tracking semantics are MVP-level and not full enterprise change-feed parity | [OData v4 Coverage](specifications/odata-v4-coverage.md) |

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

## Release Ownership

Each release must update compatibility notes and caveats:
- [Release Checklist](../contributor/RELEASE_CHECKLIST.md)

This checklist requires:
- refreshed supported/partial/unsupported status
- tested client versions from compatibility certification
- known caveats and workarounds
