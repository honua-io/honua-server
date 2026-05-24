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

## RBAC verbs

Seven Console verbs map onto the existing `IRoleStore` permission-grant set
through `IConsoleActionEvaluator`. Mapping is documented inline on the
evaluator and exercised by `ConsoleActionEvaluatorTests`:

| Console verb | Policy action(s) used |
| --- | --- |
| `view` | `metadata.read` + item visibility check |
| `edit` | `metadata.write` (and `features.edit` where applicable) |
| `publish` | `catalog.publish` |
| `share` | `metadata.write` against sharing metadata |
| `embed` | `metadata.read` + visibility check (item must be `public` or `organization` for anonymous principals) |
| `operate` | service-specific (`features.query`, `raster.render`, `reports.run`) |
| `administer` | `admin.rbac.write` |

Capabilities returned through `/session.capabilities` (e.g. `metadata.read`,
`catalog.publish`, `admin.rbac.write`) are the wire identifiers Console
surfaces use to gate UI affordances. Admin principals receive the full
capability set.

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
