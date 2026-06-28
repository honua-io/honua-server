# gRPC

Honua hosts a versioned gRPC surface from the [`Geospatial.Grpc`](https://github.com/honua-io/geospatial-grpc) protocol package (`geospatial.v1` protobuf package, `Geospatial.V1` .NET namespace) for high-throughput feature, geoprocessing, spec, and scene access.

## Connection

| Item | Value |
| --- | --- |
| Port | 8081, HTTP/2 cleartext (h2c) — `Kestrel:Endpoints:Grpc:Url = http://+:8081` (Docker images expose 8080 HTTP + 8081 gRPC; `HONUA_GRPC_PORT` in docker-compose). |
| Health | gRPC health checks enabled (`grpc.health.v1.Health`). |
| Reflection | Server reflection enabled — `grpcurl -plaintext server:8081 list`. |
| Compression | gzip response compression negotiated by default. |
| Message size | 16 MiB send/receive default; `Grpc:MaxReceiveMessageSize` / `Grpc:MaxSendMessageSize` to override. |
| Streaming page size | `Grpc:StreamBatchSize` (default 1000 features per message). |

## Services (geospatial.v1)

| Service | RPCs | Purpose |
| --- | --- | --- |
| `FeatureService` | `QueryFeatures`, `QueryFeaturesStream` (server streaming), `ApplyEdits` | Feature query and edits; respects per-service protocol enablement and access policy. |
| `ProcessService` | `ValidatePlan`, `DryRunPlan`, `ExecutePlan`, `ExecutePlanStream` (server streaming), `SubmitJob`, `GetJob`, `GetJobResult`, `CancelJob` | Canonical geoprocessing runtime (same backend as GPServer and OGC API Processes). |
| `SpecService` | `PlanSpec`, `ApplySpec` (server streaming), `CancelApply` | Spec plan/apply engine. |
| `SceneService` | `ListScenes`, `GetScene` | Scene dataset discovery. |
| `TileService` | `GetTile`, `StreamTiles` (server streaming) | Tile retrieval. |
| `ElevationService` | `GetElevation`, `GetElevationProfile` | Elevation point and profile queries. |

```bash
# List services and call a query via reflection
grpcurl -plaintext server.example.com:8081 list
grpcurl -plaintext -d '{"serviceId":"roads","layerId":0,"where":"1=1"}' \
  server.example.com:8081 geospatial.v1.FeatureService/QueryFeatures
```

## Feature query result contract (REST parity)

`QueryFeatures` (`QueryFeaturesResponse`) and `QueryFeaturesStream` (the first
`FeaturePage`) carry the same descriptive metadata that the GeoServices REST
`query` response returns inline, so clients reach result parity without a
separate layer-metadata round trip:

| Field | Meaning |
| --- | --- |
| `spatial_reference` | The spatial reference of the returned geometries. Reflects the requested `out_sr` when supplied, otherwise the layer's spatial reference. |
| `geometry_type` | The layer geometry type (`POINT`, `POLYLINE`/line, `POLYGON`, multi variants, or `NONE` for tables). |
| `object_id_field_name` | The primary object-id field name (defaults to `objectid`). |
| `fields` | Field definitions (name, type, length, nullability) for every non-geometry attribute field. |

Notes:

- For streaming queries the metadata is populated **only on the first
  `FeaturePage`**; subsequent pages set `is_last_page` and feature payloads only.
- `fields` is emitted for full feature payloads. The `return_count_only`,
  `return_ids_only`, and `return_extent_only` shapes return the relevant scalar
  (`count`, `object_ids`, `extent`) and still carry `spatial_reference` /
  `geometry_type`, but omit `fields`.
- Metadata-fallback contract: if a client is pinned to an older `Geospatial.Grpc`
  package whose `geospatial.v1` messages predate one of these fields, that field
  is absent (proto3 implicit default) and the client should fall back to the
  layer metadata surface (`FeatureService` reflection / the REST layer document)
  for the missing descriptor. Current `geospatial.v1` carries all four fields.

## Versioning and deprecation policy

Every protobuf package carries a major-version suffix (`geospatial.v1`, future `geospatial.v2`); a service lives under exactly one major version, and versions are hosted side by side.

Within a major version the wire contract is frozen:

- Never remove, renumber, or change the wire type of a shipped field; never change `oneof` membership, implicit defaults, or a method's streaming direction.
- Changes are additive only: new fields with new tag numbers, new enum values, new messages, new RPCs.
- Retiring fields are marked `[deprecated = true]` (surfaced as `[Obsolete]` in .NET) for at least one minor release; fully retired tags and names become `reserved`.
- Status codes for established failure modes are part of the contract.

Breaking changes require a new major version introduced alongside the old one: open an interface ADR, add `proto/geospatial/v2/` in parallel, host both service versions, announce `v1` deprecation when `v2` ships, and keep `v1` functional for at least two minor releases (longer if contracted clients depend on it) before removal.

CI enforcement: protobuf changes in the `Geospatial.Grpc` package must pass a breaking-change linter (`buf breaking`) against the latest release tag, and the server pins a specific package version so consumer breaks fail the build.

## Conformance

gRPC is a Honua-native surface (not OGC). Contract stability is tracked through the public-interface proof ledger; HTTP standards status lives in the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Run geoprocessing](../../guides/query-analyze/run-geoprocessing.md)
- [React to changes](../../guides/edit/react-to-changes.md)
- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md)
