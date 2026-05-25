# Disconnected Replica Conflict Review API

This admin API exposes named disconnected replicas and a durable, reviewable
record of synchronization conflicts. It is the Esri offline/disconnected-editing
shape: services expose Sync, clients create named replicas, synchronize by
server generations, and conflicting uploads become inspectable, resolvable
records instead of transient sync errors.

Tracked by issue **#1167**. Endpoints live in
`Honua.Server.Features.Admin.ReplicaConflictsEndpoints`; DTOs use
source-generated JSON through `ReplicaConflictsJsonContext` for trimming and
Native AOT compatibility. The durable schema is migration
`037_AddReplicaConflicts.sql`.

## Entitlement

All routes require admin authentication **and** the
`replica.conflict-review` license entitlement (Pro and Enterprise editions).
Requests without the entitlement receive `402 Payment Required` with the shared
admin `application/problem+json` shape.

## Surface

All routes live under `/api/v1/admin/replicas`:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/admin/replicas` | List active replicas across services. |
| `GET` | `/api/v1/admin/replicas/{replicaId}` | Get full metadata for one replica. |
| `GET` | `/api/v1/admin/replicas/{replicaId}/conflicts` | List durable sync conflicts. |
| `GET` | `/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}` | Get a conflict with base/client/server states. |
| `POST` | `/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve` | Apply a resolution to a pending conflict. |

Responses are plain camelCase JSON DTOs; null fields are omitted.

### List replicas

`GET /api/v1/admin/replicas` returns replicas newest-first. Query parameters:
`serviceId`, `status` (`active` / `stale` / `expired` / `unregistered`),
`limit` (default 50, max 200), and `afterReplicaId` (keyset cursor — pass the
last `replicaId` from the prior page). Each item carries `replicaName`, `owner`,
`deviceClient`, `syncModel`, `syncDirection`, `status`, `lastSyncTime`,
`lastSyncGeneration`, and `pendingConflicts`.

### Conflicts

`GET .../conflicts` returns conflicts newest-first with `pending` (default
`true`), `limit`, and `afterId` (keyset cursor). The conflict detail adds the
base/client/server feature states (`baseFeature`, `clientFeature`,
`serverFeature`), `fieldChanges` entries for attribute-level differences, and a
`geometryChange` summary for geometry presence/equality. It also includes a
`temporalHistoryHref` link into the temporal history API (**#1166**); the link
is present even when that API is not yet deployed.

Conflict types: `attribute`, `geometry`, `update-delete`, `delete-update`,
`delete-delete`, `duplicate-insert`.

### Resolve

`POST .../resolve` body:

```json
{ "resolution": "accept_client", "mergedPayloadJson": "{...}" }
```

`resolution` is one of `accept_client`, `keep_server`, `merge_fields`,
`reject_client`, `defer`. `mergedPayloadJson` is required for `merge_fields`.
`accept_client` and `merge_fields` commit a new server feature state through the
canonical edit pipeline (validation, change tracking, audit); the others record
the decision without a feature change. Every resolution emits a
`replica.conflict.resolve` audit event. Resolving an already-resolved conflict
returns `409 Conflict`.

## Conflict detection

`synchronizeReplica` uploads are partitioned: an edit whose object id was
changed on the server since the replica's last-sync generation becomes a durable
conflict record, while non-conflicting edits still apply in the same upload.
When conflicts occur the sync response stays `success: true` (Esri partial-apply
semantics) and adds `conflictCount`, `conflictIds`, and `syncOpId`. Existing
last-write-wins / client-wins / server-wins flows for non-conflicting uploads
are unchanged.

## First-slice limitations

- `baseFeature` (common ancestor) is `null`; it is reserved for population from
  the temporal snapshots in **#1166**.
- `branchVersionId` replica metadata is stored but unused; reconcile/post for
  named versioned editing remains **#371** scope.
- There is no conflict retention/cleanup policy yet.
