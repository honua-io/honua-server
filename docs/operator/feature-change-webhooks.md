# Feature Change Webhooks

This runbook covers operational setup for feature-change notifications and replay.

## Overview

Honua emits normalized feature-write events for:

- FeatureServer edits (`applyEdits`, `addFeatures`, `updateFeatures`, `deleteFeatures`)
- gRPC `FeatureService.ApplyEdits` (Adds / Updates / Deletes batches)
- OGC API Features writes (Create, Replace, Patch, Delete, batch)
- WFS 2.0 `Transaction` (Insert, Replace, Update, Delete)
- OData writes (including successful `$batch` mutations)

Each event envelope includes:

- `eventId`
- `timestamp`
- `serviceId`
- `layerId`
- `objectId`
- `operation` (`insert`, `update`, `delete`) — producers may use `create` internally;
  the canonical event boundary normalizes to `insert` so consumers always receive the
  values listed here.
- `protocol`
- `requestId`

Delivery is at-least-once. Consumers must treat `eventId` as the idempotency key.

## Configuration

Configure via `FeatureChangeEvents`:

- `FeatureChangeEvents:MaxRetainedEvents` in-memory replay retention (default `20000`)
- `FeatureChangeEvents:Webhook:Enabled` enable outbound webhook delivery
- `FeatureChangeEvents:Webhook:Url` destination URL
- `FeatureChangeEvents:Webhook:Secret` HMAC secret
- `FeatureChangeEvents:Webhook:MaxAttempts` retries (default `5`)
- `FeatureChangeEvents:Webhook:InitialBackoffMs` exponential backoff base (default `500`)
- `FeatureChangeEvents:Webhook:MaxBackoffMs` max backoff (default `30000`)
- `FeatureChangeEvents:Webhook:RequestTimeoutSeconds` per-attempt timeout (default `15`)

## Transactional Outbox

Mutations on capable backends record event intent atomically with the row mutation
in `honua.feature_change_outbox` (migration `024_CreateFeatureChangeOutbox.sql`). A
multi-node-safe dispatcher claims rows with `SELECT ... FOR UPDATE SKIP LOCKED` and
republishes them through the same canonical event publisher consumed by replay,
streaming, and webhook delivery.

When the outbox is active the protocol-layer post-commit publish becomes a no-op:
the dispatcher owns delivery, so envelopes are never published twice. On
non-capable backends the existing post-commit publish + Redis retry queue
remain in place unchanged (best-effort durability with a small loss window if
the process crashes between commit and append).

Row state machine: `pending` → `claimed` → `dispatched` | `failed` → `claimed`
(retry) | `dead_lettered`. `outbox_id` (the row's primary key) is internal;
consumers continue to dedupe on the canonical `eventId` carried in the published
envelope, which is reused across dispatcher retries.

The persisted row's `operation` column reflects the producer-side vocabulary
(`create`, `update`, `delete`); `InMemoryFeatureChangeEventStore.AppendAsync`
normalizes `create` to `insert` at the canonical event boundary so webhook and
replay consumers always see the canonical values listed above. The outbox table
itself is the only place an operator querying directly will see `create`.

Durability guarantees:

- **Strict publish.** The dispatcher uses `IFeatureChangeEventPublisher.PublishStrictAsync`,
  which surfaces durable-store append failures instead of silently swapping them
  for a best-effort retry-queue enqueue. A failed publish leaves the outbox row
  in `claimed`/`failed` and the next pass re-dispatches it; the durability
  guarantee never silently transfers from the multi-node-safe outbox to the
  in-process retry queue.
- **Claim-owner-bound terminal updates.** `MarkDispatchedAsync` and `MarkFailedAsync`
  filter on `status='claimed' AND claim_node_id=@owner`, so a stalled worker whose
  lease was reset by `RecoverExpiredClaimsAsync` and re-claimed elsewhere cannot
  overwrite the new owner's terminal state. Stale-claim outcomes are logged at
  Information level and skipped without inflating dispatch/failure counters.
- **Mutation-time event timestamps.** `EventPayload.Timestamp` and the row's
  `created_at` are pinned to mutation time (captured once in `BuildEntry`) so a
  delayed or retried dispatch publishes the same timestamp as the inline path.
  Replay `from`/`to` filtering uses `FeatureChangeEvent.Timestamp` and therefore
  remains correct for rows that linger in the outbox before delivery.
- **Atomic outbox write on non-rollback batches.** GeoServices `applyEdits` and
  WFS transactions with `RollbackOnFailure=false` previously autocommitted the
  row mutation and appended the outbox row from a separate connection. Each
  row's mutation + outbox INSERT now runs inside a per-row transaction so a
  crash between commit and append cannot leave a committed feature row without
  its CDC envelope. Batch-transactional and outbox-inactive paths still use
  the autocommit fast path.
- **Per-row request and geometry-intent correlation.** Atomic batches (OData
  `$batch` atomic groups, OGC Features Transactions, GeoServices `applyEdits`,
  gRPC `ApplyEdits`, WFS 2.0 transactions) thread per-row `requestId` and
  `geometryChanged` queues into the outbox scope so each emitted envelope
  carries the same correlation the inline post-commit publish would have used.
  `geometryChanged` is sourced from the originating request intent rather than
  inferred from the post-mutation snapshot, so PATCH-style updates that
  preserve existing geometry report `geometryChanged=false`. Replace-style
  operations (OData PUT, OGC Features Replace and batch updates, WFS 2.0
  Replace) report `geometryChanged=true` whenever the operation either supplies
  a new geometry or overwrites an existing non-null geometry — including the
  body-less Replace that clears existing geometry. The only no-change case for
  a Replace is null-to-null, which stays `false`.

Backend capability:

- **PostgreSQL**: full transactional outbox.
- **SQL Server**: read-only in current slice; no outbox required (capability provider
  reports the limitation at startup).
- **DuckDB**: read-only; no outbox required (capability provider reports the
  limitation at startup).

Tune via `FeatureChangeEvents:Outbox`:

- `BatchSize` rows per dispatch pass (default `32`)
- `IdlePollIntervalMs` sleep when batch is empty (default `1000`)
- `ClaimTtlSeconds` lease before another node may reclaim (default `30`)
- `RecoveryIntervalSeconds` cadence for resetting expired claims (default `30`)
- `MaxRetries` attempts before dead-letter (default `5`)
- `DegradedBacklogThreshold` rows above which readiness reports `Degraded` (default `1000`)
- `UnhealthyDeadLetterThreshold` dead-lettered rows that flip readiness to `Unhealthy` (default `1`)

All seven settings are validated at startup; a non-positive value fails the
host with an explicit message rather than silently disabling the dispatcher.

Operational signals:

- `honua.outbox.dispatched_total`, `honua.outbox.failed_total`, `honua.outbox.dead_lettered_total`
- `honua.outbox.pending_count`, `honua.outbox.dead_lettered_count`, `honua.outbox.oldest_pending_age_seconds`
- `honua.outbox.recovered_claims_total`
- Health check `feature-change-outbox` on `/healthz/ready` (tags `outbox`, `events`).
  Reports `Healthy` when the dispatcher is running and the backlog is below
  `DegradedBacklogThreshold`, `Degraded` once the backlog crosses that threshold,
  and `Unhealthy` when dead-lettered rows reach `UnhealthyDeadLetterThreshold`
  or the dispatcher is not running. Dead-letter `Unhealthy` is evaluated before
  the storage-poll branch, so a known dead-letter snapshot stays `Unhealthy`
  even when the latest claim/recovery/backlog query happened to fail — the
  triage signal outranks the transient storage-poll signal. On non-capable
  providers the check stays `Healthy` and surfaces the capability limitation
  as its description so smoke probes do not flap.
- Storage-poll failure surfacing. The dispatcher tracks each storage kind
  independently (`claim`, `recovery`, `backlog`) and exposes
  `IsStoragePollFailing` plus a per-kind success/failure timestamp pair on
  `IOutboxHealth`. Per-kind tracking is required because a successful backlog
  refresh after a failed claim must NOT clear the still-failing claim — a
  single shared timestamp pair would let the later success mask the earlier
  failure even though no rows are being dispatched. Whenever any kind has a
  failure timestamp newer than its own most recent success (or no success at
  all), the readiness probe returns `Unhealthy` if no backlog snapshot has
  been captured (cold-start failure, e.g. missing table or permissions) and
  `Degraded` when a prior pass had succeeded but the latest poll failed (the
  cached backlog snapshot may be stale). A subsequent successful pass on the
  same kind naturally clears the flag without operator intervention. The probe
  payload includes per-kind `last_<kind>_poll_success_at` /
  `last_<kind>_poll_failure_at` plus aggregate `last_storage_failure_at` and
  `last_successful_poll_at` so dashboards and alerts can correlate the
  transition with downstream symptoms.

## Webhook Signature

For each delivery Honua sends:

- `X-Honua-Event-Id: <eventId>`
- `X-Honua-Event-Timestamp: <unix-seconds>`
- `X-Honua-Signature: sha256=<hex-hmac>`
- `Idempotency-Key: <eventId>`

Signature payload is:

`<timestamp>.<raw-json-body>`

HMAC algorithm: `HMAC-SHA256` using `FeatureChangeEvents:Webhook:Secret`.

## Replay / Recovery

Use admin replay endpoint:

`GET /api/v1/admin/feature-events/replay`

Query parameters:

- `cursor` return events after cursor
- `from` / `to` ISO8601 time window
- `limit` max events (1..1000)

Typical recovery loop:

1. Load last durable cursor from consumer state.
2. Call replay with `cursor` and `limit`.
3. Process each event idempotently keyed by `eventId`.
4. Persist returned `nextCursor`.
5. Repeat while `hasMore=true`.
