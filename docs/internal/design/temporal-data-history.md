# Temporal Data History ("git over data") — Backend Scoping & Handoff

**Status:** Proposed · scoping for implementation handoff
**Issues:** honua-server#1166 (as-of/diff/timeline), #1167 (disconnected-sync conflict review), #1285,
#1287 (governed rollback + attribution)
**Owner (UI side):** honua-console `/operate/temporal` (as-of / diff / per-feature timeline + governed
rollback) and `/operate/sync` (disconnected-sync conflict review)
**Audience:** the engineer/agent implementing the honua-server side
**Goal:** complete the temporal "git over data" read surface (diff, per-feature timeline, attribution),
add **governed rollback** through the job runner, and confirm the **disconnected-sync conflict review**
contract, so `/operate/temporal` and `/operate/sync` bind to real endpoints instead of the honest
missing-binding state they show today.

---

## 1. TL;DR — what this is and is not

This is **not** a greenfield "build a history store" task. honua-server already ships slice 1 of the
temporal surface and the full disconnected-sync conflict-review surface, all built on the **existing change
tracker** — no parallel history store:

- **Temporal slice 1 (read):** `ITemporalHistoryService`
  (`src/Honua.Core.Abstractions/Features/Temporal/Abstractions/ITemporalHistoryService.cs`) with
  `GetCapabilityAsync` + `ReadAsOfAsync`, mapped by `TemporalHistoryEndpoints`
  (`src/Honua.Server/Features/Temporal/TemporalHistoryEndpoints.cs`) at
  `/api/v{version}/temporal/services/{serviceId}/layers/{layerId}` under a dedicated
  `RequireTemporalHistoryRead()` policy. Contracts in `TemporalContracts.cs`:
  `TemporalCapability` (with a **`TemporalDeferredCapabilities`** record that already declares
  `SupportsDiff`/`SupportsTimeline`/`SupportsAttribution`/`SupportsRollback` as `false`), `TemporalAsOfResult`,
  `TemporalFeatureState`, `TemporalCursorKind` (generation now; timestamp reserved).
- **The change tracker primitive:** `IChangeTracker`
  (`src/Honua.Core/Features/FeatureStore/Abstractions/IChangeTracker.cs`) backed by
  `PostgresChangeTracker` — monotonic generations, the substrate for as-of, diff, and timeline.
- **Disconnected-sync conflict review (#1167):** **fully implemented** in `ReplicaManagementEndpoints`
  (`src/Honua.Server/Features/Admin/ReplicaManagementEndpoints.cs`) at
  `/api/v{version}/admin/services/{serviceId}/replicas` — list/get replicas, list/get conflicts, and
  **resolve** with operator actions (`acceptClient`/`keepServer`/`mergeFields`/`chooseGeometry`/
  `rejectClient`/`defer`), all `ApiResponse<T>`, audited, generation-linked. Repos:
  `IReplicaRepository`, `IReplicaConflictRepository`
  (`src/Honua.Core/Features/FeatureStore/Abstractions/`). DTOs in `ReplicaConflictModels.cs` /
  `ReplicaManagementModels.cs`.

So `/operate/sync` is essentially **done server-side**, and `/operate/temporal` has its read foundation.
**The work is the deferred temporal slices:**

1. **Diff** between two generation cursors (`#1166`).
2. **Per-feature timeline** — the ordered change history of one object id (`#1166`).
3. **Attribution** — actor/source/operation per change (`#1166` / `#1285`).
4. **Governed rollback** — revert a layer (or feature) to an earlier generation, executed through the
   **job runner** with approval/audit (`#1285` / `#1287`), never a parallel lifecycle.

The deferred capabilities are already named in `TemporalDeferredCapabilities`; this doc turns each `false`
into a real endpoint and flips the capability flag when implemented.

---

## 2. Existing pieces to reuse (do not reinvent)

| Concern | Existing type / file | Reuse as |
| --- | --- | --- |
| History service seam | `ITemporalHistoryService` · `Honua.Core.Abstractions/Features/Temporal` | Extend with `DiffAsync`, `ReadTimelineAsync`; rollback goes through jobs, not here |
| Endpoint group | `TemporalHistoryEndpoints` · `Honua.Server/Features/Temporal` | Add `…/diff`, `…/timeline`, `…/rollback` routes alongside `capabilities`/`as-of` |
| Capability flags | `TemporalDeferredCapabilities` (`SupportsDiff`/`SupportsTimeline`/`SupportsAttribution`/`SupportsRollback`) | Flip each to `true` as the matching endpoint ships — clients negotiate on these |
| Change tracker | `IChangeTracker` / `PostgresChangeTracker` | The data source for diff/timeline; do **not** add a second history store |
| As-of shapes | `TemporalAsOfResult`, `TemporalFeatureState`, `TemporalChangeKind` | Diff/timeline rows reuse `TemporalChangeKind` + object-id identity |
| Cursor model | `TemporalCursorKind.Generation` (timestamp deferred) | Diff/timeline use generation cursors; reject timestamp with 400 (consistent with as-of) |
| Error mapping | `TemporalHistoryEndpoints.TryMapException` (validation→400, not-found→404, not-supported→409) | Reuse unchanged |
| Auth policy | `RequireTemporalHistoryRead()` (read); `RequireAdminAuthorization()` (rollback mutation) | Reads keep the history-read policy; rollback uses admin + approval |
| Rollback execution | `Honua.Jobs` / `Honua.Geoprocessing` job runner | Rollback is a governed job (per the service-seam remark), not an inline mutation |
| Sync conflict review | `ReplicaManagementEndpoints` + `IReplicaConflictRepository` | **Already complete** — `/operate/sync` binds it as-is |

---

## 3. What exists vs the gap (precise)

| Capability | Route today | State |
| --- | --- | --- |
| Temporal capability discovery | `GET /api/v{version}/temporal/services/{serviceId}/layers/{layerId}/capabilities` | **Exists** |
| As-of read (generation cursor) | `GET …/as-of?generation=&limit=` | **Exists** |
| Replica list / detail | `GET /api/v{version}/admin/services/{serviceId}/replicas[/{replicaId}]` | **Exists** |
| Conflict list / detail | `GET …/replicas/{replicaId}/conflicts[/{conflictId}]` | **Exists** |
| Conflict resolve | `POST …/replicas/{replicaId}/conflicts/{conflictId}/resolve` | **Exists** |
| **Diff between two generations** | — | **GAP (#1166)** |
| **Per-feature timeline** | — | **GAP (#1166)** |
| **Attribution (actor/source/op)** | — | **GAP (#1166/#1285)** |
| **Governed rollback** | — | **GAP (#1285/#1287)** |

---

## 4. The console wire contract

### 4.1 Temporal reads — envelope note

The temporal group returns the **DTO directly** (`Results.Json(<dto>)`) and maps failures to RFC-7807
ProblemDetails via `TryMapException` — the same convention as the analysis-content family, NOT
`ApiResponse<T>`. New temporal read routes MUST match this (DTO-direct). Reads inherit
`RequireTemporalHistoryRead()`. Diff/timeline use **generation cursors only**; a timestamp cursor returns
`400` exactly as `as-of` does.

### 4.2 `GET …/layers/{layerId}/diff?from={a}&to={b}&limit={n}` — NEW (#1166)

Collapsed net changes between two generation cursors (the `/operate/temporal` "compare two points" view).

```jsonc
{
  "serviceId": "parcels", "layerId": 0,
  "fromGeneration": 41, "toGeneration": 58,
  "currentGeneration": 58,
  "features": [
    { "objectId": 1207, "operation": "Update", "changedAt": "2026-05-12T09:31:00.000Z",
      "attributes": { "status": "closed" } },
    { "objectId": 1318, "operation": "Insert", "changedAt": "2026-05-12T10:02:00.000Z",
      "attributes": { "apn": "12-345-678" } },
    { "objectId": 1099, "operation": "Delete", "changedAt": "2026-05-12T10:40:00.000Z", "attributes": null }
  ]
}
```

- Reuses `TemporalChangeKind` (`Insert`/`Update`/`Delete`) and `TemporalFeatureState` semantics (delete →
  `attributes:null`, since the tracker does not retain pre-delete attributes in this model — same caveat as
  as-of).
- `from` defaults to the layer's earliest tracked generation; `to` defaults to current.
- `400` for a timestamp cursor or `from > to`; `409` (`TemporalNotSupportedException`) when the layer lacks
  a temporal column / non-no-op tracker.
- Flip `TemporalDeferredCapabilities.SupportsDiff = true` in `GetCapabilityAsync` once shipped.

### 4.3 `GET …/layers/{layerId}/features/{objectId}/timeline?limit={n}` — NEW (#1166)

The ordered change history of a single feature (the `/operate/temporal` per-feature "history" panel).

```jsonc
{
  "serviceId": "parcels", "layerId": 0, "objectId": 1207,
  "currentGeneration": 58,
  "entries": [
    { "generation": 12, "operation": "Insert", "changedAt": "2026-04-02T…Z",
      "attribution": { "actor": "field-app:device-77", "source": "replica:abc", "operation": "createReplica" } },
    { "generation": 41, "operation": "Update", "changedAt": "2026-05-01T…Z",
      "attribution": { "actor": "mike@honua.io", "source": "console", "operation": "edit" } },
    { "generation": 58, "operation": "Update", "changedAt": "2026-05-12T…Z",
      "attribution": null }
  ]
}
```

- `attribution` (#1285) is `{ actor, source, operation }` and is **nullable** per entry — when the change
  tracker did not capture attribution for an older generation, return `null` rather than fabricating it.
  Flip `SupportsAttribution` only when attribution is genuinely captured.
- Entries are ascending by generation. `404` when the object id is unknown to the tracker.
- Flip `SupportsTimeline = true` once shipped.

### 4.4 `POST …/layers/{layerId}/rollback` — NEW, governed (#1285/#1287)

Reverts a layer (or a set of object ids) to an earlier generation. This is a **mutation** → it leaves the
read group's policy and uses `RequireAdminAuthorization()`, and it **executes through the job runner**, per
the `ITemporalHistoryService` remark ("Rollback, when added, must execute through the job runner rather than
a parallel lifecycle"). It does NOT run inline.

Request:

```jsonc
{
  "toGeneration": 41,
  "objectIds": [1207, 1318],          // optional; omit → whole-layer rollback
  "reason": "Reverting bad bulk import",
  "idempotencyKey": "rb-2026-05-12-001"
}
```

Response — `ApiResponse<TemporalRollbackJobResult>` (mutation surface uses the `ApiResponse<T>` envelope,
consistent with admin/job endpoints), returning a `jobId` the console polls:

```jsonc
{ "success": true, "data": {
  "jobId": "job_9f1…",
  "status": "queued",
  "serviceId": "parcels", "layerId": 0,
  "toGeneration": 41, "fromGeneration": 58,
  "affectedFeatureEstimate": 124,
  "requiresApproval": true
}}
```

- The job re-applies inverse operations to reach `toGeneration`, advancing the change tracker forward (a
  rollback is itself a new generation — auditable, reversible). Poll via the existing console job
  observability surface (`docs/design/console-job-observability.md` / `Honua.Jobs`).
- **Governance (#1287):** rollback is gated. `requiresApproval` reflects policy; when approval is required,
  the job parks in a pending-approval state and the console shows an approve/reject affordance (reuse the
  existing job/approval plumbing — do not invent a temporal-local approval lifecycle). Every rollback writes
  an `IAuditLog` event (`temporal.rollback.*`) mirroring `ReplicaManagementEndpoints`' audit shape.
- `409` when the layer does not support history; `400` for an invalid/forward `toGeneration`.
- Flip `SupportsRollback = true` once shipped.

### 4.5 Disconnected-sync conflict review (FROZEN — already built; `/operate/sync` binds as-is)

These exist today in `ReplicaManagementEndpoints`; restated so the console binding is unambiguous. All use
`ApiResponse<T>` and `RequireAdminAuthorization()`.

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/v{version}/admin/services/{serviceId}/replicas` | `ApiResponse<ReplicaManagementListResponse>` |
| `GET` | `…/replicas/{replicaId}` | `ApiResponse<ReplicaManagementDetail>` |
| `GET` | `…/replicas/{replicaId}/conflicts?status=pending|resolved|deferred` | `ApiResponse<ReplicaConflictListResponse>` |
| `GET` | `…/replicas/{replicaId}/conflicts/{conflictId}` | `ApiResponse<ReplicaConflictDetail>` (base/client/server state) |
| `POST` | `…/replicas/{replicaId}/conflicts/{conflictId}/resolve` | `ApiResponse<ReplicaConflictResolutionResponse>` |

Resolve body: `{ "action": "acceptClient" | "keepServer" | "mergeFields" | "chooseGeometry" | "rejectClient" | "defer" }`.
The response's `committedNewServerState` is `true` for `acceptClient`/`mergeFields`/`chooseGeometry`
(these advance the server generation — the temporal linkage). Providers that cannot support manual review
return `501` (`IReplicaConflictRepository.SupportsConflictReview == false`); already-resolved conflicts
return `409`. Conflict types: `attribute`/`geometry`/`deleteUpdate`/`updateDelete`/`duplicateInsert`/
`attachment`/`relationship`.

> `/operate/sync` requires **no new server work** beyond confirming the console binds these. If the page
> still shows missing-binding, the gap is console-side wiring, not the server contract.

---

## 5. Auth, config, secrets

- **Reads** (`capabilities`/`as-of`/`diff`/`timeline`) — `RequireTemporalHistoryRead()`, the distinct
  history-read policy that already exists, so history reads authorize separately from current reads and admin
  mutations.
- **Rollback** — `RequireAdminAuthorization()` plus the governance gate (#1287). Approval/audit reuse the job
  runner and `IAuditLog`.
- **Conflict review** — `RequireAdminAuthorization()` (replica/conflict-review entitlement), already wired.
- **Config / secrets:** none new. Everything reads the existing change tracker / replica repositories. The
  temporal surface requires a configured temporal column on the storage mapping and a non-no-op change
  tracker for the provider — this is the existing capability gate, not new config.
- **Provider support:** read-only/analytics providers report `SupportsHistory:false` (capabilities) and
  `SupportsConflictReview:false` (conflict review → `501`). Honour these — never fabricate history for a
  provider that cannot track changes.

---

## 6. Build order (suggested)

1. **Diff.** Add `DiffAsync(serviceId, layerId, from, to, limit)` to `ITemporalHistoryService` over
   `IChangeTracker`; map `GET …/diff`; add the response DTO to the temporal JSON context; flip
   `SupportsDiff`. **This is the highest-value `/operate/temporal` unblock.**
2. **Timeline.** Add `ReadTimelineAsync(serviceId, layerId, objectId, limit)`; map
   `GET …/features/{objectId}/timeline`; flip `SupportsTimeline`.
3. **Attribution.** Extend the change-tracker capture (or surface what it already records) to populate
   `attribution`; flip `SupportsAttribution` only when genuinely captured.
4. **Governed rollback.** Add the rollback job (Honua.Jobs/Geoprocessing), the `POST …/rollback` admin
   route returning a `jobId`, approval gating (#1287), and audit; flip `SupportsRollback`.
5. **`/operate/sync`:** confirm the console binds the existing conflict-review routes (no server work).
6. **Tests.** Mirror the existing temporal/replica tests: diff over N generations, timeline ordering,
   attribution-null for legacy generations, rollback job lifecycle + audit, capability flags flip true.

Register every new route in `EndpointRegistry.cs` (API-surface hard gate) and add DTOs to the source-gen
context. Telemetry: reuse the temporal `LogEndpointFailed` shape (`temporal.diff`, `temporal.timeline`,
`temporal.rollback`).

---

## 7. Cross-repo

- **honua-console** — `/operate/temporal` binds the §4.2–§4.4 routes; `/operate/sync` binds §4.5 (already
  available server-side). Until diff/timeline/rollback ship, `/operate/temporal` shows the missing-binding
  state; the console negotiates on `TemporalDeferredCapabilities`, so flipping each flag is what lights up
  each panel.
- **honua-server** — this document. Keep the generation-cursor model and the DTO-direct read convention
  stable; rollback stays a governed job.
