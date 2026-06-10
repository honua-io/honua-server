# Content Publication Registry API

Tracked by issue **#1183**. This surface owns durable publication records for
Studio-generated maps, dashboards, reports, and generated apps. It is the
server-side source of truth for published routes, immutable versions, active
route pointers, rollback targets, visibility, share policy, embed policy, public
links, dependency references, provenance, and publish/audit correlation.

This registry is intentionally separate from the Studio package lifecycle API.
`/api/v1/studio/**` creates drafts, immutable package versions, and lifecycle
requests for Studio content items. `/api/v1/console/publications/**` turns a
map/dashboard/report/generated-app artifact into a route-resolvable publication,
and `/api/v1/published/**` resolves that route for runtime clients.

The implementation lives in:

- `Honua.Core.Features.Publishing.Content` for validation, route normalization,
  hashing, immutable-version projection, and the in-memory fallback store.
- `Honua.Postgres.Features.Publishing` for the durable Postgres store.
- `Honua.Server.Features.Console.Publications` for thin HTTP adapters.

JSON contracts are source-generated through `ContentPublicationJsonContext`.
Successful publication responses are returned as the DTO itself, not wrapped in
`ApiResponse<T>`. Management endpoint errors use RFC 7807 problem details with
type `https://honua.io/problems/admin`; public-route errors use the shared
standard error formatter. This route family is not part of the bundled
`/api/v1/admin/openapi.json` snapshot; this document and the source-generated
JSON context are the contract reference until a dedicated Console/Studio OpenAPI
bundle is published.

## Endpoints

Management endpoints live under `/api/v{version:apiVersion}/console/publications`
and require admin authorization.

| Method | Route | Success | Purpose |
| --- | --- | --- | --- |
| `POST` | `/` | `201 ContentPublicationDetail` | Publish a new immutable artifact version and claim a server-owned route slug. |
| `GET` | `/{publicationId}` | `200 ContentPublicationDetail` | Return the route state and newest immutable versions for a publication. |
| `GET` | `/{publicationId}/versions/{versionSelector}` | `200 ContentPublicationVersion` | Return one immutable version by revision number, `v{n}`, or version id. |
| `POST` | `/{publicationId}/republish` | `200 ContentPublicationDetail` | Create a new immutable version and move the active route pointer to it. |
| `POST` | `/{publicationId}/rollback` | `200 ContentPublicationDetail` | Move the active route pointer to an earlier immutable version without creating a new version. |
| `PATCH` | `/{publicationId}/policy` | `200 ContentPublicationPolicyUpdateResponse` | Update server-owned visibility/share/embed/service/public-link policy. |

The public route endpoint lives under `/api/v{version:apiVersion}/published`.

| Method | Route | Success | Purpose |
| --- | --- | --- | --- |
| `GET` | `/{*routeSlug}` | `200 PublishedArtifactView` | Resolve the active version, or a specific immutable version via `?version=`, while enforcing visibility/access, public-link, and embed policy. |

## Artifact Kinds

`kind` is a closed string enum:

- `map`
- `dashboard`
- `report`
- `generated-app`

Dependency references are stored by id only. `dependency.kind` is a closed
enum; an undefined kind is rejected with `400`. The defined kinds are
`service`, `resource`, `publication`, `published-service`, `deployment`,
`map-package`, `app-package`, `result-package`, `report`, and
`provenance-item`. Metadata v2 `service` / `resource` / `publication`
references are validated when the graph provider is registered. A
`published-service` dependency requires the published-service store and returns
`503` if that store is unavailable. Package, deployment, result, report, and
provenance references are accepted opaquely in this slice because their
canonical stores are not wired here yet.

## Publish Request

`POST /api/v1/console/publications` accepts:

```json
{
  "kind": "generated-app",
  "routeSlug": "field/apps/parcel-review",
  "title": "Parcel Review",
  "sourceContentId": "content.parcel-review",
  "sourcePackageId": "pkg.parcel-review",
  "contentPayload": "{\"schema\":\"honua_app_package.v1\"}",
  "contentVersionId": "studio-version-1",
  "sourceMetadataRevision": 42,
  "sourceMetadataEtag": "\"metadata-etag\"",
  "appManifestId": "manifest-v1",
  "appBundleArtifactId": "bundle-v1",
  "defaultViewBbox": {
    "crs": "EPSG:4326",
    "minX": -158.2,
    "minY": 21.2,
    "maxX": -157.6,
    "maxY": 21.8
  },
  "policy": {
    "visibility": "private",
    "share": { "allowSharing": false, "allowAnonymous": false },
    "embed": { "allowEmbedding": false },
    "service": { "requireAuthenticatedServices": false },
    "publicLink": { "enabled": false, "links": [] }
  },
  "dependencies": [
    { "kind": "service", "refId": "parcels", "revision": 42, "etag": "\"metadata-etag\"" }
  ],
  "provenance": [
    { "kind": "job", "refId": "job-123", "rel": "generated-by" }
  ],
  "jobId": "job-123",
  "operationId": "publish-app"
}
```

The server allocates `publicationId`, `versionId`, `revision`, `routePath`,
`generation`, and `etag`. If `contentPayload` is supplied, the server stores only
its SHA-256 hex hash. If `contentPayload` is omitted, a supplied `contentHash`
is recorded as-is.

Route slugs are normalized server-side. Canonical slugs are lowercase,
hierarchical paths with `/` separators; each segment contains only `a-z`,
`0-9`, and `-`, and path traversal sequences are rejected. If `routeSlug` is not
usable, the title is normalized; if neither value produces a slug, the server
allocates `pub-{id}`. A claimed slug cannot be reused and returns `409 Conflict`.

`defaultViewBbox` is optional. When a box is supplied, `crs` must be non-empty;
if the JSON omits it, the contract default is `EPSG:4326` lon/lat. Web Mercator
is never assumed. For WGS 84/CRS84 boxes, longitude must stay in `[-180, 180]`
and latitude in `[-90, 90]`.

## Response Model

`ContentPublicationJsonContext` omits `null` fields. `ContentPublicationDetail`
contains the mutable route pointer plus immutable versions. Abbreviated example:

```json
{
  "route": {
    "publicationId": "d8b3d8a0-2f52-4a24-8a59-5bd596a3b7f4",
    "routeSlug": "field/apps/parcel-review",
    "routePath": "/api/v1/published/field/apps/parcel-review",
    "kind": "generated-app",
    "activeVersionId": "b798c94d-0100-464d-8ee9-9b0874c0a556",
    "activeRevision": 1,
    "lifecycle": "active",
    "policy": { "visibility": "private" },
    "generation": 1,
    "etag": "\"a2f1d6c0e7b94c3ab1d3371f0e0df125\"",
    "updatedBy": "admin",
    "updatedAt": "2026-05-24T00:00:00Z",
    "createdAt": "2026-05-24T00:00:00Z"
  },
  "versions": [
    {
      "publicationId": "d8b3d8a0-2f52-4a24-8a59-5bd596a3b7f4",
      "versionId": "b798c94d-0100-464d-8ee9-9b0874c0a556",
      "revision": 1,
      "kind": "generated-app",
      "routeSlug": "field/apps/parcel-review",
      "routePath": "/api/v1/published/field/apps/parcel-review",
      "title": "Parcel Review",
      "appManifestId": "manifest-v1",
      "appBundleArtifactId": "bundle-v1",
      "contentHash": "9b6f...",
      "createdBy": "admin",
      "createdAt": "2026-05-24T00:00:00Z"
    }
  ]
}
```

Version rows are append-only. Republish creates a new version, keeps the
publication `kind` and route slug, snapshots the current route policy onto the
new version, and moves the route pointer. Rollback moves the route pointer to an
existing version and sets `rollbackTargetVersionId`; it does not mutate or
delete the version history.

`republish`, `rollback`, and `policy` update requests accept `expectedEtag`.
When supplied, the value must exactly match the current route `etag` or the
request returns `409 Conflict`.

## Policy And Public Links

The route `policy` is the current source of truth for read access. Each
immutable version also stores a policy snapshot from publish time for history.

Policy fields:

- `visibility`: `private`, `organization`, `team`, or `public`. This is a
  closed enum; publish and policy-patch requests that supply an undefined
  visibility are rejected with `400`.
- `access`: optional shared `AccessPolicy`; used by runtime reads when the route
  is not public and anonymous sharing is not enabled.
- `share.allowSharing`: whether sharing is allowed.
- `share.allowAnonymous`: whether anonymous route reads are allowed without
  making the route globally `public`.
- `share.allowedScopes`: optional org/team scopes the artifact may be shared
  with. Stored sharing metadata; it is not evaluated on the public read path in
  this slice.
- `embed.allowEmbedding`: whether `?embed=true` route reads are allowed.
- `embed.allowedOrigins`: optional allowlist compared against the `Origin`
  header, falling back to `Referer` origin when `Origin` is absent. Entries must
  be non-empty tokens free of whitespace and control characters.
- `embed.frameAncestors`: optional values emitted as the `frame-ancestors`
  Content Security Policy when an embed read succeeds. Entries are validated with
  the same strictness as `allowedOrigins` (non-empty; no whitespace or control
  characters) so they are safe to write into the response header; an invalid
  entry is rejected with `400`.
- `service.requireAuthenticatedServices`: whether backing service reads still
  require authentication even when the route is public.
- `service.allowedServiceIds`: optional backing service allowlist.
- `publicLink.enabled`: master switch for public-link reads.
- `publicLink.links`: issued public link ids, token hashes, expiry, and
  revocation state.

`PATCH /policy` applies top-level policy deltas. Omitted fields keep their
current values; supplied nested `share`, `embed`, `service`, or `access` objects
replace that nested policy object. The response contains the updated route state
and one-time public-link creation fields only; it does not return the version
list.

Creating a public link is done through
`PATCH /api/v1/console/publications/{publicationId}/policy`:

```json
{
  "createPublicLink": {
    "label": "customer review",
    "token": "raw-link-token",
    "expiresAt": "2026-06-01T00:00:00Z"
  },
  "expectedEtag": "\"a2f1d6c0e7b94c3ab1d3371f0e0df125\""
}
```

The response returns `createdPublicLinkId` and `createdPublicLinkToken` exactly
once. Raw public-link tokens are never persisted or returned again; the route
policy stores only a SHA-256 token hash. Creating a link implicitly sets
`publicLink.enabled` to `true`; `publicLinkEnabled: false` disables public-link
reads without deleting existing link records. Revoking a link uses
`revokePublicLinkId` on the same policy endpoint and marks that link revoked.

## Public Route Reads

`GET /api/v1/published/{*routeSlug}` returns a client-safe
`PublishedArtifactView`. The view redacts role lists and public-link hashes, but
includes the fields clients need to render or launch the artifact:

```json
{
  "publicationId": "d8b3d8a0-2f52-4a24-8a59-5bd596a3b7f4",
  "routeSlug": "field/apps/parcel-review",
  "kind": "generated-app",
  "revision": 2,
  "versionId": "6f5c8945-a329-4680-b489-bb3a7ddc87b8",
  "title": "Parcel Review",
  "visibility": "public",
  "defaultViewBbox": {
    "crs": "EPSG:4326",
    "minX": -158.2,
    "minY": 21.2,
    "maxX": -157.6,
    "maxY": 21.8
  },
  "embeddable": true,
  "allowedEmbedOrigins": ["https://console.example"],
  "frameAncestors": ["https://console.example"],
  "appManifestId": "manifest-v2",
  "appBundleArtifactId": "bundle-v2",
  "contentHash": "8cd2...",
  "updatedAt": "2026-05-24T00:10:00Z"
}
```

Query parameters:

- `version`: optional revision selector (`1`, `v1`) or version id. Omit it to
  resolve the active route pointer.
- `expand=dependencies`: includes dependency references in the published view.
- `link` and `token`: public-link authorization. A link whose stored token hash
  is empty requires only the valid `link` id; a token-protected link requires
  both values.
- `embed=true`: enforces embed policy. When `frameAncestors` are configured and
  the request is allowed, the response includes a `Content-Security-Policy`
  `frame-ancestors ...` header.

Only `active` lifecycle routes resolve. Suspended, archived, unknown, or
malformed slugs are treated as not found for public reads.

Anonymous reads are allowed when the route visibility is `public`, when
`share.allowAnonymous` is true, or when a valid public link authorizes the read.
Private and team routes otherwise allow the publishing actor or explicit read
grants in `access`; without either, they fail closed (`401` for anonymous,
`403` for authenticated non-owners). Organization routes with no `access` policy
allow any authenticated principal; when `access` is present it is evaluated by
the shared access-policy evaluator. `embed=true` additionally enforces embed
policy and returns `403` when embedding is disabled or the request origin does
not match the configured allowlist.

## Persistence, Audit, Telemetry, And Caching

Postgres deployments store:

- `content_publication_versions`: append-only immutable versions.
- `content_publication_routes`: the mutable route pointer and current policy.
- `content_publication_events`: append-only operation/event records.

The Postgres migration blocks update/delete against version and event tables so
history cannot be rewritten through normal SQL writes. In-memory fallback
registration exists for tests and local compositions that have not registered
the durable Postgres store.

Management operations record audit events for publish, republish, rollback, and
policy update. Denied public-link and embed reads are also audited as
authorization events. The slice emits OpenTelemetry activities named under
`honua.publication.*` with tags for publication id, route slug, artifact kind,
resolved revision, and dependency count where applicable.

Public route reads use the `ContentPublishedRoute` output-cache policy. The
policy varies by route slug, `version`, `expand`, and `Accept`, and it only
caches anonymous reads with no `link`, `token`, or `embed` query parameter.
Cached responses are tagged with `content-publication` and
`content-route:{slug}`. Republish, rollback, and policy changes evict the global
publication tag plus the route-specific tag before returning the updated route
state.

## Follow-ons

- SDK clients should add typed helpers for management operations, public route
  resolution, public-link reads, embed reads, dependency expansion, and
  generated-app reopen-by-revision.
- Console UX documentation should treat publish/share/embed/rollback as
  server-owned state and should not store independent route or public-link
  truth in the UI.
- Open data, STAC, and DCAT publication routes remain outside this ticket unless
  they later adopt the same registry records.
