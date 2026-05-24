# Studio Package Lifecycle API

Tracked by issue **#1180**. This surface gives Honua Studio one server-owned
package lifecycle for query, analysis, map, dashboard, report, form, app,
workflow, GP, and ETL artifacts. Console, MCP, QGIS, SDKs, and generated apps
should use this API instead of storing package drafts or published package JSON
in UI-local or protocol-specific shapes.

All endpoints live under `/api/v{version:apiVersion}/studio`, require admin
authorization in the MVP, and return successful payloads in `ApiResponse<T>`.
Client errors use RFC 7807 problem details with type
`https://honua.io/problems/studio`. Draft, validation, preview, publish, and
rollback operations are not response-cacheable.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/package-families` | Discover every package family, schema version, format, support level, supported operations, validation depth, limitations, and max package size. |
| `POST` | `/package-drafts` | Create a mutable package draft. |
| `GET` | `/package-drafts/{draftId}` | Retrieve a mutable draft. |
| `PUT` | `/package-drafts/{draftId}` | Replace a mutable draft with optimistic `generation` checking. |
| `DELETE` | `/package-drafts/{draftId}` | Delete a draft. |
| `POST` | `/package-drafts/{draftId}/validate` | Re-run validation and persist the validation summary on the draft. |
| `POST` | `/package-drafts/{draftId}/preview-plan` | Return a stable preview plan. GP, ETL, and workflow packages advertise job-backed previews. |
| `POST` | `/package-drafts/{draftId}/content-versions` | Save a draft as an immutable content version and move the item current pointer. |
| `GET` | `/content-items/{itemId}/versions` | List immutable versions for a Studio content item. |
| `GET` | `/content-items/{itemId}/versions/{versionId}` | Retrieve one immutable version. |
| `POST` | `/content-items/{itemId}/version-comparisons` | Compare two immutable versions by content hash, dependencies, validation, and provenance. |
| `POST` | `/content-items/{itemId}/versions/{versionId}/publish-requests` | Persist a publication request and move the published pointer when validation permits. |
| `POST` | `/content-items/{itemId}/versions/{versionId}/reopen` | Copy an immutable version into a new mutable draft with `baseVersionId`. |
| `POST` | `/content-items/{itemId}/rollback-requests` | Persist a rollback request and move the current, published, or both pointers to an earlier immutable version. |

## Package Envelope

Every family uses `StudioPackageEnvelope`:

```json
{
  "family": "query",
  "schemaVersion": "1.0",
  "format": "studio_query_package.v1",
  "bindings": [
    {
      "key": "source",
      "kind": "content",
      "ref": "content.parcels",
      "crs": "EPSG:4326",
      "srid": 4326,
      "requiredPermissions": ["metadata.read"]
    }
  ],
  "dependencies": [
    { "kind": "content-item", "ref": "content.parcels", "versionId": "v1", "required": true }
  ],
  "validation": { "status": "not-validated" },
  "publicationIntent": { "route": "/studio/parcels", "visibility": "organization" },
  "provenance": [
    { "kind": "prompt", "ref": "prompt-1", "rel": "generated-by" }
  ],
  "body": { "where": "1=1" }
}
```

`map` and `app` bodies are validated against the existing
`honua_map_package.v1` and `honua_app_package.v1` package models. Other
families currently receive envelope-level validation and advertise
`validationDepth: "envelope"` until deeper validators land.

## Persistence And Immutability

Postgres deployments use these tables:

- `studio_package_drafts` for mutable drafts and generation checks.
- `studio_content_versions` for append-only immutable package versions.
- `studio_content_version_dependencies` for lineage and invalidation lookups.
- `studio_content_items` for current and published version pointers.
- `studio_publication_requests` and `studio_rollback_requests` for durable
  lifecycle audit records.

Providers without the Postgres durable store fall back to an in-memory store for
tests/local development and advertise that limitation through
`GET /package-families`.

## SDK Projection Requirements

SDKs should project the DTOs from `Honua.Core.Features.Studio.Domain` and the
request models from `Honua.Server.Features.Studio.Models`. Clients must treat
`StudioContentVersion.envelope` as immutable once returned by the server; edits
must call `reopen`, update the new draft, and save a new content version.

Child SDK/client work should add typed clients for:

- Package family capability discovery.
- Draft create/read/update/delete and validation.
- Preview plan creation.
- Save-as-version, list/get/compare versions.
- Publish request, reopen, and rollback request operations.
