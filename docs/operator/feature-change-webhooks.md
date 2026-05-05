# Feature Change Webhooks

This runbook covers operational setup for feature-change notifications and replay.

## Overview

Honua emits normalized feature-write events for:

- FeatureServer edits (`applyEdits`, `addFeatures`, `updateFeatures`, `deleteFeatures`)
- OGC Features writes
- OData writes (including successful `$batch` mutations)

Each event envelope includes:

- `eventId`
- `timestamp`
- `serviceId`
- `layerId`
- `objectId`
- `operation` (`insert`, `update`, `delete`)
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

Operational signals:

- `honua.outbox.dispatched_total`, `honua.outbox.failed_total`, `honua.outbox.dead_lettered_total`
- `honua.outbox.pending_count`, `honua.outbox.dead_lettered_count`, `honua.outbox.oldest_pending_age_seconds`
- `honua.outbox.recovered_claims_total`
- Health check `feature-change-outbox` on `/healthz/ready` (tags `outbox`, `events`).
  Reports `Healthy` when the dispatcher is running and the backlog is below
  `DegradedBacklogThreshold`, `Degraded` once the backlog crosses that threshold,
  and `Unhealthy` when dead-lettered rows reach `UnhealthyDeadLetterThreshold`
  or the dispatcher is not running. On non-capable providers the check stays
  `Healthy` and surfaces the capability limitation as its description so
  smoke probes do not flap.

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
