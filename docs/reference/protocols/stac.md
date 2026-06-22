# STAC

Honua serves a STAC API v1.0.0 (SpatioTemporal Asset Catalog) at `/stac` for standards-based discovery of spatiotemporal collections and items.

## Base endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/stac` | Catalog landing page. |
| GET | `/stac/conformance` | Conformance declaration. |
| GET | `/stac/openapi.json` | OpenAPI document. |
| GET | `/stac/collections` | Collection list. |
| GET | `/stac/collections/{collectionId}` | Collection metadata. |
| GET | `/stac/collections/{collectionId}/items` | Items in a collection. |
| GET | `/stac/collections/{collectionId}/items/{itemId}` | Single item. |
| GET, POST | `/stac/search` | Cross-collection item search (GET query string or POST JSON body). |
| GET | `/stac/queryables` | Catalog-level queryables (JSON Schema of filterable properties). |
| GET | `/stac/collections/{collectionId}/queryables` | Per-collection queryables (JSON Schema). |

## Search parameters

Accepted on `GET /stac/search` as query parameters and on `POST /stac/search` as JSON body fields:

| Parameter | Notes |
| --- | --- |
| `limit` | Page size; must be >= 1. |
| `bbox` | `xmin,ymin,xmax,ymax` spatial filter. |
| `datetime` | RFC 3339 instant or interval. |
| `collections` | Comma-separated collection ids (array in POST body). |
| `ids` | Comma-separated item ids. |
| `intersects` | GeoJSON geometry filter. |
| `fields` | Include/exclude field selection (fields extension). |
| `sortby` | Sort expressions (sort extension). |
| `filter`, `filter-lang`, `filter-crs` | CQL2 filtering (filter extension). |

## Conformance classes

`GET /stac/conformance` advertises:

- `https://api.stacspec.org/v1.0.0/core`
- `https://api.stacspec.org/v1.0.0/collections`
- `https://api.stacspec.org/v1.0.0/ogcapi-features`
- `https://api.stacspec.org/v1.0.0/item-search`
- `https://api.stacspec.org/v1.0.0/item-search#fields`
- `https://api.stacspec.org/v1.0.0/item-search#sort`
- `https://api.stacspec.org/v1.0.0/item-search#filter`

## Filter Extension (CQL2)

The Item Search Filter Extension is conformant against `stac-api-validator` (`--conformance filter`):

- The landing page (`/stac`) and each collection advertise their queryables document through the
  `http://www.opengis.net/def/rel/ogc/1.0/queryables` link relation; the link `href` matches the
  queryables document's `$id`.
- Queryables documents are JSON Schema (draft 2019-09) `type: object` documents describing the
  filterable properties.
- The queryables documents declare `additionalProperties: false`. A CQL2 filter that references a
  property outside the declared queryables set is rejected with a structured `400` (problem+json),
  which is the spec-permitted behavior for that declaration.

## Examples

```bash
# Browse collections
curl "https://server.example.com/stac/collections"
```

```bash
# Search items by bbox and time
curl -X POST "https://server.example.com/stac/search" \
  -H "Content-Type: application/json" \
  -d '{"collections":["imagery"],"bbox":[-122.5,37.7,-122.3,37.9],"datetime":"2025-01-01T00:00:00Z/..","limit":10}'
```

A read-only open-data STAC projection is also published per dataset under `/api/v1/open-data/stac` for datasets opted into open-data publication.

## Conformance

STAC is validated against the STAC API v1.0.0 specification; OGC CITE status for the rest of the surface lives in the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Publish rasters](../../guides/publish/publish-rasters.md)
