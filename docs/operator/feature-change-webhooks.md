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
