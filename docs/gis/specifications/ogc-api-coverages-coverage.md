# OGC API Coverages Coverage

Honua exposes a bounded OGC API Coverages surface for raster-backed collections. The adapter is a modern REST/JSON view over the shared raster catalog and `IRasterStore.ExportImageAsync` pipeline; it does not create a separate coverage backend.

## Endpoints

```text
GET /ogc/coverages
GET /ogc/coverages/conformance
GET /ogc/coverages/api
GET /ogc/coverages/openapi.json
GET /ogc/coverages/collections
GET /ogc/coverages/collections/{collectionId}
GET /ogc/coverages/collections/{collectionId}/schema
GET /ogc/coverages/collections/{collectionId}/coverage
```

Collection discovery only returns layers that are accessible to the caller, enabled for `OGC-API-Coverages`, and backed by a primary raster in `IRasterStore`. A null `CatalogMetadata.EnabledProtocols` list keeps the default "all protocols enabled" behavior.

## Implemented Behavior

| Resource | Status | Notes |
| --- | --- | --- |
| Landing page | Implemented | JSON/HTML metadata with links to conformance, OpenAPI, and collections. |
| Conformance | Implemented | Advertises OGC API Common JSON/OpenAPI support plus Coverages core, geodata coverage, GeoTIFF, field selection, and CRS support. |
| OpenAPI | Implemented | Static runtime document served at `/api` and `/openapi.json`, mirrored under `docs/developer/api-specs/ogc-api-coverages.json`. |
| Collections | Implemented | Returns OGC collection objects with `itemType: "coverage"`, CRS metadata, extent, grid/domain metadata, schema links, and coverage links. |
| Collection schema | Implemented | Returns JSON Schema properties named `band_1`, `band_2`, etc. for `properties` field selection. |
| Coverage retrieval | Implemented | Returns GeoTIFF by default, or PNG by `f=png` / `Accept: image/png`, through the shared raster export pipeline. |

## Coverage Retrieval Parameters

| Parameter | Status | Notes |
| --- | --- | --- |
| `f` | Implemented | `geotiff`, `tiff`, `tif`, `image/tiff`, `png`, or `image/png`. NetCDF and JPEG fail clearly with `400`. |
| `bbox` | Implemented | Spatial subset as `xmin,ymin,xmax,ymax`. Defaults to CRS84 axis order unless `bbox-crs` is supplied. |
| `bbox-crs` | Implemented | Parsed by the shared CRS parser. Supports CRS84, EPSG URIs/URNs, `EPSG:{code}`, and bare SRIDs. |
| `crs` | Implemented | Output CRS. Passed to `RasterQuery.OutputSrid`; non-CRS84 outputs include `Content-Crs`. |
| `properties` | Implemented | Comma-separated band field names such as `band_3,band_1`. Order is preserved and duplicate or out-of-range bands are rejected. |
| `resolution` | Implemented | Positive pixel size as one value or `x,y`. Maps to `RasterQuery.PixelSize`. |
| `scale-factor` | Implemented | Positive multiplier over native pixel size when grid metadata is available. |
| `scale-size` | Implemented | Output size as `width,height` or `x(width),y(height)` / `Lon(width),Lat(height)`. |
| `datetime` | Deferred | Returns `400`; temporal/multidimensional coverage selection is follow-up scope. |
| `subset` | Deferred | Returns `400`; use `bbox` for MVP spatial subsetting. |
| `scale-axes` | Deferred | Returns `400`; use `resolution`, `scale-factor`, or `scale-size`. |

Only one scaling control is allowed per coverage request. `scale-size` accepts values from 1 through 8192 for each axis.

## Response Contract

Coverage bytes return `200 OK` with `image/tiff` for GeoTIFF or `image/png` for PNG. When the raster result reports an extent, Honua emits `Content-Bbox` as `xmin,ymin,xmax,ymax`. When the output CRS is not WGS 84, Honua emits `Content-Crs` as an EPSG URI. Coverage responses also include a `Link` header with `self`, GeoTIFF alternate, and PNG alternate links.

Validation failures return the shared Honua problem response with `400 Bad Request`. An unsupported `Accept` header returns `406 Not Acceptable`. Unknown or inaccessible collections return `404 Not Found`; unexpected server failures return `500` with sanitized detail.

## Examples

```bash
# Discover coverage collections.
curl https://your-honua-server.com/ogc/coverages/collections

# Inspect one collection and its selectable range fields.
curl https://your-honua-server.com/ogc/coverages/collections/0
curl https://your-honua-server.com/ogc/coverages/collections/0/schema

# Retrieve a GeoTIFF clip in the default CRS84 bbox coordinate order.
curl -o coverage.tif \
  "https://your-honua-server.com/ogc/coverages/collections/0/coverage?bbox=-122.5,37.7,-122.3,37.9"

# Select bands, reproject output, and request a fixed output size.
curl -o coverage.tif \
  "https://your-honua-server.com/ogc/coverages/collections/0/coverage?properties=band_3,band_1&crs=EPSG:3857&scale-size=Lon(512),Lat(512)"

# Ask for PNG by HTTP negotiation.
curl -H "Accept: image/png" -o coverage.png \
  https://your-honua-server.com/ogc/coverages/collections/0/coverage
```

GDAL/QGIS-style clients should start from the landing page or collections resource, follow the
collection `schema` and `coverage` links, and request `image/tiff`/GeoTIFF for georeferenced
coverage data. Automated GDAL CLI certification is not part of the normal server test lane; use the
examples above as the manual smoke path until an external client-certification lane is added.

## Caching And Telemetry

Landing, conformance, and OpenAPI metadata use bounded anonymous output-cache policies that vary by `f` and `Accept`. Collection listing, collection metadata, schema, and coverage retrieval are not output-cached because they are access-filtered or high-cardinality. Coverage retrieval emits OGC Coverages protocol telemetry with collection/layer identifiers, output format, bbox presence, selected field count, result content type, and byte count.

## Relationship To Other Raster APIs

OGC API Coverages is for modern coverage discovery and raw coverage export. WCS remains the classic OGC KVP/XML adapter. ImageServer remains the Esri-compatible raster surface for service metadata, identify, catalog query, tiles, statistics, histograms, and legend. OGC API Maps remains the modern rendered-map surface. All raster export routes share the same raster store and raster query/export infrastructure.

## Known Deferrals

The MVP does not implement NetCDF, JPEG coverage payloads, multidimensional `datetime` slicing, `subset` axis slicing, `scale-axes`, strict CoverageJSON encodings, per-scene catalog selection, multipart responses, or tiled coverage delivery. Use tile/cache-hinted surfaces for seeded raster delivery and high-volume map viewers.
