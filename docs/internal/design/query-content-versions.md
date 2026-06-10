# Saved-Query Content Versions — Backend Scoping & Handoff

**Status:** Proposed · scoping for implementation handoff
**Issue:** honua-server#1182
**Owner (UI side):** honua-console Studio query builder (the saved-query open / save / version-history surface)
**Audience:** the engineer/agent implementing the honua-server side
**Goal:** give the Studio query builder a complete read / list / save / **version-list** contract over
durable `savedQuery` content, so it can open a saved query, save a new immutable version, and render a
**version history** instead of the honest missing-binding state it shows today.

---

## 1. TL;DR — what this is and is not

This is **not** a greenfield "build a content store" task. honua-server already ships the durable
saved-query store, its versioned domain model, and most of its admin endpoints:

- A versioned content store, `IAnalysisContentStore`
  (`src/Honua.Core/Features/AnalysisContent/Abstractions/IAnalysisContentStore.cs`) that already models
  an **item root + immutable versions + current-version pointer**, with `CreateItemAsync`,
  `AddVersionAsync`, `GetVersionAsync(itemId, version?)`, **`ListVersionsAsync(itemId)`**, and
  `ListItemsAsync(query)`. This is the same item/version shape the analysis-package family (#1237) uses.
- The domain contracts (`src/Honua.Core/Features/AnalysisContent/Domain/AnalysisContentContracts.cs`):
  `AnalysisContentItem`, `AnalysisContentVersion`, `AnalysisContentKind.SavedQuery`, and the
  **`SavedQueryContent`** payload (`NaturalLanguageQuery`, `LayerId`, `ServiceName`, `FilterPlan`,
  `OutFields`, `OutputSrid`, `PreviewLimit`, `OutputFormat`, `Units`, `Metadata`). Versions carry a stable
  `ContentHash`, `BasedOnVersionId`, and provenance (`CreatedFromJobId` / `CreatedFromArtifactIds`).
- A Postgres store (`src/Honua.Postgres/Features/AnalysisContent/PostgresAnalysisContentStore.cs`) and an
  in-memory store (`src/Honua.Ai/Features/AnalysisContent/InMemoryAnalysisContentStore.cs`).
- A mapped admin endpoint group (`src/Honua.Ai/Features/AnalysisContent/AnalysisContentEndpoints.cs`)
  under `/api/v{version}/analysis/content`, `RequireAdminAuthorization()`, that already serves create-item,
  get-item, list-items, get-version (latest/explicit), create-version, estimate, preview, run, rerun.

So most of #1182 already exists. **The work is three bounded gaps the Studio query builder hits:**

1. **No version-list endpoint.** The store exposes `ListVersionsAsync`, but no HTTP route maps it. The
   query builder's "version history" panel has nothing to bind to. **This is the headline gap.**
2. **No version diff.** The builder wants to show what changed between two saved versions (the
   `FilterPlan` / `outFields` / projection delta). Today a client must fetch two versions and diff
   client-side.
3. **A `savedQuery`-scoped open/list confirmation.** `ListItemsAsync` already accepts `kind=savedQuery`,
   so listing only saved queries works today — this doc just freezes the exact shape the builder binds.

The console UI is already implemented against the §4 contract using the repo's standard missing-binding
pattern: until the version-list (and optionally diff) routes exist, the version-history panel renders an
honest "version history isn't available on this server yet" state — it is not a mock.

---

## 2. Existing pieces to reuse (do not reinvent)

| Concern | Existing type / file | Reuse as |
| --- | --- | --- |
| Versioned store | `IAnalysisContentStore` · `Honua.Core/Features/AnalysisContent/Abstractions` | Already has `ListVersionsAsync`; **just map it to HTTP** |
| Domain model | `AnalysisContentItem` / `AnalysisContentVersion` / `SavedQueryContent` · `…/Domain/AnalysisContentContracts.cs` | Wire payloads verbatim |
| Postgres + in-memory stores | `PostgresAnalysisContentStore`, `InMemoryAnalysisContentStore` | No store change required for the list endpoint |
| Endpoint group | `AnalysisContentEndpoints.MapAnalysisContentEndpoints` · `Honua.Ai/Features/AnalysisContent` | Add the `GET …/versions` (and `…/diff`) routes alongside the existing ones |
| Service seam | `IAnalysisContentService` (`AnalysisContentService.cs`) | Add `ListVersionsAsync` + (optional) `DiffVersionsAsync` passthroughs |
| JSON source-gen | `AnalysisContentApiJsonContext` | Add the new list/diff response DTOs |
| Error mapping | `TryMapException` in `AnalysisContentEndpoints` (maps validation→400, not-found→404, conflict→409, store-unavailable→503) | Reuse unchanged |
| Field-validation errors | `FieldValidationError` + `ProblemDetailsHelpers.CreateValidationProblem` | Reuse for bad version refs |

---

## 3. What exists vs the gap (precise)

| Capability | Route today | State |
| --- | --- | --- |
| Create saved-query item + v1 | `POST /api/v{version}/analysis/content/items` | **Exists** |
| Open item (latest) | `GET …/items/{itemId}` | **Exists** |
| List items (filter by `kind=savedQuery`, `lifecycle`, paging) | `GET …/items` | **Exists** |
| Open latest version | `GET …/items/{itemId}/versions/latest` | **Exists** |
| Open explicit version | `GET …/items/{itemId}/versions/{version}` | **Exists** |
| Save new immutable version | `POST …/items/{itemId}/versions` | **Exists** |
| Preview a saved-query version | `POST …/items/{itemId}/versions/{version}/preview` | **Exists** |
| **List all versions of an item** | — | **GAP (store method exists, route does not)** |
| **Diff two versions** | — | **GAP (optional, builder convenience)** |

`IAnalysisContentStore.ListVersionsAsync(itemId)` already returns versions in ascending order, so the list
endpoint is a thin adapter. The diff is server-side convenience over the same data.

---

## 4. The console wire contract (FROZEN — build against this)

All routes are admin (`RequireAdminAuthorization()`), api-version 1.0, JSON camelCase, and live in the
existing `/api/v{version}/analysis/content` group.

> **Envelope note (important).** The console/workflow families wrap responses in `ApiResponse<T>`
> (`success` / `data` / `message` / `timestamp`, `src/Honua.Hosting/Features/Models/ApiResponse.cs`). The
> **analysis-content family this contract extends returns the DTO directly** via `Results.Json(<dto>)` and
> maps failures to RFC-7807 ProblemDetails through `TryMapException` (see the existing handlers in
> `AnalysisContentEndpoints.cs`). The new routes MUST match the family they live in (**DTO-direct, not
> `ApiResponse<T>`**) so the console's existing analysis-content client binds them unchanged. Do not
> introduce a second envelope inside this group.

### 4.1 `GET /api/v{version}/analysis/content/items/{itemId}/versions` — NEW (the unblock)

Lists every immutable version of a content item in ascending version order (the version-history panel).

Response (`AnalysisContentVersionListResponse`):

```jsonc
{
  "itemId": "sq_3f9c…",
  "kind": "savedQuery",
  "currentVersion": 4,
  "currentVersionId": "ver_a81…",
  "totalCount": 4,
  "versions": [
    {
      "versionId": "ver_001…",
      "itemId": "sq_3f9c…",
      "version": 1,
      "kind": "savedQuery",
      "contentHash": "sha256:…",
      "basedOnVersionId": null,
      "createdFromJobId": null,
      "createdFromArtifactIds": [],
      "createdAt": "2026-05-01T12:00:00Z",
      "createdBy": "mike@honua.io",
      "summary": {
        "layerId": 1,
        "serviceName": "parcels_fs",
        "outFields": ["objectid", "apn"],
        "hasFilterPlan": true,
        "previewLimit": 100,
        "outputFormat": "geojson"
      }
    }
    // … v2, v3, v4 …
  ]
}
```

- Each entry is the existing `AnalysisContentVersion` metadata **without** the full inline `savedQuery`
  payload — only metadata + a compact `summary`, so a long history list stays cheap. The builder fetches
  the full payload with the existing `GET …/versions/{version}` when the user opens a version.
- `summary` is a small projection of `SavedQueryContent`: `layerId`, `serviceName`, `outFields` (capped at
  e.g. 25), `hasFilterPlan`, `previewLimit`, `outputFormat`. It deliberately omits the full `FilterPlan`.
- The route is **kind-agnostic** because the store is. For an `analysisPackage` item it returns
  `kind:"analysisPackage"` with an analysis-shaped summary (step counts). Studio query builder only
  requests `savedQuery` items, but keep the route general so #1237 reuses it.
- `404` when `itemId` is unknown (`AnalysisContentNotFoundException` → existing mapping).

### 4.2 `GET /api/v{version}/analysis/content/items/{itemId}/versions/diff?from={a}&to={b}` — NEW (optional)

Server-computed structured diff between two saved-query versions so the builder shows "what changed"
without re-deriving it. Defaults: `from` = the version immediately before `to`; `to` = current version.

Response (`SavedQueryVersionDiffResponse`):

```jsonc
{
  "itemId": "sq_3f9c…",
  "fromVersion": 3, "fromVersionId": "ver_003…",
  "toVersion": 4,   "toVersionId": "ver_004…",
  "changed": true,
  "fields": [
    { "field": "filterPlan",   "changeKind": "modified", "from": "status = 'open'", "to": "status = 'open' AND area > 100" },
    { "field": "outFields",    "changeKind": "added",    "from": null,  "to": "area" },
    { "field": "previewLimit", "changeKind": "modified", "from": "100", "to": "250" }
  ]
}
```

- `changeKind` ∈ `added` | `removed` | `modified` (string literals, camelCase).
- `from`/`to` are human-readable string projections (the builder renders a side-by-side); the structured
  `FilterPlan` remains available via the per-version endpoint.
- Validation: unknown `from`/`to` version → `400` with a `FieldValidationError` (`path: "from"` / `"to"`)
  through the existing `AnalysisContentValidationException` mapping.

### 4.3 Save / list-items (already exist — restated so the builder's bindings are unambiguous)

- **Save a new version:** `POST …/items/{itemId}/versions` with body
  `{ "savedQuery": { … SavedQueryContent … }, "basedOnVersionId": "ver_003…" }`. Returns `201` with the new
  `AnalysisContentVersionResponse` (item root advanced to the new `currentVersion`). The server computes
  the `contentHash`. **Decision:** do **not** reject a no-op re-save (identical hash to current) — the
  builder relies on monotonic versions for undo history. State this explicitly in the endpoint summary.
- **List saved queries:** `GET …/items?kind=savedQuery&lifecycle=active&limit=50&offset=0`. The builder's
  "open saved query" picker binds this. `kind` accepts the JSON wire name `savedQuery` (the handler parses
  both wire and CLR names — see `TryParseKind`).

---

## 5. Auth, config, secrets

- **Auth:** all routes inherit the group's `RequireAdminAuthorization()`. No new policy. The Studio query
  builder is an admin/console surface; the console already sends the admin key (`X-API-Key`).
- **Config / secrets:** none. The list/diff endpoints add no config and touch no secrets — they read the
  same durable store already provisioned (Postgres in production, in-memory for tests/dev). No new
  connection, no new env var.
- **Provider:** the saved-query store is provider-neutral (it stores query *definitions*, not data), so it
  works regardless of `DataSource:Provider`. The *preview* path (existing) is the only one that touches a
  data provider, and it already exists.

---

## 6. Build order (suggested)

1. **Service passthrough.** Add `ListVersionsAsync(itemId, ct)` to `IAnalysisContentService` /
   `AnalysisContentService` delegating to `IAnalysisContentStore.ListVersionsAsync` and projecting the
   compact `summary`. (No store change.)
2. **Map `GET …/versions`** in `AnalysisContentEndpoints` next to the existing version routes; add
   `AnalysisContentVersionListResponse` + the compact summary DTO to `AnalysisContentApiJsonContext`; add
   the route to `EndpointRegistry.cs` (API-surface coverage hard gate). **This alone turns the version-history
   panel green.**
3. **Diff (optional).** Add `DiffVersionsAsync` to the service (resolve both versions via the store, project
   the `SavedQueryContent` field deltas), map `GET …/versions/diff`, add `SavedQueryVersionDiffResponse` to
   the JSON context + `EndpointRegistry.cs`.
4. **Tests.** Mirror `tests/dotnet/Honua.Server.Tests/Features/AnalysisContent/AnalysisContentEndpointsTests.cs`
   and `tests/dotnet/Honua.Postgres.Tests/Features/AnalysisContent/PostgresAnalysisContentStoreTests.cs`:
   list-empty, list-after-N-saves (ascending order, `currentVersion` correct), 404 unknown item; diff
   default range, explicit range, 400 unknown version.

Step 2 is the unblock; step 3 raises fidelity.

---

## 7. Endpoints to register (admin, `RequireAdminAuthorization()`, api-version 1.0)

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/v{version}/analysis/content/items/{itemId}/versions` | `AnalysisContentVersionListResponse` |
| `GET` | `/api/v{version}/analysis/content/items/{itemId}/versions/diff?from&to` | `SavedQueryVersionDiffResponse` |

Register in `AnalysisContentEndpoints.cs`, add to `EndpointRegistry.cs`, add DTOs to
`AnalysisContentApiJsonContext`. Logging: reuse the existing `LogEndpointFailed` shape with operations
`analysis-content.versions.list` and `analysis-content.versions.diff`.

---

## 8. Cross-repo

- **honua-console** — Studio query builder binds the §4 routes through its existing analysis-content client.
  Until the version-list route ships, the version-history panel shows the missing-binding state. No console
  change is required when the routes land — they will simply bind.
- **honua-server** — this document; see also the existing admin-API reference `docs/admin-api/analysis-content.md`.
  Keep the §4 wire contract stable; if it must change, change it here and ping the console side.
