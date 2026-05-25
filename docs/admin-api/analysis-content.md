# Analysis Content API

The Analysis Content API makes natural-language saved queries and analysis
packages durable, versioned content. It is the backend contract that lets maps,
dashboards, reports, generated apps, workflows, and MCP clients bind to a saved
query or analysis artifact without copying transient browser preview state.

Tracked by issue **#1182**. The implementation lives in
`Honua.Server.Features.AnalysisContent` and uses source-generated JSON through
`AnalysisContentApiJsonContext` for trimming and Native AOT compatibility.

## Surface

All routes live under `/api/v1/analysis/...` and require admin authorization.
Responses are plain camelCase JSON DTOs, not the older `ApiResponse<T>`
envelope. Null fields are omitted. Expected errors use the shared admin
`application/problem+json` shape. Admin authorization is enforced before the
analysis-content handlers run; run, rerun, and failure requests can also return
geoprocessing authorization problems from the canonical job service.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/analysis/content/items` | Create a saved-query or analysis-package item plus version 1. |
| `GET` | `/api/v1/analysis/content/items/{itemId}` | Open the item and its latest version. |
| `GET` | `/api/v1/analysis/content/items/{itemId}/versions/latest` | Open the latest immutable version. |
| `GET` | `/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}` | Open an explicit immutable version number. |
| `POST` | `/api/v1/analysis/content/items/{itemId}/versions` | Add a new immutable version and advance the item pointer. |
| `POST` | `/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/preview` | Preview a saved-query version through the canonical feature-query pipeline. |
| `POST` | `/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/runs` | Submit an analysis-package version through the canonical geoprocessing runtime. |
| `POST` | `/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/reruns` | Rerun an analysis package with provenance and optional parameter overrides. |
| `GET` | `/api/v1/analysis/artifacts/{artifactId}` | Resolve stable artifact metadata and a downstream binding reference. |
| `GET` | `/api/v1/analysis/jobs/{jobId}/logs` | Read bounded, safe structured logs for an analysis job. |
| `GET` | `/api/v1/analysis/jobs/{jobId}/failure` | Read safe failure classification for a failed or cancelled analysis job. |

Status code conventions:

| Status | Meaning |
| --- | --- |
| `201` | Content item or version was created. |
| `200` | Read, preview, run, rerun, artifact, log, or failure request succeeded. |
| `400` | Invalid request payload, invalid limit, mismatched content kind, bad filter plan, or invalid canonical query. |
| `401` / `403` | Admin authorization or geoprocessing authorization failed. |
| `404` | Item, version, layer, job, or artifact was not found. |
| `409` | The requested job has not failed, or a geoprocessing precondition failed. |
| `503` | The backing content, job, or log store is unavailable. |
| `500` | Unexpected failures are returned as a generic problem without internal details. |

## Content Items And Versions

Create requests must supply `kind`, `name`, and exactly one payload matching
the kind:

```json
{
  "kind": "savedQuery",
  "name": "incidents-by-type",
  "title": "Incidents by Type",
  "savedQuery": {
    "layerId": 42,
    "serviceName": "public-safety",
    "naturalLanguageQuery": "show recent incidents",
    "previewLimit": 25,
    "outputSrid": 4326,
    "units": "meters"
  }
}
```

`kind` values are `savedQuery` and `analysisPackage`. Saved-query payloads
require a non-negative `layerId`; analysis packages require a plan with at
least one step. The server stamps `itemId`, `versionId`, `currentVersion`,
`currentVersionId`, timestamps, owner/audit fields from the authenticated
principal, `visibility = "organization"`, and `lifecycle = "active"`.

Successful create and read responses return the item root and the selected
version:

```json
{
  "item": {
    "itemId": "analysis-content-0f2...",
    "kind": "savedQuery",
    "name": "incidents-by-type",
    "title": "Incidents by Type",
    "visibility": "organization",
    "currentVersion": 1,
    "currentVersionId": "analysis-content-0f2...:v1",
    "lifecycle": "active",
    "createdAt": "2026-05-24T10:00:00Z",
    "updatedAt": "2026-05-24T10:00:00Z"
  },
  "version": {
    "versionId": "analysis-content-0f2...:v1",
    "itemId": "analysis-content-0f2...",
    "version": 1,
    "kind": "savedQuery",
    "savedQuery": {
      "layerId": 42,
      "serviceName": "public-safety",
      "naturalLanguageQuery": "show recent incidents",
      "previewLimit": 25,
      "outputSrid": 4326,
      "units": "meters"
    },
    "contentHash": "5bd...",
    "createdAt": "2026-05-24T10:00:00Z"
  }
}
```

`POST /versions` stores a new immutable payload. If `basedOnVersionId` is
omitted, it defaults to the latest version id. `createdFromJobId` and
`createdFromArtifactIds` are provenance fields for versions derived from prior
results.

Version creation re-reads the latest item state and retries bounded store
conflicts before returning `409 Conflict`. Clients that receive `409` should
reopen `versions/latest`, rebase their payload if needed, and submit a new
version request.

The public slice reopens content by known `itemId` plus either `latest` or an
explicit version number; it does not expose a list route yet. `contentHash` is
computed from the saved `savedQuery` or `analysisPackage` payload only, so
provenance-only fields such as `basedOnVersionId` do not change the payload
hash.

## Saved Query Preview

`POST /preview` accepts an optional body:

```json
{ "limit": 10 }
```

The preview limit defaults to the saved query's `previewLimit`, then to `25`.
Values above `200` are clamped to `200`; values less than or equal to zero
return `400`.

Preview execution uses the canonical query path: the saved `filterPlan` is
compiled against the target layer, projected into `UnifiedQuery`, validated and
optimized by `IQueryProcessor`, then executed by `IFeatureReader`. This keeps
saved-query previews aligned with the normal feature-query behavior for
filters, output fields, and output CRS.

The response is a bounded feature preview plus a stable binding:

```json
{
  "previewArtifactId": "analysis-preview-4ca...",
  "itemId": "analysis-content-0f2...",
  "version": 2,
  "layerId": 42,
  "features": [
    {
      "id": 1001,
      "attributes": { "type": "inspection" },
      "hasGeometry": true
    }
  ],
  "totalCount": 17,
  "exceededPreviewLimit": true,
  "binding": {
    "artifactId": "analysis-preview-4ca...",
    "sourceItemId": "analysis-content-0f2...",
    "sourceVersion": 2,
    "sourceVersionId": "analysis-content-0f2...:v2",
    "role": "preview",
    "targetKind": "map",
    "targetSlot": "source"
  }
}
```

Each preview also upserts a short-lived artifact record with
`retentionState = "preview"`, `jobId = "preview"`, kind `FeatureLayer`, a
`honua://analysis/artifacts/{artifactId}` URI, and a one-hour expiry. Preview
requests are ad hoc feature queries and are not exact-response cached.

## Analysis Package Runs And Reruns

Analysis packages carry an executable `analysisPackage.plan`, default
`parameters`, requested artifact kinds, optional binding hints, and spatial
assumptions:

```json
{
  "kind": "analysisPackage",
  "name": "buffer-package",
  "analysisPackage": {
    "intent": {
      "intentId": "intent-1",
      "goal": "buffer incidents",
      "requestedOutputs": ["FeatureLayer"]
    },
    "plan": {
      "planId": "plan-1",
      "intentId": "intent-1",
      "steps": [
        {
          "stepId": "step-1",
          "kind": "Geoprocess",
          "processId": "geometry.buffer",
          "inputs": { "distance": "10" }
        }
      ],
      "outputs": ["FeatureLayer"]
    },
    "parameters": { "distance": "10" },
    "requestedArtifacts": ["FeatureLayer"],
    "spatialReferenceId": 4326,
    "units": "meters"
  }
}
```

`POST /runs` submits the stored plan through `IGeoprocessingJobService`:

```json
{
  "idempotencyKey": "run-1",
  "parameters": { "format": "geojson" }
}
```

The job receives source metadata using the `analysis.content.*` key namespace,
including item id, version number, version id, content kind, source SRID,
source units, package parameters, and runtime parameters. The response includes
the job id, current job status, and submitted version:

```json
{
  "jobId": "run-1",
  "status": "Queued",
  "version": {
    "versionId": "analysis-content-0f2...:v1",
    "itemId": "analysis-content-0f2...",
    "version": 1,
    "kind": "analysisPackage",
    "contentHash": "a71...",
    "createdAt": "2026-05-24T10:00:00Z"
  }
}
```

Runtime `parameters` are submitted as job provenance metadata and do not mutate
the stored analysis package version. To make parameter changes durable, use
`/reruns` with `parameterOverrides`, which creates a new immutable version
before submitting the job.

`POST /reruns` accepts `idempotencyKey`, `rerunOfJobId`,
`rerunOfResultPackageId`, and `parameterOverrides`. When overrides are present,
each override must match an existing executable plan step input. The server
updates matching step inputs and merged package parameters in the new immutable
version, links it to the source version, and submits that new version. Unknown
override keys are rejected with `400`. Without overrides, the requested version
is resubmitted. Rerun provenance is stamped onto job metadata with
`analysis.content.rerun_of_job_id` and
`analysis.content.rerun_of_result_package_id` when supplied.

## Artifacts And Bindings

`GET /api/v1/analysis/artifacts/{artifactId}` returns the durable artifact
record and a binding clients can place into downstream packages:

```json
{
  "artifact": {
    "artifactId": "analysis-preview-4ca...",
    "resultPackageId": "analysis-content-0f2...:v2:preview",
    "jobId": "preview",
    "sourceItemId": "analysis-content-0f2...",
    "sourceVersion": 2,
    "sourceVersionId": "analysis-content-0f2...:v2",
    "kind": "FeatureLayer",
    "label": "public-safety preview",
    "uri": "honua://analysis/artifacts/analysis-preview-4ca...",
    "contentType": "application/json",
    "metadata": { "layerId": "42", "previewLimit": "10" },
    "provenance": {
      "analysis.content.item_id": "analysis-content-0f2...",
      "analysis.content.version": "2",
      "analysis.content.version_id": "analysis-content-0f2...:v2",
      "analysis.content.kind": "SavedQuery",
      "analysis.content.source_srid": "4326",
      "analysis.content.source_units": "meters"
    },
    "retentionState": "preview",
    "promotionState": "none",
    "createdAt": "2026-05-24T10:05:00Z",
    "expiresAt": "2026-05-24T11:05:00Z"
  },
  "binding": {
    "artifactId": "analysis-preview-4ca...",
    "sourceItemId": "analysis-content-0f2...",
    "sourceVersion": 2,
    "sourceVersionId": "analysis-content-0f2...:v2",
    "role": "dataSource",
    "targetKind": "content",
    "targetSlot": "source"
  }
}
```

Terminal geoprocessing jobs that carry `analysis.content.item_id`,
`analysis.content.version`, and `analysis.content.version_id` persist retained
result-artifact records during terminal callback handling. These records store
metadata, provenance, and the artifact URI produced by the job result package;
they do not copy artifact bytes into the analysis-content tables. The binding
fields are intentionally stable so downstream maps, dashboards, reports, apps,
and workflows can reference `artifactId` plus the source content version
instead of duplicating preview or job state.

## Job Diagnostics

`GET /api/v1/analysis/jobs/{jobId}/logs?limit=` returns the last bounded log
entries. The default limit is `100`, the maximum is `200`, and values less than
or equal to zero return `400`.

```json
{
  "jobId": "failed-job",
  "entries": [
    {
      "timestamp": "2026-05-24T10:00:00Z",
      "level": "Error",
      "message": "validation failed",
      "phase": "validation",
      "metadata": { "code": "invalid-distance" }
    }
  ],
  "totalCount": 2,
  "truncated": true
}
```

The job is resolved through the job service before any log content is returned,
so an unknown `jobId` returns `404` (not `200` with an empty list) and the same
job-read authorization enforced by the failure endpoint is applied first. For a
known job with no execution log store registered, the endpoint returns `200`
with an empty entry list. Messages, phases, and metadata values are
line-normalized, capped to 512 characters, and replaced with a generic failure
message when they look like stack traces, provider internals, connection
strings, or secret-bearing text (secret, token, credential, api key, or bearer
values) — including secret-bearing values stored under otherwise innocuous
metadata keys. Metadata keys containing password, secret, or connection are
omitted; remaining metadata is capped to 20 entries.

`GET /api/v1/analysis/jobs/{jobId}/failure` is only valid for failed or
cancelled jobs. Failed jobs return a safe classification:

```json
{
  "jobId": "failed-job",
  "classification": "validationFailed",
  "message": "validation failed: invalid buffer distance",
  "isTerminal": true,
  "failedAt": "2026-05-24T10:01:00Z"
}
```

Classification values are `validationFailed`, `authorizationDenied`,
`cancelled`, `timedOut`, `artifactOutputFailed`, `executionFailed`,
`storeUnavailable`, and `unknown`. Non-failed, non-cancelled jobs return
`409 Conflict`.

## Runtime Notes

Analysis content emits `honua.analysis_content.*` activities for create,
version, preview, submit/rerun, artifact, log, and failure flows. The
`honua.analysis_content.operations_total` counter is tagged by operation and
`success` or `error`; bounded version-conflict retries are logged before a
final `409` is returned.

Saved-query previews are high-cardinality ad hoc feature queries, so the API
does not add exact-response caching for preview responses. Persisted artifact
records are metadata and binding handles only; artifact bytes remain in the
backing artifact or job-result store.

## Storage Notes

Postgres-backed deployments store this surface in migration
`035_CreateAnalysisContent.sql`:

- `honua.analysis_content_items`
- `honua.analysis_content_versions`
- `honua.analysis_result_artifacts`

The server also registers an in-memory store as a fallback when no provider
overrides `IAnalysisContentStore`. Postgres registration replaces it in normal
PostGIS deployments.

Related contracts:

- [Console Job Observability](console-job-observability.md) documents the
  broader `/api/v1/admin/jobs/**` operator job surface.
- [Analysis Report Endpoints](../operator/CONTROL_PLANE_API.md#analysis-report-endpoints)
  document report retrieval and rendering over completed analysis jobs.
