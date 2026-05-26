# Console open-data publication API

Ticket #1214 adds a server-owned publication surface for Console open-data pages,
DCAT/data.json export, Schema.org Dataset preview, and Console STAC publication
controls. The implementation is intentionally bounded to the current Console
content-store baseline: page and STAC publication state are persisted through
`IOpenDataStore`. The normal PostgreSQL provider stores those records in
dedicated `honua.open_data_pages` and
`honua.open_data_stac_publications` tables; `InMemoryOpenDataStore` is an opt-in
development/test fallback for hosts that do not register the PostgreSQL provider,
selected with `OpenData:UseInMemoryStore=true`. Hosts that set
`OpenData:Enabled=true` with an explicit non-PostgreSQL provider and without
either a registered `IOpenDataStore` or the explicit in-memory fallback fail
during service registration rather than discovering the missing store at request
time. Anonymous Open Data/DCAT/STAC
publication projection is controlled by the explicit deployment capability flag
`OpenData:Enabled=true`; the default is disabled, and eligibility responses
include `OpenDataDisabled` when the flag is off. The STAC controls create and
update Console-side publication state and the public `/stac` collection/catalog
routes project eligible published records alongside Metadata v2-backed STAC
publications.

## Surface

Public anonymous endpoints:

| Method | Route | Response | Purpose |
|---|---|---|
| `GET` | `/open-data?cursor=&limit=` | `OpenDataListResponse` | Lists anonymously readable open-data pages. `limit` defaults to `25` and is capped at `200`; `nextCursor` is an opaque server cursor. |
| `GET` | `/open-data/{itemId}` | `OpenDataItemResponse`, `SchemaOrgDatasetResponse`, or empty `404` | Reads one anonymous open-data page projection. Use `Accept: application/ld+json` for the Schema.org Dataset JSON-LD projection; normal JSON returns the full page response. |
| `GET` | `/open-data/catalog.json` | `DcatCatalogResponse` | Emits a DCAT/data.json-compatible catalog for up to the first 200 visible items in stable item-id order. |

Admin endpoints:

| Method | Route | Response | Purpose |
|---|---|---|
| `GET` | `/api/v1/admin/open-data/{itemId}/eligibility` | `ApiResponse<OpenDataEligibility>` | Reports eligibility and all blocking reasons. |
| `GET` | `/api/v1/admin/open-data/{itemId}` | `ApiResponse<OpenDataPageAdminResponse>` | Reads server-owned page metadata and eligibility, synthesizing a default unpublished page from the Console item when no page record exists. |
| `PUT` | `/api/v1/admin/open-data/{itemId}` | `ApiResponse<OpenDataPageAdminResponse>` | Updates page metadata and publish flag. |
| `GET` | `/api/v1/admin/open-data/dcat/status` | `ApiResponse<OpenDataDcatStatusResponse>` | Reports DCAT validation status for the public catalog. |
| `POST` | `/api/v1/admin/open-data/dcat/validate` | `ApiResponse<OpenDataValidationSummary>` | Validates one item or the public catalog without side effects. |
| `POST` | `/api/v1/admin/stac/publications` | `201 Created` with `ApiResponse<StacPublicationStatusResponse>` | Publishes an eligible item into the Console STAC control surface. |
| `GET` | `/api/v1/admin/stac/publications/{collectionId}` | `ApiResponse<StacPublicationStatusResponse>` | Reads STAC publication status. |
| `PUT` | `/api/v1/admin/stac/publications/{collectionId}` | `ApiResponse<StacPublicationStatusResponse>` | Updates STAC title/description overrides. |
| `DELETE` | `/api/v1/admin/stac/publications/{collectionId}` | `204 No Content` | Marks the STAC publication unpublished. |

Admin routes require the normal admin authorization posture and use the
standard `ApiResponse<T>` envelope for successful reads/writes and expected
client errors, except `DELETE /api/v1/admin/stac/publications/{collectionId}`,
which returns `204 No Content` on success. Internal failures use RFC 7807
`ProblemDetails` with generic messages. Public anonymous routes return bare
protocol JSON, not the admin envelope.

STAC publish, update, and unpublish routes also run through
`OperatorApprovalGate` with `OperatorResourceType.Catalog` and
`OperatorOperation.Publish` before changing publication state. Unpublish is
marked destructive so deployments with destructive-action approval policies can
gate it independently.

`PUT /api/v1/admin/open-data/{itemId}` is a merge update over the stored page
or the synthesized default page: omitted fields keep their existing value,
blank string fields normalize to `null`, tags are trimmed and de-duplicated
case-insensitively, and distributions without any `downloadUrl`, `accessUrl`,
or `serviceUrl` are dropped. JSON `null` and omission are equivalent for
object-valued fields in the current request model, so `publisher`,
`contactPoint`, `spatialCoverage`, and `temporalCoverage` are not cleared by
sending `null`; send replacement objects instead. Empty `tags`,
`distributions`, and `provenanceReferences` arrays clear those lists.
`isPublished` controls whether the page is eligible for anonymous reads after
policy and validation checks pass.

The publish collection id comes from the request `collectionId`, falling back
to a slug of the source item name and then the item id, lowercased with
non-alphanumeric runs collapsed to single hyphens and capped at 120 characters.
`POST /api/v1/admin/stac/publications` returns `409 Conflict` when that
collection is already published, or when the resolved collection id collides
with an existing Metadata v2-backed STAC collection so Console open-data
publications cannot shadow Metadata STAC collections. It returns `400 Bad
Request` with eligibility details when the source item is ineligible, and `404
Not Found` when the source item is missing. If a collection was previously
unpublished, publishing the same collection id reuses the existing record and
returns it to `published`. `DELETE` preserves the publication record with
status `unpublished`; subsequent `GET` requests return that status instead of
`404`.

The STAC publication response is a Console control-plane readback:
`collectionId`, `itemId`, `status`, `publicStacCollectionUrl`, optional
title/description overrides, current eligibility when the source item still
exists, and `updatedAt`. `GET /stac/collections/{collectionId}` returns the
standards-shaped STAC Collection projection for published, eligible records
whose backing open-data page remains anonymous-readable and returns `404` after
the record is unpublished, the page is unpublished, or the source becomes
ineligible. STAC
collection `license` values are emitted as STAC-compatible identifiers: known
open-data license URLs such as Creative Commons BY 4.0 are normalized to SPDX
identifiers like `CC-BY-4.0`; unrecognized absolute license URLs fall back to
`proprietary` in the STAC `license` field and are retained as a `rel=license`
link.

## Eligibility

Eligibility is evaluated from the Console content item plus stored open-data
page state. The response includes every blocking reason found. Current blocking
codes are:

| Code | Meaning |
|---|---|
| `ItemNotFound` | The Console content item does not exist. |
| `OpenDataDisabled` | The deployment has not enabled anonymous Open Data publication with `OpenData:Enabled=true`. |
| `MissingTitle` | Neither page metadata nor the source item supplies a title. |
| `LifecycleBlocked` | The source item is archived or retired. |
| `PolicyBlocked` | The source item is not `public`, so anonymous reads are not allowed. |
| `ComplianceBlocked` | The source item has a `legalHold=true` or `complianceBlocked=true` label. |

Warnings are non-blocking catalog quality issues. Missing description and
missing license are reported as documented DCAT exceptions.

DCAT validation uses `RequiredFieldMissing` for missing title, description,
keyword, publisher, contact email, license, and distribution checks. Missing
description, keyword, publisher, contact email, license, and distribution are
documented exceptions and do not block publication by themselves. Missing title
is blocking. Spatial validation can also emit `UnsupportedCrs`,
`InvalidBoundingBox`, or `InvalidCoordinateRange`; temporal validation can emit
`InvalidTemporalExtent`.

## Anonymous Denial

`GET /open-data/{itemId}` returns `404 Not Found` with an empty body for missing,
private, unpublished, ineligible, and validation-blocked items. This keeps the
public surface from revealing whether a private item exists. Operators should
use the admin eligibility endpoint to diagnose why a page is not visible.

Anonymous reads require all of the following:

- `OpenData:Enabled=true` is configured for the deployment;
- the stored page has `isPublished: true`;
- the source Console item has `visibility: "public"`;
- the source item is not archived or retired;
- the source item does not carry `legalHold=true` or `complianceBlocked=true`;
- DCAT validation has no blocking issues.

Documented DCAT exceptions for sparse but intentional metadata, such as missing
description, keyword, publisher, contact, license, or distribution, do not block
publication by themselves. Missing title, invalid spatial coverage, unsupported
spatial CRS, and temporal start-after-end remain blocking validation issues.
When the deployment capability is disabled, eligibility includes the
`OpenDataDisabled` reason with field `openData.enabled`, and public list, item,
DCAT, and Console-managed STAC projections omit those records.

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
| `spatialCoverage` | GeoJSON `Polygon` in `dataset[].spatial` with `coordinates` encoded as rings of positions | not emitted separately |
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
and STAC publication mutations evict the `open-data`, `metadata`, and
`stac-metadata` tags, plus the per-item `open-data:{itemId}` tag, through
`OutputCacheInvalidationService`.

## MVP Boundaries

- The PostgreSQL provider is durable. Non-PostgreSQL providers need a bounded
  provider-specific `IOpenDataStore` before they can claim durable open-data
  publication state.
- `/open-data/catalog.json` is generated through the public list projection and
  currently includes at most 200 visible records.
- Undecodable list cursors (bad base64 or a non-numeric payload) restart at the
  first page instead of returning a client error. A decodable cursor is clamped
  into range, so an out-of-range (stale) value past the current end resolves to
  an empty final page rather than restarting at the first page.
- Public Schema.org data is embedded under `schemaOrg` in the normal JSON
  response and is also available as `application/ld+json` from the same item
  route.
- Console-managed public STAC collections expose collection metadata and links;
  item search remains backed by the existing Metadata v2 STAC publication data
  path until Console content items carry canonical feature/raster bindings.
