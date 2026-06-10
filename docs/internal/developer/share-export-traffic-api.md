# Share Export And Traffic API

The Share export and traffic API is the server-owned admin contract used by
Console Share panels. It stores scheduled export definitions, append-mostly run
history, and aggregate/per-item traffic projections without introducing a new
worker runtime. Export runs can link to the existing Operate job viewer through
`jobRunId`.

Tracked by issue **#1216**. The implementation lives in
`Honua.Server.Features.Admin.Share`, uses source-generated JSON through
`ShareAdminJsonContext`, and persists through `IShareExportStore` and
`IShareTrafficStore` (`PostgresShareExportStore` / `PostgresShareTrafficStore`
when the PostgreSQL provider is active).

## Surface

All routes require admin authorization. Responses are camelCase JSON DTOs with
null fields omitted, except `jobRunId` on export-run responses is always
present so Console can distinguish "not linked" from "field missing".

Create returns `201 Created` with a `Location` header pointing at the new
definition. Trigger returns `202 Accepted`, delete returns `204 No Content`, and
the remaining reads, updates, and pause/resume calls return `200 OK`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/admin/share/exports` | List scheduled Share export definitions. |
| `POST` | `/api/v1/admin/share/exports` | Create a scheduled Share export definition. |
| `GET` | `/api/v1/admin/share/exports/{exportId}` | Get one export definition. |
| `PUT` | `/api/v1/admin/share/exports/{exportId}` | Replace one export definition. |
| `DELETE` | `/api/v1/admin/share/exports/{exportId}` | Delete a definition and its run history. |
| `POST` | `/api/v1/admin/share/exports/{exportId}/trigger` | Manually create an export run. |
| `POST` | `/api/v1/admin/share/exports/{exportId}/pause` | Set `scheduleState` to `Paused`. |
| `POST` | `/api/v1/admin/share/exports/{exportId}/resume` | Set `scheduleState` to `Active`. |
| `GET` | `/api/v1/admin/share/exports/{exportId}/runs` | List run history for a definition. |
| `GET` | `/api/v1/admin/share/exports/{exportId}/runs/{runId}` | Get one run. |
| `GET` | `/api/v1/admin/share/traffic` | Get aggregate Share traffic summary. |
| `GET` | `/api/v1/admin/share/traffic/series` | Get aggregate Share traffic time series. |
| `GET` | `/api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic` | Get per-item traffic summary. |
| `GET` | `/api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series` | Get per-item traffic time series. |

## Export Definitions

Definitions are anchored to the stable Console `resourceId` when one exists.
`serviceName` and `layerId` are still required compatibility locators for
layer-backed resources.

Create and replace requests use this shape:

```json
{
  "resourceId": "content-parcels",
  "serviceName": "parcels",
  "layerId": 7,
  "displayName": "Parcels nightly export",
  "destinationType": "Webhook",
  "destinationConfig": {
    "url": "https://example.invalid/share-webhook",
    "credentialRef": "vault://share/webhook"
  },
  "format": "GeoJSON",
  "schedule": "0 * * * *",
  "scheduleState": "Active"
}
```

Required fields are `serviceName`, `layerId`, `destinationType`, `format`, and
`schedule`. `layerId` must be zero or greater. `scheduleState` is optional and
defaults to `Active`. `PUT` is replace-style; omitting optional fields clears
them, and omitting `scheduleState` resets it to `Active`.

Supported enum values:

| Field | Values |
| --- | --- |
| `destinationType` | `S3`, `Sftp`, `Webhook`, `AuditSnapshot` |
| `destinationStatus` | `Supported`, `Unsupported`, `NotConfigured` |
| `scheduleState` | `Active`, `Paused` |
| run `triggerKind` | `Manual`, `Scheduled` |
| run `status` | `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled` |

Numeric enum tokens are rejected. Enum strings are parsed case-insensitively,
but responses use the canonical casing above.

Definition responses include:

```json
{
  "exportId": "7f6bf330d3db442f84401142443477ad",
  "resourceId": "content-parcels",
  "serviceName": "parcels",
  "layerId": 7,
  "displayName": "Parcels nightly export",
  "destinationType": "Webhook",
  "destinationStatus": "Unsupported",
  "destinationConfig": {
    "url": "https://example.invalid/share-webhook",
    "credentialRef": "vault://share/webhook"
  },
  "format": "GeoJSON",
  "schedule": "0 * * * *",
  "scheduleState": "Active",
  "createdAt": "2026-05-25T18:20:00Z",
  "updatedAt": "2026-05-25T18:20:00Z"
}
```

The default server registration marks every destination `Unsupported` until an
environment-specific `IShareExportDestinationResolver` is registered. A
resolver can return:

| Status | Meaning |
| --- | --- |
| `Supported` | A worker/config path is registered and manual trigger can create an Operate job. |
| `Unsupported` | The destination is modeled but no worker is registered in this build. |
| `NotConfigured` | The destination is known, but required credentials or environment configuration are missing. |

Definitions may be stored while `Unsupported` or `NotConfigured`; Console should
badge those states rather than treating the definition as missing. The status is
resolved on create/update and refreshed when a manual trigger is attempted.

`destinationConfig` must be display-safe. Raw secret-shaped keys are rejected
unless they are reference keys. A key is treated as secret-shaped when it
contains `password`, `secret`, `token`, `privateKey`, `apiKey`, or `accessKey`,
and is rejected unless represented as `secretRef`, `credentialRef`, or another
key ending in `Ref` or `Reference`. As a read-side safeguard, any stored config
key that still looks like raw secret material has its value returned as
`redacted` rather than the stored value.

## Listing And Cursors

Definition list filters:

| Parameter | Notes |
| --- | --- |
| `serviceName` | Exact service-name filter; clients should preserve the service's canonical casing. |
| `resourceId` | Exact stable Console resource/content identifier. |
| `layerId` | Exact layer id. |
| `destinationType` | One destination enum value. |
| `scheduleState` | `Active` or `Paused`. |
| `limit` | Defaults to `50`; clamped to `1..200`. |
| `cursor` | Opaque `nextCursor` from the previous page; invalid cursors return `400`. |

Definitions are ordered by `updatedAt` newest first. Run history is ordered by
`triggeredAt` newest first and accepts the same `limit` and `cursor` paging
parameters.

## Trigger And Job Linkage

`POST /api/v1/admin/share/exports/{exportId}/trigger` creates a manual run.
If the destination resolves to `Supported`, the endpoint writes an
`ExecutionJobKind.ShareExport` job, enqueues it on the durable job queue for a
worker to claim, and returns `202`:

```json
{
  "runId": "4b3e9d88e77b45b196b2dc5c385b1791",
  "exportId": "7f6bf330d3db442f84401142443477ad",
  "triggerKind": "Manual",
  "status": "Queued",
  "jobRunId": "share-export-1e3f4eec4ca74304a87bb29119a1a66a",
  "triggeredAt": "2026-05-25T18:22:00Z",
  "targetSummary": "Webhook https://example.invalid/share-webhook",
  "resultArtifacts": []
}
```

`jobRunId` equals `ExecutionJob.OperationId`. Console can deep-link to
`/operate/jobs/{jobRunId}` and can also query the admin jobs API with
`kind=ShareExport`.

The job spec includes these canonical parameters:

| Parameter key | Value |
| --- | --- |
| `honua.job.definition_id` | Export definition id. |
| `honua.job.resource_refs` | `service/{serviceName}`, `layer/{serviceName}/{layerId}`, and `share/{resourceId}` when present. |
| `honua.share.export_id` | Export definition id. |
| `honua.share.run_id` | Export run id. |
| `honua.share.destination_type` | Destination enum value. |
| `honua.share.format` | Requested export format. |

If the destination is not runnable, the endpoint records a failed run with
`jobRunId: null` and returns `422 application/problem+json`:

| Destination status | Problem `title` | Run `lastError` |
| --- | --- | --- |
| `Unsupported` | `share-export-destination-unsupported` | `share-export-destination-unsupported` |
| `NotConfigured` | `share-export-destination-not-configured` | `share-export-destination-not-configured` |

A `Supported` trigger runs on the local batch backend, so it requires both the
durable execution job store and the job queue. If either is unavailable, or the
job record cannot be created, the endpoint returns `503` and does not record a
run. The run is persisted **before** the job is dispatched, so a dispatched job
always has a run record tracking it: if the run cannot be persisted, the created
job is rolled back and nothing is dispatched (`503`/`500`). If the run is
persisted but enqueuing the job fails, the endpoint rolls the job back to a
terminal `Failed` state, marks the run `Failed` (keeping its `jobRunId`) with
`lastError` `share-export-dispatch-failed`, and returns `503`. A returned `202`
therefore always means the run was persisted and the job was both created and
dispatched. These compensating paths run as a commit-once critical section that
is not bound to the client connection, so a trigger aborted or disconnected after
the job record is created still completes one of the outcomes above rather than
stranding a job without a run, or a persisted run whose job was never enqueued.

Once a run is backed by a job, its status reconciles to that job's terminal
state: when the execution job reaches `Succeeded`, `Failed`, or `Cancelled` (for
example after a Console cancel through the jobs API at
`/api/v1/admin/jobs/{jobRunId}`), the run is updated to the matching status with
`completedAt`, on failure the job's `lastError`, and any artifacts the worker
published on the backing job copied into the run's `resultArtifacts` (including
diagnostics from a failed job); the run keeps its existing `resultArtifacts` only
when the job published none. The first terminal status wins; a later job
notification does not overwrite an already-terminal run.

The backing job is created with `JobRetryPolicy.None`. Because run history is
first-terminal-wins, the generic jobs API refuses to retry a `ShareExport` job:
`POST /api/v1/admin/jobs/{jobRunId}/retry` returns `409` and the `retry` action is
reported with `allowed: false` and `disabledReason: not retryable`. This holds
even when the job became terminal before its first attempt (for example a cancel
before worker pickup), so a rejected retry can never re-run the export while its
run stays terminal. Re-running an export uses a fresh trigger (a new run) rather
than reopening the original run.

`pause` and `resume` only update `scheduleState`; they do not cancel already
queued jobs. Scheduled execution is not implemented in this API slice, so
`nextRunAt` is null unless a future scheduler writes it.

## Traffic Reads

Traffic counters are read-only projections for Console Share home and per-item
panels. They cover these interaction fields:

| Field | Meaning |
| --- | --- |
| `public` | Direct public access. |
| `publicLink` | Public-link access. |
| `embed` | Embedded access. |
| `openData` | Open-data portal access. |
| `dcat` | DCAT catalog access. |
| `stac` | STAC catalog access. |
| `export` | Export interactions. |

Summary reads return the total over `[periodStart, periodEnd)`. Series reads
return contiguous buckets beginning at `periodStart`; empty buckets are present
with zero counts. If no telemetry buckets exist for the requested scope, the
summary returns the requested period with all counts and `totalRequests` set to
zero.

Traffic query parameters:

| Parameter | Routes | Notes |
| --- | --- | --- |
| `periodStart` | summary and series | Optional ISO-8601 timestamp. Defaults to 24 hours before `periodEnd`. |
| `periodEnd` | summary and series | Optional ISO-8601 timestamp. Defaults to server time. Must be after `periodStart`. |
| `bucketMinutes` | series | Optional positive integer. Defaults to `60`. |
| `resourceId` | per-item routes | Optional stable Console resource/content id used with the path `serviceName` and `layerId`. |

All periods are normalized to UTC. Traffic reads share a bucket guard on the
number of buckets a series would emit: `ceil((periodEnd - periodStart) /
bucketMinutes)` must not exceed `2000`. The ceiling matches the buckets the
series actually returns, so a window of exactly 2,000 buckets is allowed but a
partial bucket past that is rejected with `400`. Summary routes use the default
60-minute guard and do not accept `bucketMinutes`; narrow the summary period for
wider windows. Series clients can increase `bucketMinutes` when they need a
longer period.

Aggregate summary example:

```json
{
  "periodStart": "2026-05-25T00:00:00Z",
  "periodEnd": "2026-05-25T02:00:00Z",
  "byInteractionType": {
    "public": 3,
    "publicLink": 0,
    "embed": 0,
    "openData": 4,
    "dcat": 0,
    "stac": 0,
    "export": 2
  },
  "totalRequests": 9
}
```

Per-item series example:

```json
{
  "itemRef": {
    "resourceId": "content-parcels",
    "serviceName": "parcels",
    "layerId": 7
  },
  "periodStart": "2026-05-25T00:00:00Z",
  "periodEnd": "2026-05-25T02:00:00Z",
  "bucketDuration": "01:00:00",
  "buckets": [
    {
      "bucketStart": "2026-05-25T00:00:00Z",
      "byInteractionType": {
        "public": 3,
        "publicLink": 0,
        "embed": 0,
        "openData": 0,
        "dcat": 0,
        "stac": 0,
        "export": 2
      },
      "total": 5
    }
  ]
}
```

Aggregate traffic responses omit `itemRef`; per-item responses include it.

## Errors

Error responses use the shared admin `application/problem+json` shape:

```json
{
  "type": "https://honua.io/problems/admin",
  "title": "Bad Request",
  "status": 400,
  "detail": "periodStart must be before periodEnd.",
  "instance": "/api/v1/admin/share/traffic",
  "correlationId": "0HN...",
  "timestamp": "2026-05-25T18:25:00.0000000Z"
}
```

Common status codes:

| Status | When |
| --- | --- |
| `400` | Invalid cursor, enum token, required field, secret-shaped config key, date range, or bucket size. |
| `404` | Export definition or run was not found. |
| `409` | Store conflict on create/append. |
| `422` | Destination is unsupported or not configured for trigger. |
| `503` | Durable store or job runner is unavailable. |
| `500` | Unexpected server-side failure after safe error mapping. |

## Developer Notes

The public DTO shape is fixed-width rather than enum-keyed dictionaries so SDK
projection remains trimming and Native AOT friendly. Avoid adding reflection
serialization or dynamic response maps to this slice.

PostgreSQL migration `037_CreateShareExportTraffic.sql` creates
`honua.share_export_definitions`, `honua.share_export_runs`, and
`honua.share_traffic_buckets`. The run table cascades on definition delete, and
the traffic table is a read projection: ingestion/upsert of traffic buckets is
outside this API contract.
