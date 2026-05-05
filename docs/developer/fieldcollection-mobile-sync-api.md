# FieldCollection Mobile Sync API

This document describes the FieldCollection mobile sync endpoints (`#894`)
consumed by the `honua-mobile` FieldCollection offline sync clients. The
API is the server-side contract that backs the mobile interfaces
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

## Client identity

Multiple field devices commonly share a single API key, so the API-key
principal alone is not a stable per-device identity. Mobile clients should
send `X-Honua-Client-Id` on every sync call (`sync-cursor`, pull, push) so
that each device gets an independent cursor:

```http
X-Honua-Client-Id: <stable-device-id>
```

Recommended values are durable per-install identifiers (for example, the
mobile SDK's persisted device GUID). Maximum length is 128 characters;
longer values are truncated. When the header is absent, the server falls
back to the authenticated principal name — which means callers without
the header collapse onto one shared cursor by design.

## Endpoints

| Method | Path | Mobile interface |
|--------|------|------------------|
| `GET`  | `/api/v1/fieldcollection/generation`   | `IFieldCollectionChangePuller.GetLatestServerGenerationAsync()` |
| `GET`  | `/api/v1/fieldcollection/sync-cursor`  | `IFieldCollectionChangePuller.GetLastSyncedGenerationAsync()` |
| `POST` | `/api/v1/fieldcollection/sync-cursor`  | Explicit cursor acknowledgement after local persistence |
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

Returns the last generation the calling client has explicitly
acknowledged via `POST /api/v1/fieldcollection/sync-cursor`. The server
creates a zero-valued cursor on first read so the response shape is
stable. Pull responses are pure reads — they never advance this value.

**Response 200 OK**

```json
{
  "clientId": "field-tablet-01",
  "lastSyncGeneration": 1038
}
```

### `POST /api/v1/fieldcollection/sync-cursor`

Records the client's last-applied generation after local persistence
succeeds. This is the explicit acknowledgement that decouples the HTTP
pull response from the durable client cursor: the server never advances
the per-client cursor implicitly, so a client that crashes or fails to
persist a pulled page never has its cursor jump past unapplied changes.

**Request body**

```json
{ "lastSyncGeneration": 1042 }
```

| Field                | Type    | Required | Notes |
|----------------------|---------|----------|-------|
| `lastSyncGeneration` | integer | yes      | Last applied generation. Must be ≥ 0 and ≤ the current `serverGeneration`. |

**Response 200 OK**

```json
{
  "clientId": "field-tablet-01",
  "lastSyncGeneration": 1042
}
```

The persisted value is monotonic — a smaller `lastSyncGeneration` from a
late-arriving retry can never regress a larger one, so concurrent acks
are safe.

**Error responses**

- `400 Bad Request` when `lastSyncGeneration` is missing, negative, or
  greater than the current committed `serverGeneration` (a future or
  poisoned cursor cannot be persisted).
- `401 Unauthorized` when the `X-API-Key` header is missing or invalid.

### `GET /api/v1/fieldcollection/changes`

Returns ordered FieldCollection changes after the supplied generation
cursor. The pull is a pure read — the per-client cursor is never
advanced as a side effect. Mobile clients drive the cursor explicitly
by calling `POST /api/v1/fieldcollection/sync-cursor` after local
persistence succeeds.

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
ordered by ascending `generation`. `nextCursor` is the recommended
watermark for the next pull: the largest returned generation when the
page is non-empty, or the committed server watermark when the page is
empty. The response value is always clamped to the committed server
watermark — a misbehaving client that supplies a `sinceGeneration`
greater than `serverGeneration` cannot poison the response cursor with a
future value. When `hasMore` is `true`, repeat with
`sinceGeneration=nextCursor` until empty. To persist progress on the
server side, send `nextCursor` to `POST /api/v1/fieldcollection/sync-cursor`
after local persistence completes.

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
  required fields, has an unsupported operation, or sets `feature` to a
  non-null value while `operation` is `delete`.
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

Concurrent pushes that share a `changeId` are serialized inside the server
with a transaction-scoped advisory lock keyed on `changeId`. The first
request commits the idempotency record; the second waits for that
commit and then returns the stored response. Callers never observe a
unique-violation surfaced as a 5xx for duplicate `changeId`.

Concurrent pushes that target the same `(featureId, layerId)` with
distinct `changeId` values are also serialized so exactly one push
applies and the others observe the freshly committed state as a
`conflict`. This is the contract guarantee mobile clients should rely
on when multiple devices race against the same feature: concurrent
inserts cannot silently overwrite each other.

## Generation cursor semantics

The server uses the shared `honua.sync_generation` sequence to allocate
generation values for new FieldCollection changes, so the cursor is
consistent with existing change-tracking infrastructure. Generation
values can advance between pulls because of writes from other workflows;
mobile clients may observe gaps in their pull stream and should treat
them as no-ops.

`serverGeneration` and `nextCursor` are computed from the committed
maximum generation in `honua.fieldcollection_changes`, never from the
sequence's raw `last_value`. This guarantees that an empty pull cannot
report a watermark past a write that has allocated a sequence value but
not yet committed — the next pull will still observe that change once it
lands. Pulls bound their result page by the same committed watermark, so
a row that commits in between the watermark read and the result scan is
excluded from the page and surfaces on the next pull instead of being
skipped.

The pull endpoint is a pure read. The per-client cursor stored in
`honua.fieldcollection_sync_cursors` is advanced only by an explicit
`POST /api/v1/fieldcollection/sync-cursor` after the client has durably
persisted the changes it pulled. Treating the HTTP pull response as an
implicit acknowledgement would silently advance the cursor past changes
the client never wrote to disk if the client crashed between receiving
the response and applying it. The acknowledgement endpoint also rejects
values greater than the committed server watermark, so a poisoned or
buggy `lastSyncGeneration` cannot persist a future cursor that would
later cause real changes to be skipped.

## Provider support

The mobile sync surface ships against the PostgreSQL provider only. When
the server is started against a non-Postgres provider (DuckDB, MySQL /
MariaDB, SQL Server) the FieldCollection routes are not registered and
requests return `404 Not Found`. This is deliberate: the alternative
would be a generic `500` from a missing-service resolution at request
time.
