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

### Pre-1.0 exception

`Geospatial.Grpc` is still pre-1.0, and its [`VERSIONING.md` Pre-1.0 Exception](https://github.com/honua-io/geospatial-grpc/blob/main/VERSIONING.md) permits a coordinated structural break inside `geospatial.v1` while the package major version is `0.x`. The freeze described above becomes binding at `1.0.0`.

One such break has been exercised. `0.2.0-alpha.1` (geospatial-grpc#48 Option A) promoted the duplicated job-lifecycle control-plane messages to `execution_types.proto` and converged `SpecService` onto them, and `0.1.0-alpha.3` unified the severity enums and widened feature-query pagination. The protobuf package stayed `geospatial.v1`, so no client support floor moved, but callers built against `0.1.x` bindings must regenerate. The changes that alter what a client reads:

| Change | Before | After |
| --- | --- | --- |
| Job-lifecycle messages | Per-service `Validate*Response`, `DryRun*Response`, `Submit*JobResponse`, `Get*JobRequest/Response`, `Get*JobResultRequest`, `Cancel*JobRequest/Response` | Shared `ValidateResponse`, `DryRunResponse`, `SubmitJobResponse`, `GetJobRequest`/`GetJobResponse`, `GetJobResultRequest`, `CancelJobRequest`/`CancelJobResponse` |
| Severity | `IssueSeverity` / `ValidationSeverity` / `SpecDiagnosticSeverity` (`ERROR = 1`) | Shared `Severity`, ordered ascending (`INFO = 1 < WARNING = 2 < ERROR = 3`) |
| `ErrorDetail` identity | `string error_code = 1` | `int32 code = 1`; the symbolic name Honua used to send is preserved in `details["error_code"]` |
| Edit errors | `EditError` | `ErrorDetail` |
| Feature-query pagination | `int32 result_offset = 8`, `int32 result_record_count = 9` | `int64 result_offset_long = 20`, `int64 result_record_count_long = 21`; fields 8/9 are `reserved` |
| Scene / style catalog pagination | `result_offset` + `result_record_count`, `exceeded_transfer_limit` | `page_size` + opaque `page_token`, `next_page_token` |
| Spec apply identity | `ApplySpecEvent.apply_token`, `CancelApplyRequest`/`CancelApplyResponse` | `ApplySpecEvent.job_id` (field 10), `CancelJobRequest`/`CancelJobResponse` |
| Spec node fragments | `map<string, string> inputs` / `parameters` | `map<string, ParameterValue>`; Honua accepts the `string_value` branch only |
| Spec cost / diagnostics | `SpecCostEstimate`, `SpecCostActual`, `SpecDiagnostic` | `DryRunResult` (fields 5-7 estimate, 8-10 actual), `ErrorDetail` |

`ListScenes` page tokens are opaque and server-issued; a token this server did not mint is rejected with `INVALID_ARGUMENT`. A zero or negative `page_size` returns the entire scene catalog (the catalog is a small bounded set), so `next_page_token` is empty in that case.

## Conformance

gRPC is a Honua-native surface (not OGC). Contract stability is tracked through the public-interface proof ledger; HTTP standards status lives in the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Run geoprocessing](../../guides/query-analyze/run-geoprocessing.md)
- [React to changes](../../guides/edit/react-to-changes.md)
- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md)
