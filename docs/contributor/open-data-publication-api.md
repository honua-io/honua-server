# Console open-data publication API

Ticket #1214 adds a server-owned publication surface for Console open-data pages,
DCAT/data.json export, Schema.org Dataset preview, and STAC publication controls.
The implementation is intentionally bounded to the current Console content-store
baseline: page and STAC publication state are persisted through
`IOpenDataStore`, whose default server implementation is in-memory until the
persistent Console content store lands.

## Surface

Public anonymous endpoints:

| Method | Route | Response | Purpose |
|---|---|---|
| `GET` | `/open-data?cursor=&limit=` | `OpenDataListResponse` | Lists anonymously readable open-data pages. `limit` defaults to `25` and is capped at `200`; `nextCursor` is an opaque server cursor. |
| `GET` | `/open-data/{itemId}` | `OpenDataItemResponse` or empty `404` | Reads one anonymous open-data page projection. |
| `GET` | `/open-data/catalog.json` | `DcatCatalogResponse` | Emits a DCAT/data.json-compatible catalog for visible items. |

Admin endpoints:

| Method | Route | Response | Purpose |
|---|---|---|
| `GET` | `/api/v1/admin/open-data/{itemId}/eligibility` | `ApiResponse<OpenDataEligibility>` | Reports eligibility and all blocking reasons. |
| `GET` | `/api/v1/admin/open-data/{itemId}` | `ApiResponse<OpenDataPageAdminResponse>` | Reads server-owned page metadata and eligibility. |
| `PUT` | `/api/v1/admin/open-data/{itemId}` | `ApiResponse<OpenDataPageAdminResponse>` | Updates page metadata and publish flag. |
| `GET` | `/api/v1/admin/open-data/dcat/status` | `ApiResponse<OpenDataDcatStatusResponse>` | Reports DCAT validation status for the public catalog. |
| `POST` | `/api/v1/admin/open-data/dcat/validate` | `ApiResponse<OpenDataValidationSummary>` | Validates one item or the public catalog without side effects. |
| `POST` | `/api/v1/admin/stac/publications` | `201 Created` with `ApiResponse<StacPublicationStatusResponse>` | Publishes an eligible item into the STAC control surface. |
| `GET` | `/api/v1/admin/stac/publications/{collectionId}` | `ApiResponse<StacPublicationStatusResponse>` | Reads STAC publication status. |
| `PUT` | `/api/v1/admin/stac/publications/{collectionId}` | `ApiResponse<StacPublicationStatusResponse>` | Updates STAC title/description overrides. |
| `DELETE` | `/api/v1/admin/stac/publications/{collectionId}` | `204 No Content` | Marks the STAC publication unpublished. |

Admin routes require the normal admin authorization posture and use the
standard `ApiResponse<T>` envelope for successful reads/writes and expected
client errors, except `DELETE /api/v1/admin/stac/publications/{collectionId}`,
which returns `204 No Content` on success. Internal failures use RFC 7807
`ProblemDetails` with generic messages. Public anonymous routes return bare
protocol JSON, not the admin envelope.

`PUT /api/v1/admin/open-data/{itemId}` is a merge update: omitted nullable
fields keep their existing value, blank strings normalize to `null`, tags are
trimmed and de-duplicated case-insensitively, and distributions without any
`downloadUrl`, `accessUrl`, or `serviceUrl` are dropped. `isPublished` controls
whether the page is eligible for anonymous reads after policy and validation
checks pass.

`POST /api/v1/admin/stac/publications` returns `409 Conflict` when the target
collection is already published, `400 Bad Request` with eligibility details
when the source item is ineligible, and `404 Not Found` when the source item is
missing. The published collection id is the request `collectionId` when
supplied, otherwise a slug of the source item name, otherwise the item id
(lowercased, with non-alphanumeric runs collapsed to single hyphens and capped
at 120 characters); the `201 Created` `Location` header and
`publicStacCollectionUrl` both resolve to `/stac/collections/{collectionId}`.
Re-publishing a previously unpublished collection reuses the existing record and
preserves its original `createdAt`. `DELETE` preserves the publication record
with status `unpublished`; subsequent `GET` requests return that status instead
of `404`.

## Eligibility

Eligibility is evaluated from the Console content item plus stored open-data
page state. The response includes every blocking reason found. Current blocking
codes are:

| Code | Meaning |
|---|---|
| `ItemNotFound` | The Console content item does not exist. |
| `MissingTitle` | Neither page metadata nor the source item supplies a title. |
| `LifecycleBlocked` | The source item is archived or retired. |
| `PolicyBlocked` | The source item is not `public`, so anonymous reads are not allowed. |
| `ComplianceBlocked` | The source item has a `legalHold=true` or `complianceBlocked=true` label. |

Warnings are non-blocking catalog quality issues. Missing description and
missing license are reported as documented DCAT exceptions.

## Anonymous Denial

`GET /open-data/{itemId}` returns `404 Not Found` with an empty body for missing,
private, unpublished, ineligible, and validation-blocked items. This keeps the
public surface from revealing whether a private item exists. Operators should
use the admin eligibility endpoint to diagnose why a page is not visible.

Anonymous reads require all of the following:

- the stored page has `isPublished: true`;
- the source Console item has `visibility: "public"`;
- the source item is not archived or retired;
- the source item does not carry `legalHold=true` or `complianceBlocked=true`;
- DCAT validation has no blocking issues.

Documented DCAT exceptions for sparse but intentional metadata, such as missing
description, keyword, publisher, contact, license, or distribution, do not block
publication by themselves. Missing title, invalid spatial coverage, unsupported
spatial CRS, and temporal start-after-end remain blocking validation issues.

## Field Mapping

Open-data page fields map to DCAT/data.json and Schema.org Dataset as follows:

| Page field | DCAT/data.json | Schema.org Dataset |
|---|---|---|
| `title` | `dataset[].title` | `name` |
| `description` | `dataset[].description` | `description` |
| `publisher` | `dataset[].publisher` | `publisher` |
| `contactPoint` | `dataset[].contactPoint` | not emitted separately |
| `license` | `dataset[].license` | `license` |
| `tags` | `dataset[].keyword` | `keywords` |
| `landingPage` | `dataset[].landingPage` | `url` |
| `distributions` | `dataset[].distribution[]` | `distribution[]` |
| `spatialCoverage` | GeoJSON `Polygon` in `dataset[].spatial` | not emitted separately |
| `temporalCoverage` | `dataset[].temporal` | not emitted separately |
| `provenanceReferences` | page projection only | page projection only |

Spatial coverage must be EPSG:4326. Invalid bounds, non-WGS84 CRS values, and a
temporal start later than end are blocking validation issues. Missing DCAT
quality fields are returned as documented exceptions so Console can preview and
publish intentionally incomplete catalogs while surfacing the gaps.

## Caching

Public open-data list, item, and catalog responses use one-minute output-cache
policies tagged `open-data` and `metadata`. The list policy varies by the
`cursor` and `limit` query values and `Accept`; the item policy varies by
`itemId` and `Accept`; the catalog policy varies by `Accept`. Admin page writes
and STAC publication mutations evict the `open-data` and `metadata` tags, plus
the per-item `open-data:{itemId}` tag, through `OutputCacheInvalidationService`.
