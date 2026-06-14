# Studio Package Lifecycle API

Tracked by issue **#1180**. This surface gives Honua Studio one server-owned
package lifecycle for query, analysis, map, dashboard, report, form, app,
workflow, GP, and ETL artifacts. Console, MCP, QGIS, SDKs, and generated apps
should use this API instead of storing package drafts or published package JSON
in UI-local or protocol-specific shapes.

All endpoints live under `/api/v{version:apiVersion}/studio`, require admin
authorization in the MVP, and use source-generated JSON contracts from
`StudioApiJsonContext` / `StudioJsonContext`. This is a sibling control-plane
surface to `/api/v1/admin`; it is not included in the bundled
`/api/v1/admin/openapi.json` snapshot. Until a dedicated Studio OpenAPI snapshot
is published, this document and the source-generated JSON contexts are the
contract reference. Successful responses are wrapped in the standard
`ApiResponse<T>` envelope. The Studio source-generated JSON context omits
properties whose values are `null`:

```json
{
  "success": true,
  "data": {},
  "timestamp": "2026-05-24T00:00:00Z"
}
```

Handler-generated client errors (`400`, `404`, `409`) and caught internal
failures (`500`) use RFC 7807 problem details with type
`https://honua.io/problems/studio`. Draft, validation, preview, publish, and
rollback operations are not response-cacheable. A successful draft delete returns
`ApiResponse<object>` with message `Studio package draft deleted.` and no data
payload.

## Endpoints

| Method | Route | Success | Purpose |
| --- | --- | --- | --- |
| `GET` | `/package-families` | `200 ApiResponse<StudioPackageFamilyCapabilities>` | Discover every package family, schema version, format, support level, supported operations, validation depth, limitations, and max package size. |
| `POST` | `/package-drafts` | `201 ApiResponse<StudioPackageDraft>` | Create a mutable package draft. |
| `GET` | `/package-drafts/{draftId}` | `200 ApiResponse<StudioPackageDraft>` | Retrieve a mutable draft. |
| `PUT` | `/package-drafts/{draftId}` | `200 ApiResponse<StudioPackageDraft>` | Replace a mutable draft with optional optimistic `generation` checking. |
| `DELETE` | `/package-drafts/{draftId}` | `200 ApiResponse<object>` | Delete a draft. |
| `POST` | `/package-drafts/{draftId}/validate` | `200 ApiResponse<StudioValidationSummary>` | Re-run validation and persist the validation summary on the draft. |
| `POST` | `/package-drafts/{draftId}/preview-plan` | `200 ApiResponse<StudioPreviewPlan>` | Return a stable preview plan. GP, ETL, and workflow packages advertise job-backed previews. |
| `POST` | `/package-drafts/{draftId}/content-versions` | `201 ApiResponse<StudioContentVersion>` | Save a draft as an immutable content version and move the item current pointer. |
| `GET` | `/content-items/{itemId}/versions` | `200 ApiResponse<StudioContentVersionListResponse>` | List immutable versions for a Studio content item ordered by `versionNumber`. |
| `GET` | `/content-items/{itemId}/versions/{versionId}` | `200 ApiResponse<StudioContentVersion>` | Retrieve one immutable version. |
| `POST` | `/content-items/{itemId}/version-comparisons` | `200 ApiResponse<StudioVersionComparison>` | Compare two immutable versions by content hash, dependencies, validation, and provenance. |
| `POST` | `/content-items/{itemId}/versions/{versionId}/publish-requests` | `201 ApiResponse<StudioPublicationRequest>` | Persist a publication request and move the published pointer when validation permits. |
| `POST` | `/content-items/{itemId}/versions/{versionId}/reopen` | `201 ApiResponse<StudioPackageDraft>` | Copy an immutable version into a new mutable draft with `baseVersionId`. |
| `POST` | `/content-items/{itemId}/rollback-requests` | `201 ApiResponse<StudioRollbackRequest>` | Persist a rollback request and move the current, published, or both pointers to an earlier immutable version. |

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

`schemaVersion` and `format` must match the descriptor returned by
`GET /package-families` for the selected family.

`map` and `app` bodies are validated against the existing
`honua_map_package.v1` and `honua_app_package.v1` package models. Other
families currently receive envelope-level validation and advertise
`validationDepth: "envelope"` until deeper validators land.

Supported family strings are `query`, `analysis`, `map`, `dashboard`,
`report`, `form`, `app`, `workflow`, `gp`, and `etl`. Every registered family
advertises schema version `1.0`, `maxPackageBytes: 1048576`, preview support,
publish support, and this package-family operation list in
`GET /package-families`: `draft.create`, `draft.read`, `draft.update`,
`validate`, `preview-plan`, `content-version.create`, `content-version.read`,
`content-version.compare`, `publish-request.create`, `reopen`, and `rollback`.
`DELETE /package-drafts/{draftId}` is available for cleanup, but draft delete is
not advertised as a per-family capability operation.

In Postgres-backed deployments, `durable` is `true`; `map` and `app` advertise
`supportLevel: "supported"` because family-specific validators are active, while
the other families advertise `supportLevel: "limited"` with envelope validation.
In-memory fallback deployments advertise `durable: false`,
`persistenceMode: "in-memory"`, and a limitation noting that lifecycle data is
not durable across server restarts.

## Request Semantics

Draft create and update requests carry `packageKey`, optional `workspaceId`,
optional `ownerId`, and the `envelope`. `packageKey` is trimmed, limited to 200
characters, and may contain only letters, numbers, dash, underscore, or dot.
`workspaceId` and `ownerId` are also trimmed and empty strings are stored as
`null`. Omit `ownerId` on draft create to use the authenticated actor id; omit
it on draft update to preserve the existing owner. `workspaceId` uses replace
semantics on update: omit it or send an empty string to clear the stored
workspace. Omit `itemId` on draft create to let the server allocate a new
Studio content item. Both durable and in-memory stores enforce `(workspaceId,
family, packageKey)` uniqueness across content items.

Drafts are mutable and carry a server-managed `generation`. `PUT
/package-drafts/{draftId}` increments `generation` on success. Clients that
need optimistic concurrency should send the last-seen `generation`; a stale
generation returns `409 Conflict`. Omitting `generation` updates from the
current draft generation loaded by the server, so clients that need strict
lost-update protection should include the last-seen value. `validate`,
`preview-plan`, and save-as-version calls persist the latest validation summary
back onto the draft and therefore also advance the draft generation. A client
that calls one of those operations and then performs a strict `PUT` should
reload the draft first.

Saving a draft as a content version revalidates the draft, captures the
validated envelope, stamps a monotonic `versionNumber`, computes a SHA-256
`contentHash` that excludes volatile validation timestamps, copies dependencies
and provenance into sidecars, and advances the content item's current pointer.
The immutable version can be read, compared or reopened, but is never edited in
place. Listing versions returns `200` with the requested `itemId` and an empty
`versions` array when no versions exist for that item; retrieving an individual
missing or cross-item version returns `404`.

Routes that include both `{itemId}` and `{versionId}` treat the pair as an
ownership boundary. A version that exists under a different content item is
handled as not found, and publish, reopen, compare, and rollback operations do
not persist side effects for cross-item version ids.

`POST /content-items/{itemId}/versions/{versionId}/publish-requests` uses the
request `intent` when supplied and otherwise falls back to the version
envelope's `publicationIntent`. Invalid intent overrides fail with `400` before
a publication request is persisted. Versions whose captured validation status
is `invalid` still produce a durable publication request, but the request
status is `rejected` and the published pointer is not moved. Valid and warning
versions are accepted and move the Studio content item's published pointer;
warning acknowledgement is optional audit text in the MVP. The `pending`
publication status is reserved for later asynchronous publication execution and
is not emitted by the API service today.

Route-resolvable publication for Studio-generated `map`, `dashboard`, `report`,
and `app` artifacts is handled by the sibling
[Content Publication Registry API](content-publication-registry.md). That API
owns the public route slug, active route pointer, share/embed/public-link policy,
generated-app reopen-by-revision reads, and rollback pointer. Studio clients
should use the package lifecycle API for draft/version governance, then use the
publication registry when a version must become a runtime route.

`POST /content-items/{itemId}/rollback-requests` accepts `pointer` values
`current`, `published`, and `both` and returns the resulting
`StudioContentItemPointers` in the rollback response. Undefined numeric enum
values are rejected before the store is called.

## Validation And Preview

Create, update, validate, preview, and save-as-version paths all run the shared
`IStudioPackageValidator`. Validation produces:

- `status`: `not-validated`, `valid`, `warning`, or `invalid`.
- `diagnostics`: machine-readable `code`, `severity`, `path`, and `message`.
- `unsupportedCapabilities`: deployment-limited capabilities clients should
  render as disabled states.
- `generatedAt`: the timestamp for the validation pass.

Baseline validation checks the registered family, schema version, package
format, serialized package size, object-shaped `body`, unique binding keys,
positive SRIDs, supported CRS identifiers (`EPSG:*`, OGC CRS URIs, or OGC CRS
URNs), dependency identity uniqueness, required provenance fields, and
publication visibility. Map packages also validate `honua_map_package.v1` body
format and initial-view bbox/CRS; app packages validate
`honua_app_package.v1`.

Preview plans are planning-only responses for preview execution, but they still
persist the validation summary on the draft. `gp`, `etl`, and `workflow` drafts
return `requiresJob: true`, `synchronous: false`, and steps
`["validate-envelope", "plan-background-preview-job"]`; all other families
return `requiresJob: false`, `synchronous: true`, and steps
`["validate-envelope", "prepare-inline-preview"]`.

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

The package lifecycle service emits OpenTelemetry activities under
`Honua.Studio.PackageLifecycle` with stable tags such as `studio.item.id`,
`studio.draft.id`, `studio.version.id`, `studio.family`,
`studio.validation.status`, and `studio.publish.status`. Endpoint logging uses
source-generated `StudioEndpointsLog` messages for draft, version,
publication, rollback, and capability operations.

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
