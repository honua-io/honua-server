---
type: reference
title: "gRPC"
description: "Honua hosts a versioned gRPC surface from the `Geospatial.Grpc` protocol package (geospatial.v1 protobuf package, Geospatial.V1 .NET namespace) for high-throughput feature, geoprocessing, spec, and scene access."
resource: "honua://capability/serve.grpc"
---
# gRPC

Honua hosts a versioned gRPC surface from the [`Geospatial.Grpc`](https://github.com/honua-io/geospatial-grpc) protocol package (`geospatial.v1` protobuf package, `Geospatial.V1` .NET namespace) for high-throughput feature, geoprocessing, spec, and scene access.

## .NET package installation

gRPC remains **supported for 2026.1**. `Geospatial.Grpc` is published to
[nuget.org](https://www.nuget.org/packages/Geospatial.Grpc/) and installs
anonymously:

```bash
dotnet add package Geospatial.Grpc
```

No feed configuration, package-source mapping, or token is needed. Pin the exact
version certified for your server release if you need reproducibility; the
server's own dependency pin is described under [versioning](#versioning-and-deprecation-policy)
below.

The producer attests tag-built package bytes before publishing. Verify a
downloaded archive with:

```bash
gh attestation verify Geospatial.Grpc.X.Y.Z.nupkg --repo honua-io/geospatial-grpc
```

Installing this package does not upgrade the server dependency or expand its
certified operations.

## Connection

| Item | Value |
| --- | --- |
| Port | 8081, HTTP/2 cleartext (h2c) — `Kestrel:Endpoints:Grpc:Url = http://+:8081` (Docker images expose 8080 HTTP + 8081 gRPC; `HONUA_GRPC_PORT` in docker-compose). |
| Health | gRPC health checks enabled (`grpc.health.v1.Health`). |
| Reflection | Server reflection enabled in Development only; production clients should use the published protobuf descriptors. |
| Compression | gzip response compression negotiated by default. |
| Message size | 16 MiB send/receive default; `Grpc:MaxReceiveMessageSize` / `Grpc:MaxSendMessageSize` to override. |
| Streaming page size | `Grpc:StreamBatchSize` (default 1000 features per message). |

## Services (geospatial.v1)

| Service | RPCs | Purpose |
| --- | --- | --- |
| `FeatureService` | `DescribeService`, `DescribeLayer`, `QueryFeatures`, `QueryFeaturesStream` (server streaming), `ApplyEdits`, `ListAttachments`, `GetAttachmentUrl`, `UploadAttachment`, `BeginAttachmentUpload`, `UploadAttachmentChunk`, `CommitAttachmentUpload`, `AbortAttachmentUpload`, `DeleteAttachment` | Feature query and edits; respects per-service protocol enablement and access policy. |
| `ProcessService` | `ValidatePlan`, `DryRunPlan`, `ExecutePlan`, `ExecutePlanStream` (server streaming), `SubmitJob`, `GetJob`, `GetJobResult`, `CancelJob` | Canonical geoprocessing runtime (same backend as GPServer and OGC API Processes). |
| `SpecService` | `PlanSpec`, `ApplySpec` (server streaming), `CancelApply` | Spec plan/apply engine. |
| `SceneService` | `ListScenes`, `GetScene` | Scene dataset discovery. |
| `TileService` | `GetTile`, `StreamTiles` (server streaming) | Tile retrieval. |
| `ElevationService` | `GetElevation`, `GetElevationProfile` | Elevation point and profile queries. |

```bash
# In Development, list services and call a query via reflection
grpcurl -plaintext server.example.com:8081 list
grpcurl -plaintext -d '{"serviceId":"roads","layerId":0,"where":"1=1"}' \
  server.example.com:8081 geospatial.v1.FeatureService/QueryFeatures
```

## Native metadata discovery

Use unary `DescribeService` followed by `DescribeLayer` to discover the feature
surface over gRPC or gRPC-Web. Neither operation reads feature rows or needs a
GeoServices REST metadata request. The descriptors come from the same canonical
metadata snapshot and authorization policy as feature access.

`DescribeService` lists routable layers visible to the caller in ascending public
layer-id order. `page_size` defaults to 100 and accepts 1–1000; follow
`next_page_token` until empty. Tokens are bound to the service and snapshot
revision. Invalid or stale tokens return `INVALID_ARGUMENT`; each page evaluates
access again. Service capabilities are the union of its visible layers; check the
individual layer before enabling an operation.

`DescribeLayer` returns field definitions, defaults, coded/range domains,
subtypes and creation templates, relationships to accessible layers, spatial and
time metadata, editing tracking fields, and effective capabilities. Field masks
also apply to defaults, templates, key-field references and relationship keys.
The server omits facts absent from canonical metadata, including unknown time
zones and tracking timestamps. An absent default differs from an explicit null. Structured defaults are encoded
as JSON in the default value's string branch.

Managed edit capabilities require a writable storage binding, declared publication
capability and caller authorization. Unsupported operations remain false;
attachment editing additionally requires explicit attachment enablement. Clients must
respect these flags and surface unsupported operations explicitly.

## Attachments over gRPC-Web

All attachment RPCs are unary, so browsers can use gRPC-Web without client
streaming. Resolve the service and layer with native metadata, then supply an
`AttachmentParent` with the public layer identifier and either the feature's
object ID or global ID. Every call checks current layer access and feature
visibility. Editing also checks the declared edit capability, attachment
configuration, caller authorization and ownership policy.

For content up to 1 MiB, call `UploadAttachment` with the bytes, total size,
SHA-256 digest, name, content type and an idempotency key. Larger files use
`BeginAttachmentUpload`, repeated `UploadAttachmentChunk` calls, and
`CommitAttachmentUpload`. Each chunk carries its offset and SHA-256 digest;
use the returned `next_offset_bytes` and `max_chunk_bytes` to resume. Chunks
are at most 1 MiB. Upload sessions and idempotency receipts last 24 hours.
An identical retry returns the accepted result; a conflicting chunk returns
`FAILED_PRECONDITION`. Commit verifies the complete size, digest and configured
content policy before publication. `AbortAttachmentUpload` releases an
unfinished upload. Configured total-size and allowed-content-type limits apply
to both upload paths.

`ListAttachments` returns native descriptors, including keywords, stable global
IDs and SHA-256 digests when available. Legacy attachments can lack these native
identity and digest fields. Page size defaults to 100, with a maximum of 500;
follow `next_page_token` with the same parent and filters. `GetAttachmentUrl`
returns an HTTPS signed download URL valid for five minutes. Download the content
from that URL; descriptors never expose storage paths. `DeleteAttachment` uses an
idempotency key and rechecks edit access before applying or replaying a deletion.

Session calls remain bound to the initiating caller and published layer. They
recheck authorization and feature visibility rather than treating possession of
a session ID as permission. Idempotency keys are required for upload and delete,
with a maximum of 256 UTF-8 bytes. Keep the same key and metadata when retrying.

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
- `order_by` fields are resolved against the layer schema, so attributes declared
  as numeric, date, or time sort by value rather than as text, matching the REST
  adapters. Fields the schema does not declare keep text ordering.
- `out_statistics` and `group_by` are rejected with `INVALID_ARGUMENT`.
  `geospatial.v1` defines no aggregate result shape, so the server refuses the
  request rather than returning ordinary features. Use the GeoServices FeatureServer
  `query` parameters `outStatistics` and `groupByFieldsForStatistics` for aggregates.
- Metadata-fallback contract: if a client is pinned to an older `Geospatial.Grpc`
  package whose `geospatial.v1` messages predate one of these fields, that field
  is absent (proto3 implicit default) and the client should fall back to the
  native `DescribeLayer` RPC for the missing descriptor. Current `geospatial.v1`
  carries all four fields.

## Versioning and deprecation policy

Every protobuf package carries a major-version suffix (`geospatial.v1`, future `geospatial.v2`); a service lives under exactly one major version, and versions are hosted side by side.

Within a major version the wire contract is frozen:

- Never remove, renumber, or change the wire type of a shipped field; never change `oneof` membership, implicit defaults, or a method's streaming direction.
- Changes are additive only: new fields with new tag numbers, new enum values, new messages, new RPCs.
- Retiring fields are marked `[deprecated = true]` (surfaced as `[Obsolete]` in .NET) for at least one minor release; fully retired tags and names become `reserved`.
- Status codes for established failure modes are part of the contract.

Breaking changes require a new major version introduced alongside the old one: open an interface ADR, add `proto/geospatial/v2/` in parallel, host both service versions, announce `v1` deprecation when `v2` ships, and keep `v1` functional for at least two minor releases (longer if contracted clients depend on it) before removal.

CI enforcement: protobuf changes in the `Geospatial.Grpc` package must pass a breaking-change linter (`buf breaking`) against the latest release tag, and the server pins a specific package version so consumer breaks fail the build.

The server pins `Geospatial.Grpc` **1.3.0**, which is on
[nuget.org](https://www.nuget.org/packages/Geospatial.Grpc/) and restores
anonymously.

### The freeze is binding

`1.0.0` promotes the `geospatial.v1` schema settled in `0.2.0-alpha.1` to the
permanent v1 compatibility contract in
[`VERSIONING.md`](https://github.com/honua-io/geospatial-grpc/blob/trunk/VERSIONING.md),
with no wire-surface change from that baseline. The Pre-1.0 Exception that
permitted a coordinated structural break inside `geospatial.v1` while the package
major version was `0.x` therefore no longer applies: the freeze described above
is binding, and bindings generated from `1.0.0` stay valid.

Two such breaks were exercised before the freeze. `0.2.0-alpha.1` promoted the duplicated job-lifecycle control-plane messages to `execution_types.proto` and converged `SpecService` onto them, and `0.1.0-alpha.3` unified the severity enums and widened feature-query pagination. The protobuf package stayed `geospatial.v1`, so no client support floor moved, but callers built against `0.1.x` bindings must regenerate. The changes that alter what a client reads:

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
