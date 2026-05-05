# FieldCollection Mobile Sync API

This document describes the four FieldCollection mobile sync endpoints
(`#894`) consumed by the `honua-mobile` FieldCollection offline sync clients.
The API is the server-side contract that backs the mobile interfaces
`IFieldCollectionChangePuller` and `IFieldCollectionChangeUploader`. All
endpoints are versioned under `/api/v1/fieldcollection/`, require
`X-API-Key` authentication, and emit `Cache-Control: no-store`.

## Authentication

All endpoints require API-key authentication consistent with existing
mobile/SDK conventions. Send the API key on every request via the
`X-API-Key` request header. Unauthenticated traffic receives `401
Unauthorized` with a problem-details body.

```http
X-API-Key: <api-key>
```

The authenticated principal name is treated as the `clientId` for per-client
sync cursor tracking.

## Endpoints

| Method | Path | Mobile interface |
|--------|------|------------------|
| `GET`  | `/api/v1/fieldcollection/generation`   | `IFieldCollectionChangePuller.GetLatestServerGenerationAsync()` |
| `GET`  | `/api/v1/fieldcollection/sync-cursor`  | `IFieldCollectionChangePuller.GetLastSyncedGenerationAsync()` |
| `GET`  | `/api/v1/fieldcollection/changes`      | `IFieldCollectionChangePuller.GetChangesAsync(sinceGeneration)` |
| `POST` | `/api/v1/fieldcollection/changes`      | `IFieldCollectionChangeUploader.UploadChangeAsync(change)` |

### `GET /api/v1/fieldcollection/generation`

Returns the latest monotonic server generation cursor. Suitable for
offline clients to persist as the watermark for the next pull.

**Response 200 OK**

```json
{ "serverGeneration": 1042 }
```

### `GET /api/v1/fieldcollection/sync-cursor`

Returns the last server-acknowledged generation for the calling client. The
server creates a zero-valued cursor on first read so the response shape is
stable.

**Response 200 OK**

```json
{
  "clientId": "field-tablet-01",
  "lastSyncGeneration": 1038
}
```

### `GET /api/v1/fieldcollection/changes`

Returns ordered FieldCollection changes after the supplied generation
cursor. Successful pulls advance the per-client cursor as a side effect so
the next call to `sync-cursor` reflects the new watermark.

**Query parameters**

| Name              | Type    | Default | Notes |
|-------------------|---------|---------|-------|
| `sinceGeneration` | integer | `0`     | Exclusive lower bound. Must be ≥ 0. |
| `limit`           | integer | `200`   | Max page size. Range `[1, 1000]`. |

**Response 200 OK**

```json
{
  "serverGeneration": 1042,
  "nextCursor": 1042,
  "hasMore": false,
  "changes": [
    {
      "featureId": "abc-123",
      "layerId": 1,
      "operation": "update",
      "version": 12,
      "generation": 1042,
      "timestamp": "2026-05-01T10:00:00+00:00",
      "feature": {
        "type": "Feature",
        "geometry": { "type": "Point", "coordinates": [-122.4, 37.7] },
        "properties": { "name": "Pump 17", "status": "operational" }
      }
    }
  ]
}
```

`feature` is `null` for `delete` operations; CRS, datum, and coordinate
precision in `feature` are preserved exactly as stored. Changes are
ordered by ascending `generation` and the largest returned generation is
echoed in `nextCursor`. When `hasMore` is `true`, repeat with
`sinceGeneration=nextCursor` until empty.

**Error responses**

- `400 Bad Request` with problem-details when `sinceGeneration < 0` or
  `limit` is outside `[1, 1000]`.
- `401 Unauthorized` when the `X-API-Key` header is missing or invalid.

### `POST /api/v1/fieldcollection/changes`

Pushes a single mobile-assigned FieldCollection change. Repeated calls
with the same `changeId` return the previously stored outcome without
re-applying.

**Request body**

```json
{
  "changeId": "d47ac10b-58cc-4372-a567-0e02b2c3d479",
  "featureId": "abc-123",
  "layerId": 1,
  "operation": "update",
  "baseVersion": 11,
  "timestamp": "2026-05-01T09:58:00+00:00",
  "feature": {
    "type": "Feature",
    "geometry": { "type": "Point", "coordinates": [-122.4, 37.7] },
    "properties": { "name": "Pump 17", "status": "needs-service" }
  }
}
```

| Field         | Type    | Required | Notes |
|---------------|---------|----------|-------|
| `changeId`    | string  | yes      | Mobile-assigned UUID; the server uses this as the idempotency key. |
| `featureId`   | string  | yes      | Mobile-assigned feature identifier. |
| `layerId`     | integer | yes      | Layer the feature belongs to. |
| `operation`   | string  | yes      | `insert`, `update`, or `delete`. |
| `baseVersion` | integer | for update/delete | Server version the device last knew. Required for conflict detection. |
| `timestamp`   | string  | no       | ISO-8601 client-side timestamp; defaults to server now. |
| `feature`     | object  | for insert/update | Pre-serialized feature payload. Mobile chooses the schema and the server stores it verbatim. Must be `null` for delete. |

**Response 200 OK — applied**

```json
{
  "changeId": "d47ac10b-58cc-4372-a567-0e02b2c3d479",
  "outcome": "applied",
  "serverGeneration": 1043,
  "version": 12
}
```

**Response 200 OK — conflict**

```json
{
  "changeId": "d47ac10b-58cc-4372-a567-0e02b2c3d479",
  "outcome": "conflict",
  "serverGeneration": 1042,
  "conflictType": "update-update",
  "serverVersion": 13,
  "serverFeature": {
    "type": "Feature",
    "geometry": { "type": "Point", "coordinates": [-122.4, 37.7] },
    "properties": { "name": "Pump 17", "status": "operational" }
  }
}
```

`conflictType` is one of `update-update`, `update-delete`, `delete-update`,
or `delete-delete`. `serverFeature` is the current server payload (or
`null` when the server-side feature is absent).

**Response 200 OK — rejected**

```json
{
  "changeId": "d47ac10b-58cc-4372-a567-0e02b2c3d479",
  "outcome": "rejected",
  "serverGeneration": 1042,
  "rejectionReason": "Update requires baseVersion."
}
```

`rejected` indicates a deterministic validation failure inside the
business contract. Mobile clients should not retry without first
correcting the request.

**Error responses**

- `400 Bad Request` with problem-details when the request body is missing
  required fields or has an unsupported operation.
- `401 Unauthorized` when the `X-API-Key` header is missing or invalid.
- `5xx` outcomes are retryable. Mobile clients should leave the local
  change in pending state and retry on the next sync cycle.

## Idempotency

The server records a row in `honua.fieldcollection_pushed_changes` keyed by
`changeId` for every push that produced a deterministic outcome (applied,
conflict, or rejected). Repeating the push with the same `changeId`
replays the stored response payload without re-applying. Mobile clients
should always reuse the same `changeId` when retrying after network or
server failure.

## Generation cursor semantics

The server uses the shared `honua.sync_generation` sequence so the cursor
is consistent with existing change-tracking infrastructure. Generation
values can advance between pulls because of writes from other workflows;
mobile clients may observe gaps in their pull stream and should treat
them as no-ops.
