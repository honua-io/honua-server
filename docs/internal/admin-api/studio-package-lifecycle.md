# Studio Package Lifecycle API

Tracked by issue **#1180**. This surface gives Honua Studio one server-owned
package lifecycle for query, analysis, map, dashboard, report, form, app,
workflow, GP, and ETL artifacts. Console, MCP, QGIS, SDKs, and generated apps
should use this API instead of storing package drafts or published package JSON
in UI-local or protocol-specific shapes.

All endpoints live under `/api/v{version:apiVersion}/studio` and use
source-generated JSON contracts from `StudioApiJsonContext` / `StudioJsonContext`.
Authorization defaults to admin-only and widens to ownership-scoped end users
behind a feature flag — see [Authorization](#authorization). This is a sibling
control-plane surface to `/api/v1/admin`; it is not included in the bundled
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
| `GET` | `/content-items` | `200 ApiResponse<StudioContentItemListResponse>` | List/search Studio content items with filters (`family`, `workspaceId`, `owner`, `state`, `q`), cursor pagination, and a joined publication-registry lifecycle badge per row. |
| `POST` | `/package-drafts` | `201 ApiResponse<StudioPackageDraft>` | Create a mutable package draft. |
| `GET` | `/package-drafts` | `200 ApiResponse<StudioPackageDraftListResponse>` | List/search mutable Studio package drafts with filters (`family`, `workspaceId`, `owner`, `q`) and cursor pagination. |
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

## Authorization

Every endpoint requires the admin role by default. Non-admin, ownership-scoped
access (honua-server#3001) is available behind the
`Studio:EndUserAuthorization:Enabled` feature flag (default `false`); with the
flag off, behavior is byte-for-byte unchanged from the MVP admin-only posture
(NFR-001).

With the flag on:

- **Baseline tier** — draft create/read/update/delete/validate/preview,
  save-as-version, reopen, and reads of an item's versions: an authenticated
  non-admin caller may act on resources they own. Ownership is recorded once at
  create time from the caller's resolved identity (`ownerId` on
  `StudioPackageDraft` and `studio_content_items.owner_id`) and is immutable
  thereafter — a later update cannot transfer ownership, and a non-admin caller's
  own `ownerId` field on create/update is ignored rather than trusted (it always
  resolves to the caller). Reading a *published* version is additionally allowed
  regardless of ownership. Cross-user access to a non-owned, non-published
  resource is denied by default. No operator grant is required for the baseline
  tier — the flag itself is the widening switch (REQ-002).
- **Elevated tier** — publish-request and rollback: always policy-gated, even for
  the resource's own owner (REQ-003). The caller must additionally hold a
  matching `StudioDraft` operator grant (`Publish` or `Rollback` operation)
  through the platform's existing role/grant model
  (`IOperatorAuthorizationEvaluator`). An operator provisions self-service
  publish/rollback rights with a role grant
  `{ "service": "StudioDraft", "layer": "own", "operation": "Publish" }` — the
  `own` sentinel layer authorizes every resource the principal owns; a grant
  scoped to a concrete draft/item id instead authorizes an operator-provisioned
  delegate on that one resource, independent of ownership. See
  [Connect AI agents to Honua over MCP](../../guides/connect/ai-agents-mcp.md#studio-package-lifecycle-grants-honua-server3001)
  for the equivalent `/mcp` grant story.
- **Enumeration** (`GET /content-items`, `GET /package-drafts`) — with the flag
  on, a non-admin caller's effective `owner` filter is always forced server-side
  to their own resolved id, regardless of any `owner` query value the client
  supplies: the list is scoped to "my content," never trusting a client-supplied
  owner parameter. Admins (and every caller with the flag off) keep today's
  unscoped-by-default behavior.

Admin principals retain full, unscoped access in both flag states; nothing above
changes existing admin client behavior.

Authorization denials return the shared `https://honua.io/problems/studio` RFC
7807 problem with `status: 403` and a machine-readable `code` extension member
(REQ-004) the SDK client can branch on:

| `code` | Meaning |
| --- | --- |
| `studio_authorization/end_user_mode_disabled` | The flag is off and the caller is not admin. |
| `studio_authorization/authentication_required` | No authenticated principal. |
| `studio_authorization/cross_user_denied` | The caller does not own the resource (and, for reads, it is not publicly readable; for the elevated tier, the caller also holds no delegate grant for it). |
| `studio_authorization/elevated_grant_required` | Publish-request or rollback on the caller's own resource without a matching `StudioDraft` operator grant. |

Every denial is recorded to the audit log (`AuditEventType.Authorization`,
`AuditOutcome.Denied`); every *allowed* elevated-tier decision is also recorded
(`AuditOutcome.Success`), so publish/rollback policy decisions are independently
auditable per REQ-003.

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

## Content Item And Draft Enumeration

Tracked by issue **#3003**. `GET /content-items` and `GET /package-drafts` give
clients a "my content" / content-browser view instead of requiring callers to
already know an `itemId` or `draftId`.

Both endpoints accept:

- `family`: comma-separated family filter, e.g. `family=map,dashboard`. Accepts the
  same short strings as the envelope's `family` field (`query`, `analysis`, `map`,
  `dashboard`, `report`, `form`, `app`, `workflow`, `gp`, `etl`). An unknown value
  returns `400`.
- `workspaceId`: exact workspace match.
- `owner`: exact match against the recorded owner (see below).
- `q`: case-insensitive substring match against `packageKey`. There is no full-text
  index in this slice (REQ-002); this is enough for a content-browser search box.
- `cursor` / `limit` (default `25`, max `1000`): opaque keyset cursor pagination.
  Pass the previous response's `nextCursor` back as `cursor` to fetch the next page.
  `nextCursor` is `null` on the last page.

`GET /content-items` additionally accepts `state`, a comma-separated filter over the
derived lifecycle state: `draft` (no immutable version saved yet — `currentVersionId`
is null), `current` (a saved version exists but is not published), or `published`
(`publishedVersionId` is set). This is distinct from a joined publication's route
`lifecycle` (`active`/`suspended`/`archived`) described below.

Both endpoints order results by `updatedAt` descending with the row id (`itemId` or
`draftId`) descending as a stable tiebreak (REQ-001), so pages stay stable even as
other rows are concurrently created or updated.

### Ownership Filter (honua-server#3001)

`studio_content_items.owner_id` (migration `090_AddStudioContentItemOwner.sql`)
carries the item's real owner, populated once at create time from the owning
draft's `ownerId` (itself defaulted to the authenticated creator when not
explicitly assigned — see [Authorization](#authorization)) and immutable
thereafter. `GET /content-items`'s `owner` parameter filters this column
directly; it no longer stands in for `createdBy`. `GET /package-drafts`'s
`owner` parameter filters `studio_package_drafts.owner_id`, unchanged.

With `Studio:EndUserAuthorization:Enabled` on, both endpoints additionally
force-scope the effective owner filter to the requesting principal for
non-admin callers — see [Authorization](#authorization). The `owner` query
parameter is honored as supplied only for admins, or while the flag is off.

### Publication-Registry Lifecycle Badge (REQ-004)

Each row in `GET /content-items` carries an optional `publication` badge sourced
from the [Content Publication Registry API](content-publication-registry.md), so
clients can render lifecycle state without a second call per item. The registry
does not have a foreign key back to Studio; the join uses the convention that a
publication's `sourceContentId` equals the Studio item id
(`itemId.ToString("D")`). Publishers that route a Studio content item through the
registry should set `sourceContentId` accordingly. The badge reflects the *route's*
current state (potentially newer than the version that is current/published in
Studio); `publication` is omitted when no publication references the item. The
lookup batches every row in a page into one query
(`IContentPublicationStore.GetLatestRouteStatesBySourceContentIdsAsync`) rather than
querying per row.

### Example: `GET /content-items?family=map&state=published&limit=2`

```json
{
  "success": true,
  "data": {
    "items": [
      {
        "itemId": "b2a6e6a0-9b8e-4b2f-9a3a-6a6d9c8e6f31",
        "packageKey": "parcels-overview",
        "workspaceId": "studio",
        "family": "map",
        "state": "published",
        "currentVersionId": "6f5c8945-a329-4680-b489-bb3a7ddc87b8",
        "publishedVersionId": "6f5c8945-a329-4680-b489-bb3a7ddc87b8",
        "createdBy": "alice",
        "updatedBy": "alice",
        "createdAt": "2026-05-24T00:00:00Z",
        "updatedAt": "2026-06-01T00:00:00Z",
        "publication": {
          "publicationId": "d8b3d8a0-2f52-4a24-8a59-5bd596a3b7f4",
          "routeSlug": "field/maps/parcels-overview",
          "routePath": "/api/v1/published/field/maps/parcels-overview",
          "lifecycle": "active",
          "activeRevision": 2,
          "updatedAt": "2026-06-01T00:10:00Z"
        }
      }
    ],
    "total": 14,
    "nextCursor": "MTc3NzYwMDYwMDAwMDAwMDA6YjJhNmU2YTAtOWI4ZS00YjJmLTlhM2EtNmE2ZDljOGU2ZjMx"
  },
  "timestamp": "2026-06-01T00:15:00Z"
}
```

`GET /package-drafts` returns the same envelope shape but with `items` as full
`StudioPackageDraft` objects (matching `GET /package-drafts/{draftId}`) and no
`publication` badge — drafts are mutable and pre-publication by definition.

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

Migration `089_AddStudioContentEnumerationIndexes.sql` adds the keyset-pagination
indexes `GET /content-items` and `GET /package-drafts` rely on (`updated_at DESC,
<id> DESC`, plus per-filter composites on `family`, `created_by`/`owner_id`, and
`workspace_id`) and a `content_publication_versions (source_content_id, created_at
DESC, revision DESC)` index supporting the publication-badge join. Migration
`090_AddStudioContentItemOwner.sql` adds `studio_content_items.owner_id`
(backfilled from `created_by` for pre-existing rows) and its keyset-pagination
owner index (honua-server#3001).

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
- Content item and draft enumeration (`GET /content-items`, `GET /package-drafts`)
  with filter/cursor pagination and the joined publication badge (sdk-js#780).
- Preview plan creation.
- Save-as-version, list/get/compare versions.
- Publish request, reopen, and rollback request operations.
