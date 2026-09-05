# React to feature changes

Receive an event for every feature insert, update, and delete — pushed to your endpoint as a signed webhook or streamed live over WebSocket/SSE.

**Prerequisites:** A writable layer (see [Edit features](edit-features.md)). Streaming is a Preview surface and is disabled by default; enable `Capabilities:Experimental:realtime.feature-streams:Enabled=true` in addition to the required license. Webhook configuration requires restart access to the server's environment; replay and session admin require an admin API key.

Every write — FeatureServer, OGC API Features, OData (including `$batch`), WFS 2.0 transactions, and gRPC `ApplyEdits` — publishes one normalized envelope: `eventId`, `timestamp`, `serviceId`, `layerId`, `objectId`, `operation` (`insert`|`update`|`delete`), `protocol`, `requestId`, plus geometry/attributes when available. Delivery is at-least-once on every channel; always dedupe on `eventId`.

## Steps

### 1. Configure a webhook destination

Webhooks are configured through server settings (environment variables), not the admin API:

```bash
FeatureChangeEvents__Webhook__Enabled=true
FeatureChangeEvents__Webhook__Url=https://hooks.example.com/honua
FeatureChangeEvents__Webhook__Secret=$WEBHOOK_HMAC_SECRET
FeatureChangeEvents__Webhook__MaxAttempts=5
```

The URL must be HTTPS, carry no embedded credentials, and resolve to a public address — private/loopback targets are rejected at startup. Retries use exponential backoff (`InitialBackoffMs`/`MaxBackoffMs`/`RequestTimeoutSeconds` tune it).

### 2. Verify webhook signatures in your receiver

Each delivery carries `X-Honua-Event-Id`, `X-Honua-Event-Timestamp` (unix seconds), `Idempotency-Key` (= the event id), and `X-Honua-Signature: sha256=<hex>` — an HMAC-SHA256 of `<timestamp>.<raw-json-body>` using your configured secret:

```bash
printf '%s.%s' "$TIMESTAMP" "$RAW_BODY" | openssl dgst -sha256 -hmac "$WEBHOOK_HMAC_SECRET"
```

### 3. Stream over SSE

> Use `createHonuaServerRealtimeSubscription` from `@honua/sdk-js/realtime` and subscribe with `layerId: 0`; the SDK handles the SSE endpoint and event decoding.

SSE subscriptions are fixed by query parameters (`serviceId`, `layers`, `bbox`, `filter` CQL2, `datetime`) at connect time; each `feature-change` event's SSE `id:` is a cursor, so `EventSource` reconnects resume automatically (or pass `cursor=` explicitly).

### 4. Stream over WebSocket with dynamic subscriptions

Connect a WebSocket to `GET /api/v1/streaming/features`, then send JSON control frames:

```json
{"type":"subscribe","subscriptionId":"alpha","layerId":0,"bbox":[-158,21,-157,22],"filter":"status = 'active'","cursor":1234}
```

One socket carries many subscriptions; the server answers with `status`, `heartbeat`, `error`, and `feature-change` frames, and `cursor` triggers replay of missed events before live delivery. Unfiltered all-layer streams require admin access; everyone else subscribes to explicit service or layer scopes.

### 5. Start from a baseline (snapshot-then-delta)

Add `mode=snapshot` (streamed framing) or `mode=snapshot-then-delta` (batched framing) to the SSE/WebSocket query string, or the same value to a WebSocket `subscribe` frame, to receive a complete baseline before any live mutation. The subscription must carry an explicit layer scope (`layers=`, or `layerId`/`layers` on the control frame).

Both framings share every semantic that matters — boundary cursor, replacement-snapshot reasons, truncation reporting, and delta resumption. Pick on baseline size:

- **`mode=snapshot`** streams the baseline as `snapshot-begin`, one `snapshot-feature` per matching feature, then `snapshot-end`. Each frame consumes one `sequence`, so neither side buffers a large baseline whole.
- **`mode=snapshot-then-delta`** batches the whole baseline into a single `snapshot` frame carrying a `features` array, consuming exactly one `sequence` — the vocabulary the `@honua/sdk-js` realtime client reduces natively.

Streamed framing:

```json
{"type":"snapshot-begin","snapshotId":"9f2…","subscriptionId":"alpha","sequence":0,"cursor":4821,"reason":"initial","layerIds":[0]}
{"type":"snapshot-feature","snapshotId":"9f2…","sequence":1,"cursor":4821,"layerId":0,"objectId":17,"geometry":{…},"geometryCrs":"EPSG:4326","attributes":{…}}
{"type":"snapshot-end","snapshotId":"9f2…","sequence":2,"cursor":4821,"featureCount":1,"complete":true}
```

- **`sequence` is the subscription-local position.** It starts at 0 and advances by exactly one for every snapshot or delta frame this subscription admits, so it stays contiguous even though `cursor` skips values belonging to events your filter rejected. Use `sequence` for gap detection and `cursor` only for replay/resume.
- **`cursor` on the snapshot frames is the delta boundary.** It is captured before the baseline read starts, so no mutation can slip between the two. A mutation that commits just before the boundary can appear in both the baseline and the delta stream — delta envelopes carry the full post-mutation attributes, so applying them in `sequence` order over the baseline is idempotent.
- **`reason`** is `initial` for a fresh subscription, or `cursor-expired` / `cursor-invalid` when you reconnected with a `cursor` the server can no longer replay from. In those cases the server sends a **replacement snapshot** instead of silently continuing with deltas.
- **`complete: false`** means the baseline hit `FeatureStreaming__MaxSnapshotFeatures` (default 5000), `FeatureStreaming__MaxSnapshotScanRows` (default 20000), or `FeatureStreaming__MaxSnapshotBytes` (default 4 MiB). Do not treat a truncated baseline as authoritative state — narrow the subscription scope or raise the bounds. All three are advertised on `/api/v1/streaming/features/capabilities`, and the stream sends a terminating `status: error` frame naming which bound it hit. The byte budget is the one a large-geometry layer reaches first, and it is what keeps a baseline deliverable through a response path that buffers before returning (API gateways and serverless invoke responses are commonly capped at 6 MB).
- **An unservable snapshot is a typed problem, not a dead stream.** If a layer in the subscription scope has left the catalog, or its backing store will not accept the baseline read, the request is refused with an RFC 7807 `503` problem document naming the condition — before the stream is opened, since a committed stream can no longer carry a status code.
- **On SSE, only `snapshot-end` carries an `id:`.** A baseline becomes a resumable cursor only once it is whole, so a connection that drops mid-baseline reconnects (via `Last-Event-ID`) into another snapshot rather than a delta tail with a half-applied baseline.
- **A replay-window gap ends the stream before the first post-gap delta is emitted.** Reconnect with the last complete cursor; snapshot mode then returns a replacement snapshot instead of letting a retention race leave stale state marked current.

Reconnecting with a still-replayable `cursor` continues with deltas and no snapshot, so steady-state reconnects stay cheap.

Batched framing (`mode=snapshot-then-delta`) delivers the same baseline as one frame:

```json
{"type":"snapshot","snapshotId":"9f2…","subscriptionId":"alpha","sequence":0,"cursor":4821,"reason":"initial",
 "replace":true,"layerIds":[0],"featureCount":1,"complete":true,
 "features":[{"id":"17","sourceId":"places","layerId":0,"geometryCrs":"EPSG:4326",
              "feature":{"type":"Feature","id":"17","geometry":{…},"properties":{…}}}]}
```

`replace` is always `true` — a Honua baseline replaces the client's live set rather than merging into it. Each entry's `id` and `sourceId` match the delta envelope's `featureId` and `serviceId` exactly, so the baseline record and its later deltas key identically, and `geometry`/`properties` are always present (written even when null). The first delta continues at `sequence: 1`.

### 6. Drive a controlled mutation (conformance only)

Deployments that provision a dedicated conformance source advertise it in the `conformance` block of `/api/v1/streaming/features/capabilities`. It is **off by default**, and when off no caller can reach the mutation surface however authorized. It exists so a scheduled conformance runner can produce a correlated mutation on a live deployment without ever touching demo or user records.

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `POST /api/v1/streaming/conformance/runs`.

The workflow is three requests, and the third belongs in a `finally` block:

1. **Lease a run.** `POST /api/v1/streaming/conformance/runs` with an optional `clientLabel`, `expectedDeploymentRevision`, and `expectedServiceId`. A supplied expectation that does not match this deployment answers `409` rather than running against the wrong target. The response carries `runId`, a one-time `runToken`, the `runMarker` written to your records, the `serviceId`/`layerId`/`runIdField` in play, `expiresAt`, and `baselineDigest`.
2. **Mutate.** `POST /api/v1/streaming/conformance/runs/{runId}/mutations` with `{"operation":"insert","label":"nightly"}`, carrying the run token in `X-Honua-Conformance-Run-Token`. `touch` rewrites a record with its current values: the state does not change but the canonical edit pipeline still publishes an event, so two subscriptions opened at different times observe the same baseline and the same mutation.
3. **Release.** `DELETE /api/v1/streaming/conformance/runs/{runId}` with the same token. The response reports `deletedRecords`, the post-cleanup `baselineDigest`, and `baselineRestored`.

- Operations are `insert`, `update`, `touch`, and `delete`. All but `insert` take the `objectId` of a record **this run owns**; anything else answers `404`.
- Ownership is re-read from the record's stored `runIdField` marker before every mutation and every delete, so two concurrent runs cannot claim or destroy each other's records even holding the same credential.
- The `baselineDigest` covers every record no run owns. A cleanup digest equal to the lease digest proves the run left the source exactly as it found it.
- A run that never releases is swept once its lease TTL expires — the marker carries its own deadline, so the sweep works after the runner process dies.
- `POST /api/v1/admin/streaming/conformance/reset` (admin) drops every lease and deletes every controlled record.

### 7. Replay missed events (recovery)

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/feature-events/replay?limit=100`.

Page with `cursor` (and optional `from`/`to` ISO 8601 bounds, `limit` 1–1000): process each event idempotently, persist the returned `nextCursor`, repeat while `hasMore=true`. Retention is in-memory and capped by `FeatureChangeEvents__MaxRetainedEvents` (default 20000); on PostgreSQL a transactional outbox makes publication atomic with the row mutation.

### 8. Manage live sessions (admin)

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/streaming/features/sessions`.

`DELETE /api/v1/admin/streaming/features/sessions/{sessionId}` force-disconnects one.

## Verify

> Open `/api/v1/streaming/features/capabilities` in a browser.

```json
{ "enabled": true, "transports": ["websocket", "sse"],
  "modes": ["delta", "snapshot", "snapshot-then-delta"], "subscriptionSequence": true,
  "serverVersion": "1.0.0", "deploymentRevision": "sha256:…", "serverRevision": "sha256:…",
  "deploymentRevisionSource": "image-digest",
  "conformance": { "enabled": false },
  "replaySupported": true, "layers": [ { "layerId": 0, "canSubscribe": true } ] }
```

Then insert a feature in another terminal ([Edit features](edit-features.md)) and watch the `feature-change` event arrive on your open SSE stream.

## Troubleshoot

| Symptom | Fix |
|---|---|
| `403` before the stream connects | Community edition fails closed; streaming needs Pro or Enterprise. Capabilities reports `enabled=false`. |
| `401` on an unfiltered stream | All-layer streams require admin. Subscribe with explicit `layers=` / `serviceId=` scopes instead. |
| `503` on connect | The tenant or principal has exhausted `FeatureStreaming__MaxConcurrentSessions`; free sessions in that partition or raise the configured cap applied to every partition. Other tenants and principals retain their own admission capacity. |
| Webhook never fires | The URL must be HTTPS and publicly resolvable — private, loopback, or unresolvable addresses are rejected. Check startup logs and your receiver's signature validation. |
| Duplicate events | Expected: delivery is at-least-once (and once per matching subscription). Dedupe on `eventId` (or `(subscriptionId, eventId)` for multi-subscription sockets). |
| `400` on `mode=snapshot` | Snapshot subscriptions need an explicit layer scope so the baseline read stays bounded. Add `layers=` (or `layerId` on the control frame). |
| `deploymentRevision` is `null` | The deployment carries no verifiable revision. Set `Deployment__ImageDigest` (preferred) or `Deployment__Revision`/`HONUA_GIT_SHA`. Malformed values are rejected rather than echoed. The same value is published as `serverRevision` for clients that read that name. |
| `403` from `/streaming/conformance/runs` | This deployment provisions no conformance source. Set `FeatureStreaming__Conformance__Enabled` and point `ServiceId`/`LayerId` at a dedicated source. |
| `503` from `/streaming/conformance/runs` | Either the configured conformance source does not resolve to a writable layer carrying the marker field, or the deployment reports no immutable revision (evidence could not be bound to it). |
| `409` from `/streaming/conformance/runs` | Every lease is held (raise `MaxConcurrentRuns`, or wait for the TTL), or the caller's expected revision/source does not match this deployment. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Edit features](edit-features.md)
- [Monitoring](../deploy/monitoring.md)

### Connection admission

`FeatureStreaming__MaxConcurrentSessions` limits simultaneous SSE and WebSocket
connections per effective tenant on each server process. All principals in a tenant
share that tenant's limit. When tenancy is disabled or there is no effective tenant,
the limit applies per authenticated principal, using the same scheme- and
issuer-qualified identity as other security boundaries. Anonymous connections share
one partition. Changing `clientLabel`, transport, or subscription count does not
create additional capacity.

A full partition receives HTTP `503` and a problem document describing the session
limit before the SSE stream or WebSocket handshake opens. Disconnecting a session
returns capacity only to its own partition. The cap does not impose a second global
limit, and it is not a distributed cluster quota. The streaming health check retains
the total active-session count and reports saturation using the largest partition,
without exposing tenant or principal identifiers.
