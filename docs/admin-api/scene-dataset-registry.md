# Scene Dataset Registry (Admin API)

The scene dataset registry is the server-side admin API for registering hosted
3D scene datasets so that hosted serving (#837), protected access envelopes
(#849), and snippet/embed consumers (#838, #532) all work against a single,
durable contract.

This document covers the **server/API slice only**. Visual admin UI work is a
separate `honua-server-admin` ticket and is intentionally out of scope here.

## Endpoint surface

All routes live under `/api/v1/admin/scenes`, require admin authorization, and
return shared admin problem-details for any 4xx/5xx response.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET`    | `/api/v1/admin/scenes`              | List active datasets (`?includeInactive=true` for all). |
| `POST`   | `/api/v1/admin/scenes`              | Register a new scene dataset. Returns `201` and the full record. |
| `GET`    | `/api/v1/admin/scenes/{id}`         | Get a dataset by its database `datasetId` (Guid). |
| `PUT`    | `/api/v1/admin/scenes/{id}`         | Update mutable fields. Returns the new record (revision incremented). |
| `DELETE` | `/api/v1/admin/scenes/{id}`         | Soft-delete (sets status to `inactive`). Returns `204`. |
| `GET`    | `/api/v1/admin/scenes/{id}/resolve` | Returns serving metadata + CesiumJS / `<honua-scene>` snippets. |

`{id}` is the database primary key (`datasetId`, a Guid). The URL slug is
preserved across renames; using `{id}` keeps admin URLs stable.

`DELETE` is **soft deactivation only**. There is no physical-delete endpoint —
historical records remain visible via `?includeInactive=true` and continue to
satisfy unique constraints on `id` and `name`.

## Record fields

| Field | Type | Notes |
| --- | --- | --- |
| `datasetId` | Guid | DB primary key. |
| `id` | string | URL slug — `[a-z0-9-]{1,64}`, no leading/trailing hyphen. |
| `name` | string | Display name; up to 128 characters; must be globally unique. |
| `description` | string? | Optional human-readable description. |
| `assetRoot` | string | Server-side filesystem directory containing the root tileset document. URI schemes, `..`, backslashes, and shell metacharacters are rejected. |
| `tilesetFileName` | string | Defaults to `tileset.json`. Up to 64 characters; relative filename only (no path separators, traversal segments, or shell metacharacters). |
| `datasetType` | string | `hosted_tiles` (default) or `terrain`. |
| `extent` | object? | WGS-84 axis-aligned bounding box `{ xMin, yMin, xMax, yMax }`. Required-paired or omitted entirely. |
| `crs` | string? | Authority token (`EPSG:4979`, `OGC:1`). Geodesy interpretation is the caller's responsibility. |
| `cachePolicy` | object | `{ maxAgeSeconds, noStore }`. `maxAgeSeconds` is bounded to `[0, 86400]`. |
| `editionGate` | string? | Lower-kebab slug consumed by the licensing layer. |
| `requiresAuth` | bool | When true, the hosted serving path refuses anonymous access. |
| `isPublic` | bool | When true, the dataset is publicly readable. Cannot be true together with `requiresAuth`. |
| `allowedRoles` | string[]? | Roles allowed to read a protected dataset. Forwarded to `CatalogMetadata.AccessPolicy.AllowedRoles`. Ignored for public datasets. |
| `status` | string | `active`, `inactive`, or `validation_failed`. |
| `validationMessage` | string? | Optional non-fatal validation message. |
| `revision` | int | Monotonically increasing counter; bumped on every update. |
| `createdAt` / `createdBy` / `updatedAt` | audit fields | UTC. |

## Validation

Validation is shape-only; no live network or filesystem probing is performed.
Containment of `assetRoot` against the canonical serving directory is
enforced by the hosted-serving asset resolver introduced in #837.

| Field | Rule |
| --- | --- |
| `id` | Must be 1–64 chars of `[a-z0-9-]`, not starting or ending with a hyphen. |
| `name` | Required, 1–128 chars, globally unique. |
| `assetRoot` | Non-empty filesystem path; no URI schemes, `..`, `\`, `*`, `?`, `;`, `&`, `|`, `$`, `<`, `>`, `` ` ``, quotes, or null/control bytes. |
| `tilesetFileName` | Optional. When supplied, ≤ 64 chars with no path separators (`/`, `\`), no `..` traversal segments, and no shell metacharacters. Null/whitespace is accepted and falls back to `tileset.json`. |
| `crs` | Null or `[A-Z]+:[0-9]+`. |
| `cachePolicy.maxAgeSeconds` | `0 ≤ value ≤ 86_400`. |
| `editionGate` | Null or 1–32 chars of `[a-z0-9-]`. |
| `extent` | Optional; when supplied, **all four bounds must be present** (`xMin`, `yMin`, `xMax`, `yMax`) — partial payloads are rejected rather than silently defaulting missing bounds to zero. Each bound must be finite, in WGS-84 ranges, and `min ≤ max`. |
| `isPublic` + `requiresAuth` | Cannot both be true. |

Failures return `400 Bad Request` shaped as
`application/problem+json` (admin problem type).

## Update semantics

`PUT /api/v1/admin/scenes/{id}` accepts a partial update payload: every field
is optional, and any field that is omitted (or sent as `null`) is treated as
"keep the current value". Updates always succeed against the current revision —
the response carries the new `revision` value so optimistic clients can detect
out-of-band edits.

For nullable fields where omission cannot mean "clear", the contract uses an
explicit boolean sentinel that wins over the value field when set to `true`:

| Field | Clear sentinel | Effect when sentinel is `true` |
| --- | --- | --- |
| `extent` | `clearExtent` | Removes the stored bounding box. |
| `crs` | `clearCrs` | Removes the stored CRS authority token. |
| `editionGate` | `clearEditionGate` | Removes the licensing gate slug. |
| `allowedRoles` | `clearAllowedRoles` | Removes the role allow-list (the dataset becomes role-unrestricted; combine with `requiresAuth = true` to keep it protected). |

Sending the value field together with its `clear*` sentinel is allowed — the
sentinel wins. `tilesetFileName`, `description`, `name`, `assetRoot`,
`datasetType`, `cachePolicy`, `requiresAuth`, and `isPublic` have no clear
sentinel because each carries a non-nullable default; passing the field
overwrites it, omitting it keeps the previous value. Whitespace-only values
for `tilesetFileName` are treated as "no change" rather than "reset to
default" so the column never ends up empty after an update.

## Resolve output

`GET /api/v1/admin/scenes/{id}/resolve` returns the active record together with
a tileset URL composed from `Public:BaseUrl` (or the safe local origin
fallback) plus `/scenes/{id}/tileset.json`:

```json
{
  "datasetId": "5f4aa5c0-...",
  "id": "downtown",
  "name": "Downtown massing model",
  "tilesetUrl": "https://server.honua.io/scenes/downtown/tileset.json",
  "extent": { "xMin": -122.4, "yMin": 37.7, "xMax": -122.3, "yMax": 37.8 },
  "crs": "EPSG:4979",
  "cachePolicy": { "maxAgeSeconds": 3600, "noStore": false },
  "isPublic": true,
  "requiresAuth": false,
  "status": "active",
  "cesiumJsSnippet": "new Cesium.Cesium3DTileset({ url: \"https://server.honua.io/scenes/downtown/tileset.json\" })",
  "honuaSceneSnippet": "<honua-scene src=\"https://server.honua.io/scenes/downtown/tileset.json\"></honua-scene>"
}
```

Inactive or unknown ids return `404` with admin problem-details. The snippet
strings escape any user-controlled URL content for the JS-string and
HTML-attribute contexts they target.

## How #837 sees the registry

The hosted serving path uses `ISceneDatasetRegistry.FindAsync(id)`, which the
Postgres-backed implementation projects from a `SceneDatasetRecord` to the
lean `SceneDataset` it serves:

- Public records map to `SceneDataset { Metadata = null }`. Anonymous reads
  are accepted.
- Protected records (`isPublic = false`) map to `SceneDataset { Metadata.AccessPolicy = { AllowAnonymous = false, AllowedRoles = … } }`,
  matching the existing scene access-policy contract.
- The persisted `cachePolicy` is forwarded onto the serving model. The hosted
  routes use `cachePolicy.maxAgeSeconds` to pin the response `Cache-Control`
  header, and emit `Cache-Control: no-store` (plus `Vary: Authorization` on
  protected scenes) when `noStore = true`. A no-store response also disables
  server-side output-cache storage on the matching scene route so a
  previously cached body cannot outlive the dataset's no-store directive.
- `assetRoot` is canonicalized against the server content root before
  projection, so relative asset roots stored at registration survive the
  hosted asset resolver's path-containment check.

Inactive records are filtered out of `FindAsync` so deactivation immediately
hides a dataset from the public surface. Successful register / update /
deactivate calls evict the shared `scene` and per-scene `scene:{id}` output
cache tags so cached anonymous responses cannot outlive an access-flag or
deactivation change.

When the configuration-backed `Scenes:Datasets` block is also present (the
local-dev fallback path described under [Provider gating](#provider-gating)),
the composite registry checks Postgres first and only delegates to the
configuration entry when the slug is **not owned by Postgres at all** — a
slug that exists in Postgres but is inactive returns `null` from the
composite, never the configuration entry. Deactivation therefore stays
authoritative even when an operator left a same-named entry in
`Scenes:Datasets`.

## Provider gating

The Postgres-backed registry only registers when `DataSource:Provider` is
unset or set to `postgres`/`postgresql`/`postgis`. Under non-Postgres
profiles (e.g. DuckDB) the configuration-driven `Scenes:Datasets` registry
remains the active `ISceneDatasetRegistry`, and the admin endpoints
documented above are not mapped — the hosted serving path keeps working but
the admin CRUD surface is intentionally absent.

## Example: register and resolve

```bash
# Create
curl -sS -X POST https://server.honua.io/api/v1/admin/scenes \
  -H "X-API-Key: $HONUA_ADMIN" -H "Content-Type: application/json" \
  -d '{
        "id": "downtown",
        "name": "Downtown massing model",
        "description": "Photogrammetry tileset, 2026 Q1.",
        "assetRoot": "/var/lib/honua/scenes/downtown",
        "extent": { "xMin": -122.4, "yMin": 37.7, "xMax": -122.3, "yMax": 37.8 },
        "crs": "EPSG:4979",
        "cachePolicy": { "maxAgeSeconds": 3600, "noStore": false },
        "isPublic": true
      }'

# Resolve the snippet block for embedding
curl -sS -H "X-API-Key: $HONUA_ADMIN" \
  https://server.honua.io/api/v1/admin/scenes/{datasetId}/resolve
```

## Admin UI handoff

This ticket only delivers the server/API slice. Building the `honua-server-admin`
visual UI on top of this contract — list/detail/edit/delete forms, snippet
copy buttons, edition-gate badges — is tracked separately and intentionally
deferred. The shapes documented above are the durable contract the UI work
will bind to.
