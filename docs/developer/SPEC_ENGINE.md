# Spec Plan / Apply Engine

The spec engine executes canonical spec documents with Terraform-style
**plan / apply** semantics. `plan` compiles a spec into a DAG with per-node
cost estimates and structured warnings. `apply` streams per-node progress
events and serves cache hits without re-invoking the compute backend.

The engine ships as a mixed REST + gRPC surface:

- REST: `POST /v1/spec/plan`, `POST /v1/spec/apply`, `POST /v1/spec/cancel`,
  `GET /v1/spec/artifact/{hash}`
- gRPC: `geospatial.v1.SpecService/{PlanSpec, ApplySpec, CancelApply}`
  (`src/Honua.Core/Transport/Proto/geospatial/v1/spec_service.proto`)

Both transports share the same orchestrator; apply events (`SpecApplyEvent`)
are identical on SSE and gRPC server streaming.

## Canonical document envelope

The request body for `plan` and `apply` is the same canonical shape produced by
the grammar / validator pipeline. Top-level fields:

| Field | Required | Notes |
|---|---|---|
| `grammarVersion` | yes | Participates in the content-hash cache key. Missing values hard-fail with `version-skew`. |
| `processFamilyVersion` | yes | Participates in the cache key. |
| `specId` | no | Surfaced in telemetry and apply-event correlation only; never part of the cache key. |
| `nodes` | yes | Author-order node list. Cycles and duplicate ids hard-fail. |

Each `nodes[*]` entry:

| Field | Notes |
|---|---|
| `id` | Stable node identifier (unique within the document). |
| `kind` | Required. One of `compute`, `report`, `dataset`, `service`, `app`. Omitted values hard-fail with `unknown-kind`. In S1 only `compute` / `report` are executable; `dataset` / `service` / `app` apply-calls hard-fail with `spec-kind-not-in-s1`. |
| `op` | Operator identifier (e.g. `compute.buffer`). Null only for pure sources. |
| `inputs` | Parameter → canonical token (e.g. `@other-node`, layer ref, scalar literal). |
| `parameters` | Flat parameter bag captured from the spec body. |
| `canonicalFragment` | Canonicalised JSON fragment used as the hash input. When omitted the engine hashes sorted `parameters` + `sourcePins` instead. |
| `sourcePins` | Optional declared source pins. When absent, mutable sources emit `mutable-source-no-pin`. |
| `nondeterministic` | Surfaces `nondeterministic-op` during plan. |

## POST /v1/spec/plan

Returns the compiled DAG. The handler reads only catalog and metadata; no
process family is invoked and no artifacts are written.

```bash
curl -X POST http://localhost:8080/v1/spec/plan \
  -H "Content-Type: application/json" \
  -d '{
    "grammarVersion": "1.0.0",
    "processFamilyVersion": "2026.4",
    "nodes": [
      { "id": "parks", "kind": "compute", "op": "source.layer",
        "parameters": { "layerId": "42" } },
      { "id": "buffered", "kind": "compute", "op": "compute.buffer",
        "inputs": { "source": "@parks" },
        "parameters": { "distanceMeters": "100" } }
    ]
  }'
```

Response:

```json
{
  "planId": "9b0d3c0e-...",
  "grammarVersion": "1.0.0",
  "processFamilyVersion": "2026.4",
  "nodes": [
    {
      "nodeId": "parks",
      "kind": "Compute",
      "op": "source.layer",
      "dependsOn": [],
      "contentHash": "f12a...",
      "cost": { "estimatedRows": 1200, "estimatedBytes": 482000, "estimatedDurationMs": 45 },
      "warnings": []
    },
    {
      "nodeId": "buffered",
      "kind": "Compute",
      "op": "compute.buffer",
      "dependsOn": ["parks"],
      "contentHash": "a31d...",
      "cost": { "estimatedRows": 1200, "estimatedBytes": 960000, "estimatedDurationMs": 180 },
      "warnings": []
    }
  ],
  "warnings": []
}
```

Structural errors (cycles, duplicate ids, unresolved `@` references, unknown
kinds) are returned as `400 Bad Request` with a problem-details body whose
`code` is one of the stable diagnostic codes listed below.

## POST /v1/spec/apply

Streams per-node progress events as Server-Sent Events. Clients **must** send
`Accept: text/event-stream` — any other `Accept` returns `400 accept-required`.

```bash
curl -N -X POST http://localhost:8080/v1/spec/apply \
  -H "Accept: text/event-stream" \
  -H "Content-Type: application/json" \
  -d '{
    "grammarVersion": "1.0.0",
    "processFamilyVersion": "2026.4",
    "cacheMode": "ReadWrite",
    "maxConcurrency": 4,
    "nodes": [ /* ... */ ]
  }'
```

The apply token is returned via the `X-Spec-Apply-Token` response header
(REST) or the `x-spec-apply-token` trailer (gRPC).

Structural errors in the document (cycles, duplicate ids, unresolved `@`
references) are surfaced as `400 Bad Request` / `InvalidArgument` **before**
the SSE stream is opened — the response body is a `SpecProblem` envelope with
the matching stable `code`, never an event frame.

Malformed request bodies (parse failures) are reported as
`400 invalid-request-body` on both `POST /v1/spec/plan` and
`POST /v1/spec/apply` without opening a stream.

### Cancellation contract

Apply runs are owned by a server-side `CancellationTokenSource`, not the HTTP
request. Disconnecting the SSE stream or dropping the gRPC response stream
stops only the outbound writes; the run continues until it completes or until
`POST /v1/spec/cancel` / `SpecService.CancelApply` trips the registered token.
Operators that need to abort a run must call the cancel endpoint explicitly —
closing the socket alone has no effect.

### `cacheMode`

| Value | Behaviour |
|---|---|
| `ReadWrite` (default) | Reads from cache, writes successful outputs. Mutable-source nodes without a pin get the configured TTL stamped on write. |
| `ReadOnly` | Reads from cache but never writes new entries. Useful for dry runs against warm caches. **A miss is a terminal `Failed` event with `read-only-cache-miss` — the executor is not invoked and no synthetic hash is emitted.** |
| `Bypass` | Ignores the cache entirely and recomputes every node. Outputs are still hashed and written so downstream reads remain stable. |

### Event buffer policy

The event channel is bounded (256 frames, `DropOldest`). Apply runs outlive
the originating HTTP/gRPC request — a disconnected reader would otherwise
retain the full backlog until the run finishes. Clients detect drops via the
monotonic `sequence` field on every frame: a gap in sequence numbers means an
older frame was discarded. Readers that stay connected never see drops because
writers produce events at compute-bound rate.

### SSE frame shape

Each frame carries an `id` (monotonic `sequence`), an `event` name matching the
event kind, and a JSON body. Example:

```
id: 3
event: Succeeded
data: {"sequence":3,"kind":"Succeeded","applyToken":"8d...","nodeId":"buffered","contentHash":"a31d...","timestamp":"2026-04-20T10:22:05.120Z","actualCost":{"rows":1200,"bytes":960000,"durationMs":173.2}}
```

Event kinds:

| Kind | Emitted when |
|---|---|
| `ApplyStarted` | Run has begun; includes no node-level fields. |
| `Queued` | Node admitted to orchestration queue. |
| `Running` | Compute backend reported progress for the node. |
| `Cached` | Output was satisfied from the content-hash cache — no compute invocation. |
| `Succeeded` | Node wrote a fresh artifact; `actualCost` is populated. |
| `Failed` | Node failed; `diagnostic` carries the stable `code`. Downstream nodes receive `Skipped`. |
| `Skipped` | Deferred node. The `diagnostic.code` distinguishes the cause: `upstream-failed` when a parent failed, `apply-cancelled` when the run was cooperatively cancelled. |
| `Warning` | Non-terminal warning (structured diagnostic). |
| `ApplyCompleted` | Terminal; `summary` aggregates cached/ran/failed/skipped counts and wall-clock duration. |
| `ApplyCancelled` | Terminal after cooperative cancel; same summary shape with `cancelled: true`. |

## POST /v1/spec/cancel

Cooperatively cancels an in-flight apply run by token. Already-completed nodes
remain materialised in the cache because each node writes through on its own
success.

```bash
curl -X POST http://localhost:8080/v1/spec/cancel \
  -H "Content-Type: application/json" \
  -d '{"applyToken":"8d...bd"}'
```

Returns `200 OK` with `{ "applyToken": "...", "cancelled": true }`, or
`404 apply-token-unknown` when the token is not registered. The apply
registry is **in-process in S1**; tokens do not survive a server restart.

## GET /v1/spec/artifact/{hash}

Retrieves a cached artifact by its content hash. The response sets
`X-Spec-Content-Hash` and streams bytes with the artifact's declared content
type. `404 artifact-not-found` is returned when the hash is unknown or has
been evicted since the apply event referenced it.

## Cache identity

The cache key is derived — never caller-supplied:

```
sha256( grammarVersion || processFamilyVersion || nodeId || kind || op
        || canonicalFragment OR sorted(parameters + sourcePins)
        || sorted(input hashes) )
```

The derivation lives in
`src/Honua.Core/Features/Spec/Services/SpecContentHashCalculator.cs`.
Re-applying the same spec with unchanged input hashes completes with zero
compute invocations; mutating a single node invalidates only its transitive
closure.

## Structured diagnostic codes

All warnings and errors carry a stable `code` from `SpecDiagnosticCodes`.
Admin tooling keys off these strings:

| Code | Severity typical | Meaning |
|---|---|---|
| `crs-mismatch` | warning | Op expects projected CRS but input is geographic (or vice versa). |
| `missing-column` | warning | Spec references a column not present in the catalog. |
| `unknown-service` | warning | `@` reference does not resolve against the catalog. |
| `mutable-source-no-pin` | warning | Source is mutable and not pinned; cache degrades to TTL. |
| `rbac-out-of-scope` | warning | Operator principal cannot read one or more sources. |
| `version-skew` | error | Grammar or process-family version outside supported range, or absent. |
| `estimated-oversize` | warning | Estimated bytes exceed the configured threshold. |
| `nondeterministic-op` | warning | Op declared non-deterministic (sample-based, time-based, etc.). |
| `spec-kind-not-in-s1` | error | Encountered a `dataset` / `service` / `app` node during apply. Slot is reserved for a later stage. |
| `dag-cycle` | error | Spec declares a cycle. |
| `duplicate-node-id` | error | Two nodes share an id. |
| `unresolved-reference` | error | `@` reference points to a missing id. |
| `invalid-request-body` | error | Request body could not be parsed as a canonical spec document. |
| `apply-token-unknown` | error | Cancel targets a token the in-process registry does not know (usually after a restart). |
| `artifact-not-found` | error | Requested artifact hash is unknown or evicted. |
| `upstream-failed` | warning | A parent node failed and the current node was skipped. |
| `apply-cancelled` | warning | A deferred node was skipped because the apply was cancelled cooperatively. Also emitted on the terminal `ApplyCancelled` frame. |
| `read-only-cache-miss` | error | `ReadOnly` apply encountered a cache miss. The executor is intentionally not invoked and no synthetic hash is written. |
| `unknown-kind` | error | Node omits the `kind` field (REST) or sends `SPEC_RESOURCE_KIND_UNSPECIFIED` (gRPC). Rejected at the transport boundary so operator typos do not silently dispatch through the compute executor. |

## Telemetry

All signals hang off the shared `Honua` meter and activity source (see
`SpecTelemetry`):

- Activities: `honua.spec.plan`, `honua.spec.apply`, `honua.spec.node`
- Counters: `honua.spec.applies_started`, `honua.spec.applies_completed`,
  `honua.spec.nodes_processed` (tagged by outcome),
  `honua.spec.cache_lookups` (tagged by outcome)
- Histograms: `honua.spec.apply_duration_ms`, `honua.spec.node_duration_ms`

## S1 scope notes

- **Durable-kind slots**: `dataset` / `service` / `app` nodes are rejected at
  apply with `spec-kind-not-in-s1`. Plan returns them so clients can render
  the DAG; applying a document that contains them fails fast.
- **Apply token registry** is in-process. Cancellation does not survive
  server restart; `/v1/spec/cancel` returns `apply-token-unknown` in that
  case. Acceptable for minutes-long applies in S1; persistent tokens are a
  follow-on.
- **Mutable source TTL** defaults to 15 minutes (`SpecApplyOptions.MutableSourceTtl`)
  and is applied at cache-write time to any payload whose planning warnings
  include `mutable-source-no-pin`. Set to `null` to disable TTL degradation
  entirely. When tightened, operators should watch
  `honua.spec.cache_lookups{outcome="miss"}` for regressions.
- **Artifact backing**: cached artifacts flow through the existing
  `CloudFileStorageBase` stack (local / S3 / Azure Blob) under a
  `spec-cache/{hash}` prefix. No dedicated bucket per environment.
- **S1 compute ops** reuse existing geoprocessing implementations (`filter`,
  `spatial_join`, `buffer`, `reproject`, `zonal_stats`, `slope`). Additional
  ops ship with the grammar / validator ticket.

## Related documents

- [API Examples — Spec Engine](API_EXAMPLES.md#spec-planapply-engine)
- [Proto: `geospatial/v1/spec_service.proto`](../../src/Honua.Core/Transport/Proto/geospatial/v1/spec_service.proto)
- [Public Interface Proof Ledger](../gis/data/public-interface-proof.json) — `spec-engine` entry
