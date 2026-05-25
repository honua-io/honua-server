# Console Job Observability API

The Console job observability API exposes the durable execution-job substrate
for operator job viewers. It is a server-side admin contract over existing job
records, logs, artifacts, retry policy, cancellation state, RBAC, and Operate
event correlation. It does not introduce a new execution engine.

Tracked by issue **#1170**. The implementation lives in
`Honua.Server.Features.Admin.Jobs` and uses source-generated JSON through
`ConsoleJobJsonContext` for trimming and Native AOT compatibility.

## Surface

All routes live under `/api/v1/admin/jobs`, require admin authentication, and
use the shared operator authorization gate:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/admin/jobs` | List durable execution jobs with cursor pagination. |
| `GET` | `/api/v1/admin/jobs/{jobId}` | Get job detail. |
| `GET` | `/api/v1/admin/jobs/{jobId}/logs` | Page structured execution logs. |
| `GET` | `/api/v1/admin/jobs/{jobId}/artifacts` | Page artifact references and availability state. |
| `GET` | `/api/v1/admin/jobs/{jobId}/actions` | List currently available control actions. |
| `POST` | `/api/v1/admin/jobs/{jobId}/cancel` | Cancel a queued, provisioning, or running job. |
| `POST` | `/api/v1/admin/jobs/{jobId}/retry` | Retry a failed or cancelled job when policy allows. |

Responses are plain camelCase JSON DTOs, not the `ApiResponse<T>` envelope used
by some older admin endpoints. Null fields are omitted. Error responses use the
shared admin `application/problem+json` shape, except approval-gated control
actions which return `403` with `type = "urn:honua:approval-required"` and
approval extension fields.

Every route sets `Cache-Control: no-store`. Per-job read responses also set
`X-Correlation-Id` when the job record has a correlation id.

## List query

`GET /api/v1/admin/jobs` returns jobs ordered by `createdAt` newest first:

```json
{
  "items": [
    {
      "jobId": "job-01H...",
      "kind": "Geoprocessing",
      "queue": "critical",
      "backend": "local",
      "targetKind": "KubernetesJob",
      "workloadName": "buffer-parcels",
      "definitionId": "plan-42",
      "status": "Running",
      "priority": "Normal",
      "requestedBy": "alice",
      "createdAt": "2026-05-24T06:50:00Z",
      "updatedAt": "2026-05-24T06:51:15Z",
      "durationMs": 75000,
      "percentComplete": 42,
      "currentPhase": "Running",
      "attemptCount": 1,
      "maxAttempts": 3,
      "correlationId": "corr-123",
      "traceId": "trace-abc",
      "resourceRefs": ["service/parcels"],
      "artifactCount": 0,
      "latestEvent": {
        "type": "job.running",
        "occurredAt": "2026-05-24T06:51:15Z",
        "status": "Running",
        "phase": "Running"
      },
      "links": {
        "self": "/api/v1/admin/jobs/job-01H...",
        "logs": "/api/v1/admin/jobs/job-01H.../logs",
        "artifacts": "/api/v1/admin/jobs/job-01H.../artifacts",
        "actions": "/api/v1/admin/jobs/job-01H.../actions",
        "cancel": "/api/v1/admin/jobs/job-01H.../cancel",
        "retry": "/api/v1/admin/jobs/job-01H.../retry",
        "eventsByJob": "/api/v1/admin/observability/events?kind=job&operationId=job-01H...",
        "eventsByCorrelation": "/api/v1/admin/observability/events?kind=job&correlationId=corr-123"
      }
    }
  ],
  "nextCursor": null
}
```

Supported query parameters:

| Parameter | Notes |
| --- | --- |
| `status` | Comma-separated and repeatable list. Values: `Queued`, `Provisioning`, `Running`, `Succeeded`, `Failed`, `Cancelled`. Numeric enum tokens are rejected. |
| `kind` | Single job kind. Values: `Geoprocessing`, `ExtractTransformLoad`, `TileCache`. |
| `backend` | Backend identifier, compared case-insensitively. |
| `queue` | Resolved queue/routing lane, compared case-insensitively. This matches the displayed `queue` value: `honua.job.queue` when present, otherwise the job `backend`. |
| `actor` or `requestedBy` | Submitting actor filter. |
| `correlationId` | Correlation id filter. |
| `traceId` | Trace id filter from `honua.trace_id`. |
| `definitionId` | Matches `honua.job.definition_id` or `honua.geoprocessing.plan_id`. |
| `resourceRef` | Matches one value from pipe-delimited `honua.job.resource_refs`. |
| `environment` | Matches `honua.job.environment`. |
| `server` | Matches `honua.job.server`. |
| `releaseId` | Matches `honua.job.release_id`. |
| `changeSetId` | Matches `honua.job.change_set_id`. |
| `alertId` | Matches `honua.job.alert_id`. |
| `from` | Inclusive ISO-8601 lower bound for `createdAt`. |
| `to` | Exclusive ISO-8601 upper bound for `createdAt`. |
| `limit` | Defaults to `50`; Redis-backed store clamps to `1..200`. |
| `cursor` | Opaque cursor returned as `nextCursor`; invalid cursors return `400`. |

String filters are trimmed and compared case-insensitively by the Redis-backed
store. Clients should not rely on case-sensitive distinctions when filtering
the Console jobs list.

The list path applies authorization per returned job. A page can therefore
contain fewer than `limit` items if some records in the underlying page are not
readable by the caller; clients should continue while `nextCursor` is present.

`latestEvent` is derived from the durable job record (`status`, `updatedAt` or
`completedAt`, `currentPhase`, and failure message). The list endpoint does not
perform full log reads.

## Detail

`GET /api/v1/admin/jobs/{jobId}` returns the summary fields plus:

| Field | Notes |
| --- | --- |
| `providerOperationId` | Remote provider id when the job has been submitted. |
| `claimedBy`, `claimedAt`, `lastHeartbeatAt` | Worker claim and heartbeat state. |
| `cancellationRequestedAt` | Set when remote or cooperative cancellation is in progress. |
| `warnings[]` | Retained warnings on the durable record. |
| `failure` | `{ message, classification }`; classification is `execution_failed` or `cancelled`. |
| `retryPolicy` | `{ maxAttempts, strategy, baseDelayMs, maxDelayMs }`. |
| `parentJobId`, `childJobIds[]` | Workflow parent/child metadata from canonical job parameters. |
| `selectedMetadata` | Safe metadata keys only; secret-like keys/values are omitted and long values are truncated to 512 characters. |
| `stages[]` | Current phase projected as a stage. |
| `actions[]` | Same action descriptors returned by `/actions`. |

Selected metadata includes canonical job keys (`honua.job.*`,
`honua.trace_id`) plus geoprocessing plan/process/output keys. It intentionally
does not echo arbitrary job parameters.

## Logs

`GET /api/v1/admin/jobs/{jobId}/logs?limit=&cursor=` returns:

```json
{
  "jobId": "job-01H...",
  "correlationId": "corr-123",
  "state": "available",
  "items": [
    {
      "timestamp": "2026-05-24T06:51:15Z",
      "level": "Info",
      "message": "queued",
      "phase": "Queued",
      "metadata": { "worker": "worker-1" }
    }
  ],
  "nextCursor": null
}
```

`limit` defaults to `100`; the Redis log store clamps to `1..500`. Cursors are
opaque offset tokens. Invalid cursors return `400`.

If the execution log store is not registered, the endpoint still returns `200`
with `state = "unavailable"`, an empty `items` array, and no cursor. Unknown
jobs return `404`.

## Artifacts

`GET /api/v1/admin/jobs/{jobId}/artifacts?limit=&cursor=` pages artifact
references from the durable job record. `limit` defaults to `100` and is
clamped to `1..200`; cursors are opaque offset tokens.

Each item has:

| Field | Notes |
| --- | --- |
| `artifactId` | Stored artifact id or raw reference. |
| `availability` | `Available`, `Unavailable`, `Expired`, `Redacted`, or `ProviderError`. |
| `kind`, `label`, `contentType`, `sizeBytes` | Present when artifact metadata is available. |
| `providerLink` | Returned only for safe absolute `http`/`https` URLs or non-root relative provider links. |
| `message` | Operator-safe explanation for unavailable, redacted, or provider-error cases. |

Unsafe links are redacted. This includes `data:`, `file:`, root-relative
paths, absolute local paths, traversal segments, and values containing
secret-like tokens.

## Actions and controls

`GET /api/v1/admin/jobs/{jobId}/actions` returns:

```json
{
  "jobId": "job-01H...",
  "correlationId": "corr-123",
  "actions": [
    {
      "name": "cancel",
      "method": "POST",
      "href": "/api/v1/admin/jobs/job-01H.../cancel",
      "allowed": true,
      "requiresApproval": false
    }
  ]
}
```

Action advertisement rules:

| Action | Advertised when | Additional checks |
| --- | --- | --- |
| `cancel` | Job status is `Queued`, `Provisioning`, or `Running`. | Caller needs `OperatorOperation.Execute`; destructive-approval policy can require approval. |
| `retry` | Job status is `Failed` or `Cancelled`. | Caller needs execute permission and `JobRetryPolicy` must allow another attempt. Local jobs also require `IJobQueue`; remote jobs require a registered backend that supports retry. |

Use the `actions[]` response, not the presence of `links.cancel` or
`links.retry`, to decide whether a control should be enabled.
Retry action descriptors can be present with `allowed = false`; clients should
surface `disabledReason` when present. Current values are `execute permission
required`, `job queue unavailable`, `job backend unavailable`, `job backend
retry capability unavailable`, `job backend does not support retry`, `retry
budget exhausted`, and `not retryable`.

Control endpoints accept no request body and return:

```json
{
  "jobId": "job-01H...",
  "correlationId": "corr-123",
  "action": "retry",
  "status": "Queued",
  "message": "Job queued for manual retry."
}
```

Conflict and availability behavior:

| Scenario | Response |
| --- | --- |
| Unknown job | `404`. |
| Terminal job cancelled again | `409`, except already-cancelled jobs return `200` with an idempotent message. |
| Retry requested for a non-failed/non-cancelled job | `409`. |
| Retry budget exhausted | `409`. |
| Local retry queue unavailable or write fails | `503`; when a local queue write fails, the service attempts to restore the prior terminal job state before returning. |
| Remote retry backend unavailable or capability lookup fails | `503`. |
| Remote backend does not support retry | `409`. |
| Backend does not support remote cancellation | `409`. |
| Approval required | `403 application/problem+json` with `urn:honua:approval-required`. |

Cancellation persists the durable job transition before best-effort queue
cleanup. If removing a stale queue entry fails after cancellation has been
persisted, the endpoint still returns the durable cancellation response and
logs the cleanup failure. Manual retry uses the same job id; if the durable
record for a local job is moved to `Queued` but `IJobQueue.RequeueAsync` fails
before the retry is safely enqueued, the service restores the prior terminal
state when the record is still an unclaimed manual retry candidate. Remote
manual retry stores a due `nextRetryAt` marker and lets the execution-job
reconciler start the next provider attempt.

Only `cancel` and `retry` are implemented in this contract. Pause, resume,
approve, promote, rollback, and rerun-with-parameters require future bounded
contracts and should not be assumed by clients.

## Operate event correlation

Job links include `eventsByJob` and, when present, `eventsByCorrelation`
deep links into the Operate event API:

```text
GET /api/v1/admin/observability/events?kind=job&operationId={jobId}
GET /api/v1/admin/observability/events?kind=job&correlationId={correlationId}
```

The event feed projects durable execution jobs from `IExecutionJobStore` and
active progress from `IUniversalProgressStore` into normalized job events. A
durable job event uses:

| Event field | Source |
| --- | --- |
| `eventId` | `job:{operationId}` |
| `kind` | `job` |
| `severity` | `info`, `notice`, `warning`, or `error` from job status |
| `occurredAt` | `completedAt` when present, otherwise `updatedAt` |
| `title` | `{kind} {status}` |
| `summary` | `currentPhase` |
| `actor`, `correlationId`, `traceId`, `releaseId`, `changeSetId` | Durable job audit/metadata |
| `operationId` | Job id |
| `resourceRef` | `job/{operationId}` |

The event projection is read-time composition; it does not persist a separate
event row for every job state transition.

## Retention and storage notes

Redis-backed execution jobs and structured logs use a default 7-day retention.
The job store maintains sorted-set indexes for list filters and removes stale
index members opportunistically during queries. Log and artifact paging use
bounded cursor reads so Console views do not load every entry or artifact into
memory.
