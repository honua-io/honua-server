# Console open-data publication API

Ticket #1214 adds a server-owned publication surface for Console open-data pages,
DCAT/data.json export, Schema.org Dataset preview, and STAC publication controls.
The implementation is intentionally bounded to the current Console content-store
baseline: page and STAC publication state are persisted through
`IOpenDataStore`, whose default server implementation is in-memory until the
persistent Console content store lands.

## Endpoints

Public anonymous endpoints:

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/open-data` | Lists anonymously readable open-data pages. |
| `GET` | `/open-data/{itemId}` | Reads one anonymous open-data page projection. |
| `GET` | `/open-data/catalog.json` | Emits a DCAT/data.json-compatible catalog for visible items. |

Admin endpoints:

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/v1/admin/open-data/{itemId}/eligibility` | Reports eligibility and all blocking reasons. |
| `GET` | `/api/v1/admin/open-data/{itemId}` | Reads server-owned page metadata and eligibility. |
| `PUT` | `/api/v1/admin/open-data/{itemId}` | Updates page metadata and publish flag. |
| `GET` | `/api/v1/admin/open-data/dcat/status` | Reports DCAT validation status for the public catalog. |
| `POST` | `/api/v1/admin/open-data/dcat/validate` | Validates one item or the public catalog without side effects. |
| `POST` | `/api/v1/admin/stac/publications` | Publishes an eligible item into the STAC control surface. |
| `GET` | `/api/v1/admin/stac/publications/{collectionId}` | Reads STAC publication status. |
| `PUT` | `/api/v1/admin/stac/publications/{collectionId}` | Updates STAC title/description overrides. |
| `DELETE` | `/api/v1/admin/stac/publications/{collectionId}` | Marks the STAC publication unpublished. |

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

Public open-data list, item, and catalog responses use short output-cache
policies tagged `open-data`. Admin page writes and STAC publication mutations
evict the open-data tag through `OutputCacheInvalidationService`.
