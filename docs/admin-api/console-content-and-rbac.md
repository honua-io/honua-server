# Console Content And RBAC API (Baseline)

Honua Console consolidates Portal, Admin, and Studio into a single deployment
surface. The endpoints in this document are the shared server-side baseline
that Console (and any other client — `honua-sdk-js`, MCP, QGIS plugin) consume
to list, authorize, publish, share, and trace content items without
reintroducing separate Portal/Admin metadata models. The shapes are owned by
the `Honua.Server.Features.Console` vertical slice and source-generated through
`ConsoleJsonContext` so AOT publish stays clean.

Tracked by issue **#1162**. Subsequent tickets (#1163 — persistent store,
#1164/#1165 — release lifecycle) build on this surface.

## Surface

All endpoints live under `/api/v{version:apiVersion}/console` and require admin
authorization. Successful responses are wrapped in the standard
`ApiResponse<T>` envelope (`success`, `data`, `message`, `timestamp`).

Error contract (matching the established admin endpoint pattern):

- **Expected client errors (400, 404, 409).** Returned as
  `ApiResponse<object>` with `success: false` and a human-readable
  `message`. Examples: invalid `itemType` filter, unknown content id on
  detail/update/delete, stale `generation` on PUT.
- **Internal failures (5xx).** Returned as RFC 7807 `ProblemDetails` via
  `TypedResults.Problem(...)` with a generic title/detail so internal
  diagnostics never leak across the boundary.
- **Client cancellation.** Endpoints rethrow `OperationCanceledException`
  rather than mask it as a 500, so a client disconnect or request timeout
  surfaces as the host's standard cancelled response (no `ApiResponse`
  envelope, no `ProblemDetails`) instead of being recorded as a server
  failure. The `/session` endpoint applies the same rule on its inner
  content-page fetch — a cancellation surfaces, while a non-cancellation
  failure degrades to an empty content page (`degraded: true`).

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/session` | Bootstrap: user profile, capabilities, route entitlements, first content page. |
| `GET` | `/content` | Paginated content listing (filter by `itemType`, `visibility`, `owner`, `namespace`; `q` substring search). |
| `POST` | `/content` | Create a content item. Server stamps `id`, `generation=1`, audit timestamps. |
| `GET` | `/content/search?q=…` | Search shortcut over the same listing pipeline. |
| `GET` | `/content/{id}` | Detail. Returns the item with computed `actions` for the requesting principal. |
| `PUT` | `/content/{id}` | Full replacement update. Honours optimistic concurrency via `generation`. |
| `PATCH` | `/content/{id}` | Partial update for displayable fields (title, description, tags, labels, visibility). Does not touch `generation`. |
| `DELETE` | `/content/{id}` | Delete the item. |
| `GET` | `/content/{id}/provenance?depth=` | Resolve the transitive provenance chain anchored on the item (default depth 5). |
| `POST` | `/actions/check` | Bulk evaluate Console verbs over a set of item ids and/or route keys. |

## Content item shape

`ConsoleContentItem` (see `src/Honua.Core/Features/Console/Domain/ConsoleContentItem.cs`):

```json
{
  "id": "5966...",
  "name": "parcels",
  "namespace": "default",
  "title": "Parcels",
  "description": "Cadastral parcels for Region A",
  "itemType": "layer",
  "tags": ["parcels", "cadastre"],
  "labels": { "env": "prod" },
  "lifecycle": "active",
  "operationalState": "ready",
  "visibility": "organization",
  "ownerId": "u-1",
  "teamScopeId": null,
  "createdById": "u-1",
  "updatedById": "u-2",
  "createdAt": "2026-05-24T06:51:00Z",
  "updatedAt": "2026-05-24T06:51:00Z",
  "generation": 7,
  "actions": ["view", "edit", "operate"],
  "provenance": [
    { "kind": "catalog-resource", "itemId": "res-1", "rel": "derived-from" }
  ],
  "typeMetadata": { "resourceId": "res-1", "fieldCount": 14 }
}
```

`itemType` is one of: `service`, `layer`, `saved-map`, `dashboard`, `report`,
`generated-app`, `open-data`. The `typeMetadata` sidecar is an opaque JSON
element whose shape depends on `itemType`. Server writes use source-generated
serializers — clients decode with typed helpers per `itemType` rather than
forking the outer DTO. The sidecar shapes are tracked under
`Honua.Server.Features.Console.TypeSidecars` and documented per type in the
table below.

| `itemType` | Sidecar fields |
| --- | --- |
| `service` | `serviceType`, `routeBase?`, `publications[]` |
| `layer` | `resourceId`, `geometryType?`, `fieldCount?`, `extent?` (EPSG:4326 lon/lat bbox) |
| `saved-map` | `layerCount`, `extent?`, `projection?` |
| `dashboard` | `widgetCount`, `dataSourceIds[]` |
| `report` | `templateId?`, `lastRunAt?` |
| `generated-app` | `appType`, `sourceArtifactId?`, `entryUrl?` |
| `open-data` | `license?`, `formats[]`, `distributionUrls[]` |

`actions` is always **server-computed** for the requesting principal and never
stored. Clients should not send it on create/update; the server overwrites it.

## Update semantics

`PUT /content/{id}` and `PATCH /content/{id}` are intentionally split rather
than overloaded on `PUT`:

- **`PUT` is a full replacement.** Any nullable field omitted from the request
  body is cleared on the stored item; value-type fields fall back to the same
  defaults the create path applies (`lifecycle = draft`,
  `operationalState = unknown`, `visibility = personal`); collection fields
  (`tags`, `labels`, `provenance`) default to empty when omitted. Clients that
  only want to change a subset of fields must use `PATCH`.
- **`PATCH` merges.** Only `title`, `description`, `tags`, `labels`, and
  `visibility` are patchable. Other fields require `PUT`. `PATCH` does not
  increment `generation`. The in-memory baseline store applies the patch
  through a `TryUpdate` compare-and-swap loop, so a `PATCH` racing a
  concurrent `PUT` cannot silently revert the `PUT`'s generation or
  `PUT`-only fields — the patch re-reads the latest snapshot before
  committing.
- **Optimistic concurrency on `PUT`** compares the supplied `generation`
  against the stored value with **exact-match** semantics. A request whose
  `generation` is older *or* newer than the stored value is rejected with
  `409 Conflict` (`ApiResponse<object>`, `success: false`, message
  `"Stale generation; refresh and retry."`). The in-memory baseline store
  swaps via `ConcurrentDictionary.TryUpdate`, so two concurrent writers
  reading the same generation cannot both succeed — the loser must refresh
  and retry. Omit `generation` to bypass the check (last-write-wins).
- **Identity and audit fields** (`id`, `createdAt`, `createdById`) are stamped
  by the server and ignored if sent on update; `updatedAt` and `updatedById`
  are stamped from the request principal. On create, `ownerId` is honoured
  when supplied but does **not** influence audit stamping — a privileged
  caller creating a content item on behalf of another user is still
  recorded as the acting principal in `createdById`/`updatedById`.
- **Principal identifier resolution.** The acting principal id used for
  `createdById`/`updatedById` and for `session.user.id` is resolved via
  `ConsolePrincipal.ResolveActorId` with a fixed fallback chain:
  `ClaimTypes.NameIdentifier` → `sub` → `api_key_id` → `api_key_name` →
  `ClaimsIdentity.Name` → `ClaimTypes.Name`. OIDC/JWT principals normally
  resolve at the first hop; admin API-key principals (which carry only the
  API-key claims documented on `ApiKeyAuthenticationHandler`) resolve via
  the `api_key_*` hops, so audit fields and the session user id stay
  non-empty regardless of which scheme authenticated the request.
- **Team-scope precondition.** `visibility: "team"` is only valid when a
  non-empty `teamScopeId` is paired with it. `POST` and `PUT` requests that
  supply `visibility: "team"` without a `teamScopeId` are rejected with
  `400 Bad Request`. Because `PATCH` cannot change `teamScopeId` (the patch
  contract is restricted to displayable fields), `PATCH` requests that set
  `visibility: "team"` are rejected unless the stored item already carries a
  `teamScopeId` — clients must use `PUT` to establish the team scope before
  the visibility transition. The invariant is enforced atomically inside
  the store's `PATCH` compare-and-swap loop in addition to the endpoint's
  pre-flight check, so a concurrent `PUT` that clears `teamScopeId` between
  the pre-check and the swap still yields `400 Bad Request` rather than a
  stored item with `visibility: "team"` and no scope.
- **Closed enum sets.** Body enums (`itemType`, `visibility`, `lifecycle`,
  `operationalState`, and each entry of the action-check `actions` array)
  are validated against the documented string set. Requests carrying
  undefined numeric values such as `"itemType": 999` or `"actions": [999]`
  are rejected with `400 Bad Request` even though the underlying
  `JsonStringEnumConverter` would otherwise admit them; the validator uses
  AOT-safe switch-based whitelists to stay reflection-free.
- **Provenance edge validation.** `POST`/`PUT` requests whose
  `provenance` array contains a null entry, or an entry whose
  `itemId`, `kind`, or `rel` is null or whitespace, are rejected with
  `400 Bad Request`. The error message names the offending index
  (e.g. `provenance[1].itemId must be a non-empty string.`). Without
  this guard a malformed edge would persist and NRE during transitive
  resolution in `GET /content/{id}/provenance`. The provenance traversal
  also defensively skips null or empty-id edges in the store, so legacy
  data (or a future persistent backend that surfaces an invalid edge)
  cannot break later reads.

## RBAC verbs

Seven Console verbs map onto the existing `IRoleStore` permission-grant set
through `IConsoleActionEvaluator`. Mapping is documented inline on the
evaluator and exercised by `ConsoleActionEvaluatorTests`:

| Console verb | Visibility precondition | Policy action(s) used |
| --- | --- | --- |
| `view` | terminal | `personal` = owner only; `team` = team-scope members only; `organization` = any authenticated principal; `public` = anyone |
| `edit` | yes | `metadata.write` (and `features.edit` where applicable); owner always passes |
| `publish` | n/a (catalog-wide) | `catalog.publish` |
| `share` | yes | `metadata.write` against sharing metadata; owner always passes |
| `embed` | yes | inherits `view` (anonymous principals are limited to `public` items) |
| `operate` | yes | service-specific (`features.query`, `raster.render`, `reports.run`); anonymous principals may operate `public` items with `features.query` |
| `administer` | n/a (catalog-wide) | `admin.rbac.write` |

**Visibility is a terminal gate for non-admins.** A principal that cannot
view an item per the visibility rule above cannot `edit`, `share`, `embed`,
or `operate` on it either — capabilities like `metadata.write` or
`features.query` do not override the visibility check. Owners always pass
the visibility gate for items they own. Admin principals bypass the gate
entirely (they receive the full capability set).

Capabilities returned through `/session.capabilities` (e.g. `metadata.read`,
`catalog.publish`, `admin.rbac.write`) are the wire identifiers Console
surfaces use to gate UI affordances.

## Action-check response shape

`POST /api/v1/console/actions/check` evaluates the supplied verbs against a
batch of `itemId` and/or `routeKey` targets. Each result mirrors the target
identifier and partitions the requested verb set into `allowed`/`denied`:

- **Item targets** — `allowed` contains the subset of the requested verbs the
  caller may perform on the item (computed by `IConsoleActionEvaluator` using
  the same rules as the `actions` field on content responses). `denied`
  contains the remaining requested verbs. Unknown ids return
  `notFound: true` with empty `allowed`/`denied`.
- **Route targets** — route entitlements model navigation access, not item
  verbs. When the route is allowed for the caller **and** the request asked
  for `view`, the response surfaces `allowed: ["view"]`; otherwise `allowed`
  is empty. All other requested verbs land in `denied` because routes do not
  carry the non-`view` item verbs.

When the request omits an `actions` list the server evaluates the full
seven-verb set; clients should pass an explicit `actions` filter when only a
subset is interesting to keep the response compact.

`targets` is required and must contain at least one entry. Individual
`null` entries inside the array (e.g. `{"targets":[null]}`) and entries
that supply neither `itemId` nor `routeKey` are rejected with
`400 Bad Request` rather than masked as a 500.

## Provenance

`ConsoleProvenanceRef` records describe directed lineage edges. Well-known
`kind` values: `catalog-resource`, `published-service`, `studio-artifact`,
`generated-app`. Well-known `rel` values: `derived-from`, `publishes`,
`generated-by`, `input-of`. Free-form values are permitted for adapter-specific
lineage. The `GET …/provenance` endpoint walks references transitively, capped
at `depth=5` by default, and breaks cycles by tracking visited ids.

## Bootstrap response

`GET /api/v1/console/session` returns user profile, capability strings, route
entitlements for `catalog`, `studio`, `operate`, `share`, `admin`, and the
first page of content items. If the underlying content store fails the
response degrades — `content.items` is empty, `content.total = 0`,
`degraded = true` — so the Console shell can still initialize.

## SDK / OpenAPI

Endpoints carry `WithTags("Console")`, summaries, and source-generated DTOs.
`honua-sdk-js` generation should consume `Console` as a new tag group and
produce typed helpers per `itemType` for the `typeMetadata` sidecar. No
frontend-specific DTO forks.

## Known follow-ons

- **Persistent store (#1163)** — `IConsoleContentStore` is currently backed by
  the baseline in-memory store registered in `Program.cs`. A Postgres-backed
  implementation, schema migration, and snapshot test live in the follow-on.
- **Release lifecycle (#1164, #1165)** — operation lifecycle, compatibility
  prevalidation, rollback workflow APIs.
- **OGC alignment** — sidecar shapes for `service` map onto Metadata v2
  publication types; future iterations should track Metadata v2 enum changes
  through shared reuse rather than duplicating shapes.
