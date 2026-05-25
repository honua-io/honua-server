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

Routes that address an individual replica or conflict also require service data
editor access for the replica's service. The top-level list route is an
operator inventory surface and is gated by admin authentication plus the
entitlement.

## Surface

All routes live under `/api/v1/admin/replicas`:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/admin/replicas` | List replicas across services. |
| `GET` | `/api/v1/admin/replicas/{replicaId}` | Get full metadata for one replica. |
| `GET` | `/api/v1/admin/replicas/{replicaId}/conflicts` | List durable sync conflicts. |
| `GET` | `/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}` | Get a conflict with base/client/server states. |
| `POST` | `/api/v1/admin/replicas/{replicaId}/conflicts/{conflictId}/resolve` | Apply a resolution to a pending conflict. |

Responses are plain camelCase JSON DTOs; null fields are omitted.

### List replicas

`GET /api/v1/admin/replicas` returns replicas newest-first. Query parameters:
`serviceId`, `status` (`active` / `stale` / `expired` / `unregistered`),
`limit` (default 50, max 200; zero/negative values fall back to the default),
and `afterReplicaId` (keyset cursor — pass the last `replicaId` from the prior
page). Unknown cursors return an empty page.

Each item carries:

```json
{
  "replicaId": "8cc87a6688144be3a972bc82a507d49d",
  "replicaName": "Field Crew Alpha",
  "serviceId": "water",
  "owner": "operator@example.com",
  "deviceClient": "ArcGIS Pro/3.4",
  "syncModel": "perReplica",
  "syncDirection": "bidirectional",
  "status": "active",
  "createdAt": "2026-05-25T18:45:00+00:00",
  "lastSyncTime": "2026-05-25T19:20:00+00:00",
  "lastSyncGeneration": 1042,
  "pendingConflicts": 2
}
```

`GET /api/v1/admin/replicas/{replicaId}` returns the same metadata plus
`layerIds`, `replicaGeometryJson`, and `branchVersionId`. New `createReplica`
calls set `owner` from the authenticated principal and `deviceClient` from the
request `User-Agent`; older rows may omit those values.

### Conflicts

`GET .../conflicts` returns conflicts newest-first with `pending` (default
`true`), `limit` (default 50, max 200), and `afterId` (keyset cursor). Conflict
summaries include `conflictId`, `syncOpId`, `layerId`, `objectId`,
`conflictType`, `baseGeneration`, `createdAt`, and `resolution` when resolved.
Pending conflicts omit the null `resolution` property.

The conflict detail adds the
base/client/server feature states (`baseFeature`, `clientFeature`,
`serverFeature`), `fieldChanges` entries for attribute-level differences, and a
`geometryChange` summary for geometry presence/equality. It also includes a
`temporalHistoryHref` link into the temporal history API (**#1166**); the link
is present even when that API is not yet deployed.

Conflict types: `attribute`, `geometry`, `update-delete`, `delete-update`,
`delete-delete`, `duplicate-insert`.

Detail response shape:

```json
{
  "conflictId": "1c7c9996-0c5f-4ea7-a510-e0219a5c3a58",
  "replicaId": "8cc87a6688144be3a972bc82a507d49d",
  "syncOpId": "b6dc0db9-4322-4cfe-9565-3a6f19f9fe3c",
  "serviceId": "water",
  "layerId": 0,
  "objectId": 42,
  "conflictType": "attribute",
  "baseGeneration": 1038,
  "clientFeature": { "attributes": { "objectid": 42, "status": "needs-service" } },
  "serverFeature": { "attributes": { "objectid": 42, "status": "operational" } },
  "fieldChanges": [
    {
      "fieldName": "status",
      "clientValue": "needs-service",
      "serverValue": "operational",
      "clientChanged": true,
      "serverChanged": true,
      "clientDiffersFromServer": true
    }
  ],
  "geometryChange": {
    "baseHasGeometry": false,
    "clientHasGeometry": false,
    "serverHasGeometry": false,
    "clientChanged": false,
    "serverChanged": false,
    "clientDiffersFromServer": false
  },
  "createdAt": "2026-05-25T19:20:10+00:00",
  "updatedAt": "2026-05-25T19:20:10+00:00",
  "temporalHistoryHref": "/api/v1/history/water/layers/0/features/42"
}
```

### Resolve

`POST .../resolve` body:

```json
{ "resolution": "accept_client", "mergedPayloadJson": "{...}" }
```

`resolution` is one of `accept_client`, `keep_server`, `merge_fields`,
`reject_client`, `defer`. `mergedPayloadJson` is required for `merge_fields`.
It is a string containing a GeoServices feature JSON payload, not a nested
object.

`accept_client` and `merge_fields` commit a new server feature state through the
canonical edit pipeline (validation, change tracking, audit) for `attribute` and
`geometry` conflicts; the others record the decision without a feature change.
This first slice returns `400 Bad Request` for feature-payload resolutions on
`update-delete`, `delete-update`, `delete-delete`, and `duplicate-insert`
conflicts because object-id-preserving restore/delete resolution is not yet
implemented. Choose `keep_server`, `reject_client`, or `defer` for those
conflict types. Once any resolution is recorded, including `defer`, the conflict
is no longer returned by the default `pending=true` list.

Successful resolution returns:

```json
{ "conflictId": "1c7c9996-0c5f-4ea7-a510-e0219a5c3a58", "resolution": "keep_server" }
```

Every resolution emits a `replica.conflict.resolve` audit event. Resolving an
already-resolved conflict returns `409 Conflict`.

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
- The durable enum reserves `delete-update`, `delete-delete`, and
  `duplicate-insert`, but the current `synchronizeReplica` upload detector emits
  `attribute`, `geometry`, and `update-delete` conflicts from the simplified
  feature-array upload path.
- Attachment and relationship conflict classifiers are not emitted by this
  first slice.
- There is no separate `choose_geometry` resolution action; use
  `accept_client` or `merge_fields` to commit geometry changes.
- There is no conflict retention/cleanup policy yet.
