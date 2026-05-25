# Temporal History API

Honua exposes optional temporal data-history endpoints for layers whose catalog
metadata declares a `TemporalSource`. This surface is for immutable source
history: capability discovery, as-of reads, checkpoint lists, diffs,
per-feature timelines, rollback planning, and approved rollback submission.

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
provider internals.

## Cursors

Temporal cursors are stable text tokens. Timestamp cursors use the `ts:` prefix,
for example:

```text
ts:2024-01-01T00:00:00.0000000Z
```

The model also reserves non-timestamp cursor kinds for transaction, named
checkpoint, release, job-run, and edit-session references. Providers resolve
those references to a UTC instant before reading history.

## Permissions

Temporal history has separate authorization decisions:

| Operation | Policy source |
|---|---|
| Capability discovery | current layer read policy |
| Checkpoints and as-of reads | `HistoryReadRoles` or current read fallback |
| Diffs | `DiffReadRoles` or history-read fallback |
| Timelines | `TimelineReadRoles` or history-read fallback |
| Rollback plans | `RollbackPlanRoles` or history-read fallback |
| Rollback execution | `RollbackExecuteRoles` or layer write fallback |

`MaskAttribution` omits actor/source/correlation fields from diff and timeline
responses while preserving the revision timestamp when available.

## Postgres Sources

The Postgres provider supports audit-log sources and temporal-table sources.
Audit-log as-of and diff support requires an index whose leading columns are the
configured feature id and changed-at columns. If the configured table exists but
the index is missing, capability discovery keeps history/timeline visible and
withdraws as-of and diff support with a warning.

Geometry returned by as-of, diff, and timeline responses stays in the layer's
configured source SRID. The temporal-history path does not reproject geometry.

## Rollback

Rollback is append-only. `POST /history/rollback` validates the requested target,
requires explicit approval, creates an `ExecutionJobKind.TemporalRollback` job,
and enqueues that job when a queue is available. The worker applies a forward
corrective operation, appends new history rows, and emits a new checkpoint
cursor. Existing history rows are not deleted.

Plans return `ScriptRequired` when automatic corrective rows cannot safely cover
the declared schema-evolution policy. Those plans are intentionally not accepted
by the generic rollback endpoint; operators must run a governed migration script
or manual workflow for that target.
