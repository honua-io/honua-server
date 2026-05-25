# Temporal History API

Honua exposes optional temporal data-history endpoints for layers whose catalog
metadata declares a `TemporalSource`. This surface is for immutable source
history: capability discovery, as-of reads, checkpoint lists, diffs,
per-feature timelines, rollback planning, and approved rollback submission.
Responses are JSON with camelCase property names, string enum values, and null
properties omitted.

This is distinct from named-version edit branching, reconcile, and post
workflows tracked in honua-server#371. A named version may reference a temporal
cursor when it is posted, but the temporal-history API does not own version
graphs or branch lifecycle.

## Endpoints

All endpoints are scoped to API v1 and a layer id:

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/v1/layers/{layerId}/history/capabilities` | Discover source kind, supported operations, retention, attribution, geometry history, and schema evolution policy. |
| `GET` | `/api/v1/layers/{layerId}/history/checkpoints` | List named or derived checkpoints, newest first. |
| `GET` | `/api/v1/layers/{layerId}/history/items?at={cursor}` | Query a deterministic as-of snapshot. |
| `GET` | `/api/v1/layers/{layerId}/history/diff?from={cursor}&to={cursor}` | Return summary counts plus a page of added, removed, attribute-changed, and geometry-changed features. |
| `GET` | `/api/v1/layers/{layerId}/history/items/{featureId}/timeline` | Return a feature revision timeline with field-level changes and attribution when policy permits it. |
| `GET` | `/api/v1/layers/{layerId}/history/rollback-plan?to={cursor}` | Report rollback feasibility, affected count, findings, approval, script, job, or manual requirements. |
| `POST` | `/api/v1/layers/{layerId}/history/rollback` | Submit an approved rollback as a durable job-backed corrective operation. |

Non-temporal layers return `404`. Client-correctable temporal errors return
problem details without exposing raw SQL, table names, filesystem paths, or
provider internals. Invalid cursors and unsupported per-layer operations return
`400`; unauthorized requests return the shared `401` or `403` access response;
blocked, script-required, and manual rollback execution attempts return `409`;
and deployments without an execution job store return `503` for rollback
execution.

## Query Parameters And Paging

`at`, `from`, and `to` accept either a temporal cursor token or a bare RFC 3339
timestamp. Bare timestamps are normalized to `ts:` cursors before execution.

`limit` is supported on checkpoints, as-of reads, diffs, and timelines. The
default is `100` and the maximum accepted page size is `1000`; non-positive
values fall back to the default. As-of, diff, and timeline responses include a
`next` continuation token when another page exists. Send that value back as
`cursor` on the same endpoint with the same temporal cursor parameters.

## Cursors

Temporal cursors are stable text tokens. Timestamp cursors use the `ts:` prefix,
for example:

```text
ts:2024-01-01T00:00:00.0000000Z
```

The model also reserves these non-timestamp cursor prefixes:

| Prefix | Meaning |
|---|---|
| `txn:` | Backend transaction identifier. |
| `named:` | Operator-assigned named checkpoint. |
| `release:` | GitOps metadata release identifier. |
| `job:` | Honua job-run identifier. |
| `edit:` | Edit-session correlation identifier. |

Providers resolve supported references to a UTC instant before reading history.
Postgres audit-log sources resolve `source_ref` or `correlation_id` references;
Postgres temporal-table sources expose timestamp cursors only.

## Response Contract

Capability discovery returns:

- `layerId`
- `supportsAsOf`, `supportsHistory`, `supportsDiff`, `supportsTimeline`
- `supportsRollbackPlan`, `supportsRollbackExecution`
- `supportsGeometryHistory`, `supportsAttribution`
- `sourceKind`: `AuditLog`, `TemporalTable`, or `DeltaLog`
- `retentionPolicy`, `attributionFields`, `schemaEvolution`, `geometrySrid`
- `warnings` for non-fatal withdrawn capabilities, such as a missing index

Checkpoint enumeration returns `{ "layerId": 1, "checkpoints": [...] }`.
Each checkpoint has `cursor`, `label`, `timestamp`, and optional `kind`.

As-of reads return a `TemporalSnapshot` with `layerId`, `at`, `resolvedAt`,
`generatedAt`, optional `srid`, `items`, and optional `next`. Each item has
`id`, optional GeoJSON `geometry`, and an `attributes` object. Items are ordered
deterministically by feature id.

Diff reads return `layerId`, `from`, `to`, `summary`, `items`, and optional
`next`. Summary fields are `added`, `removed`, `attributeChanged`, and
`geometryChanged`. Each item has `featureId`, `changeKind`, `geometryChanged`,
`fieldChanges`, optional `attribution`, and optional `operationRef`.
`changeKind` values are `Added`, `Removed`, `AttributeChanged`, and
`GeometryChanged`.

Timeline reads return `layerId`, `featureId`, `attributionMasked`, `revisions`,
and optional `next`. Revisions are newest first and include `cursor`,
`operation`, optional `attribution`, `fieldChanges`, and `geometryChanged`.

Rollback plans return `layerId`, `to`, `mode`, `isSupported`,
`affectedCount`, `requiresApproval`, `requiresJob`, `requiresScript`,
`validationFindings`, and `compatibilityFindings`. `mode` values are
`Supported`, `Blocked`, `ScriptRequired`, `JobRequired`, and `Manual`.
Findings carry stable `code`, `severity`, and client-safe `message` fields.

Approved rollback execution returns `202 Accepted` with `layerId`, `jobId`,
`to`, `status`, `mode`, and `affectedCount`. The submitted body is:

```json
{
  "to": "ts:2024-01-01T00:00:00.0000000Z",
  "approved": true,
  "reason": "Restore prior approved import state"
}
```

## Permissions

Temporal history has separate authorization decisions:

| Operation | Policy source |
|---|---|
| Capability discovery | current layer read policy |
| Checkpoints and as-of reads | `HistoryReadRoles` or current read fallback |
| Diffs | `DiffReadRoles` or history-read fallback |
| Timelines | `TimelineReadRoles` or history-read fallback |
| Rollback plans | `RollbackPlanRoles`, then `HistoryReadRoles`, then current read fallback |
| Rollback execution | `RollbackExecuteRoles` or layer write fallback |

`MaskAttribution` omits actor/source/correlation fields from diff and timeline
responses while preserving the revision timestamp when available.

## Layer Configuration

Temporal history is opt-in per layer through `CatalogMetadata.TemporalSource`.
The configuration is independent of normal protocol time filters and does not
create named-version branches.

Relevant fields:

| Field | Notes |
|---|---|
| `sourceKind` | `AuditLog`, `TemporalTable`, or `DeltaLog`; the Postgres provider currently implements audit-log and temporal-table reads. |
| `historyTableName` | Audit-log table name; defaults to `{tableName}_history`. |
| `systemPeriodColumn` | Temporal-table `tstzrange` column; defaults to `sys_period`. |
| `attribution` | Column mapping for feature id, operation, changed-at, actor, source reference, correlation id, before/after attributes, and geometry. |
| `geometryHistory` | Controls whether recorded geometry history is exposed. |
| `retentionPolicy` | Optional ISO 8601 duration advertised to clients. |
| `schemaEvolution` | `Fixed`, `Additive`, or `Compatible`; non-fixed rollback requires an operator-managed script. |
| `allowRollback` | Enables rollback planning/execution policy, subject to runtime feasibility. |
| `accessPolicy` | Optional history-specific role sets and attribution masking. |

## Postgres Sources

The Postgres provider supports audit-log sources and temporal-table sources.
Audit-log as-of, diff, and automatic rollback support requires an index whose
leading columns are the configured feature id and changed-at columns. If the
configured table exists but the index is missing, capability discovery keeps
history/timeline visible and withdraws as-of and diff support with a warning.
Temporal-table range reads use the configured system-period column and do not
support named reference resolution.

Geometry returned by as-of, diff, and timeline responses stays in the layer's
configured source SRID. The temporal-history path does not reproject geometry.

## Rollback

Rollback is append-only. `POST /history/rollback` validates the requested target,
requires explicit approval, creates an `ExecutionJobKind.TemporalRollback` job,
and enqueues that job when a queue is available. The worker applies a forward
corrective operation, appends new history rows, and emits a
`temporal-checkpoint:{cursor}` job artifact. Existing history rows are not
deleted.

Plans return `ScriptRequired` when automatic corrective rows cannot safely cover
the declared schema-evolution policy. Those plans are intentionally not accepted
by the generic rollback endpoint; operators must run a governed migration script
or manual workflow for that target.

Automatic rollback execution is available only for supported audit-log sources
with `allowRollback=true`, a fixed schema-evolution policy, a post-change
attribute column, and a valid as-of index. Plans with `Supported` or
`JobRequired` modes can be accepted after explicit approval; `Blocked`,
`ScriptRequired`, and `Manual` modes cannot be submitted to the generic
rollback endpoint.

History reads and diffs are ad hoc data queries. Any client or intermediary
cache must vary by layer id, caller/tenant authorization context, cursor
parameters, `limit`, continuation token, and attribution policy. Rollback
requests and responses are not cacheable.
