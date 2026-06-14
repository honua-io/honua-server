# Spec engine

The spec engine executes canonical spec documents with Terraform-style plan/apply semantics: `plan` compiles a spec into a DAG with per-node cost estimates and warnings; `apply` streams per-node progress events and serves cache hits without re-invoking compute. Specs are written in the spec grammar — see the [spec grammar v1.0 reference](../developer/spec-grammar/v1.0/README.md) (EBNF + JSON schema).

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/v1/spec/validate` | Parse and validate spec DSL text or canonical JSON; returns diagnostics and optionally the canonical JSON. |
| POST | `/v1/spec/plan` | Compile the DAG; returns per-node `contentHash`, `dependsOn`, cost estimates, warnings. Reads catalog/metadata only. |
| POST | `/v1/spec/apply` | Execute the DAG; streams per-node events as Server-Sent Events (requires `Accept: text/event-stream`). |
| POST | `/v1/spec/cancel` | Cooperatively cancel an in-flight apply by token. |
| GET | `/v1/spec/artifact/{hash}` | Fetch a cached artifact by content hash (`X-Spec-Content-Hash` response header). |

The same orchestrator is exposed over gRPC as `geospatial.v1.SpecService` (`PlanSpec`, `ApplySpec` server-streaming, `CancelApply`) — see the [gRPC reference](protocols/grpc.md).

## Request document

`plan` and `apply` take the canonical document shape produced by the validator:

| Field | Required | Notes |
| --- | --- | --- |
| `grammarVersion` | yes | Part of the cache key; missing values fail with `version-skew`. |
| `processFamilyVersion` | yes | Part of the cache key. |
| `nodes` | yes | Author-order node list; cycles and duplicate ids fail. |
| `specId` | no | Never part of the cache key. |

Each node carries `id`, `kind` (`compute`, `report`, `dataset`, `service`, `app`), `op`, `inputs` (`@node` references or literals), `parameters`, and optional `canonicalFragment`/`sourcePins`. Only `compute` and `report` are executable today; applying `dataset`/`service`/`app` nodes fails with `spec-kind-not-in-s1`.

## Apply behavior

- The apply token is returned in the `X-Spec-Apply-Token` response header (gRPC: `x-spec-apply-token` initial metadata), flushed before the first event so cancel can be issued mid-stream.
- Disconnecting the stream does **not** cancel the run; only `/v1/spec/cancel` (or `CancelApply`) does. Tokens are in-process and do not survive a restart (`apply-token-unknown` after restart).
- Event frames carry a monotonic `sequence`; the event buffer is bounded (256 frames, drop-oldest), so a gap in sequence numbers means frames were dropped for a slow reader.
- Event kinds: `ApplyStarted`, `Queued`, `Running`, `Cached`, `Succeeded`, `Failed`, `Skipped`, `Warning`, `ApplyCompleted`, `ApplyCancelled`.

### `cacheMode`

| Value | Behavior |
| --- | --- |
| `ReadWrite` (default) | Read from cache, write successful outputs. |
| `ReadOnly` | Read but never write; a miss is a terminal `Failed` event with `read-only-cache-miss`. |
| `Bypass` | Recompute every node; outputs are still hashed and written. |

Any other value is rejected with `unknown-cache-mode`. Cache keys are derived from grammar/process-family versions, node identity, parameters/pins, and upstream hashes — never caller-supplied. Re-applying an unchanged spec completes with zero compute invocations; mutating one node invalidates only its transitive closure.

## Diagnostic codes

All warnings and errors carry a stable `code`. Errors: `version-skew`, `dag-cycle`, `duplicate-node-id`, `unresolved-reference`, `invalid-node-id`, `invalid-request-body`, `unknown-kind`, `unknown-cache-mode`, `spec-kind-not-in-s1`, `apply-token-unknown`, `artifact-not-found`, `read-only-cache-miss`. Warnings: `crs-mismatch`, `missing-column`, `unknown-service`, `mutable-source-no-pin`, `rbac-out-of-scope`, `estimated-oversize`, `nondeterministic-op`, `upstream-failed`, `apply-cancelled`. Structural errors are returned as `400` problem details before any stream is opened.

## Example

```bash
curl -N -X POST http://localhost:8080/v1/spec/apply \
  -H "Accept: text/event-stream" -H "Content-Type: application/json" \
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

## Scope and limits (current stage)

- The artifact store is process-local and in-memory: cache state does not survive a restart and is not shared across replicas. Durable backing is a follow-on.
- The compute executor is a deterministic placeholder that emits a JSON payload describing each node and its resolved inputs; it proves content-hash identity, cache reuse, and event streaming end to end. Wiring the real geoprocessing process families is tracked separately.
- Mutable sources without a pin degrade to a TTL-stamped cache entry (default 15 minutes) and emit `mutable-source-no-pin`.

## Related pages

- [Spec grammar v1.0](../developer/spec-grammar/v1.0/README.md)
- [gRPC reference](protocols/grpc.md)
- [Geoprocessing operations](geoprocessing-operations.md)
