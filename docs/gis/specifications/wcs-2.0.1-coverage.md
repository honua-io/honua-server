# WCS 2.0.1 Coverage

Honua exposes a bounded OGC Web Coverage Service 2.0.1 KVP adapter for raster-backed layers. WCS returns coverage bytes from the existing `IRasterStore` pipeline; it does not create a separate raster backend.

## Endpoints

```text
GET /rest/services/{id}/ImageServer/WCS
GET /ogc/services/{serviceId}/wcs
```

The ImageServer route is layer-scoped and uses `{id}` as the bare integer coverage ID. The service-scoped OGC route lists and serves raster-backed layers in the named service that are visible to the caller and enabled for WCS.

## Operations

| Operation | Status | Notes |
| --- | --- | --- |
| `GetCapabilities` | Implemented | Returns WCS 2.0.1 XML with OWS service metadata, operations metadata, WCS service metadata, and `wcs:Contents` coverage summaries. |
| `DescribeCoverage` | Implemented | Accepts one or more repeated or comma-separated bare integer `COVERAGEID` values. Returns GML 3.2 bounds/grid metadata and `gmlcov:rangeType` band metadata. |
| `GetCoverage` | Implemented | Returns raw raster bytes from `IRasterStore.ExportImageAsync` for one coverage ID. Supports format, optional spatial trim, and optional output CRS. |

## Parameters

Common parameters:

| Parameter | Status | Notes |
| --- | --- | --- |
| `SERVICE` | Optional | Must be `WCS` when supplied. |
| `VERSION` | Optional | Only `2.0.1` is supported. |
| `REQUEST` | Optional for capabilities | Defaults to `GetCapabilities` when omitted. |

`DescribeCoverage`:

| Parameter | Status | Notes |
| --- | --- | --- |
| `COVERAGEID` | Required | Bare non-negative integer layer IDs only, for example `COVERAGEID=0` or `COVERAGEID=0,1`. |

`GetCoverage`:

| Parameter | Status | Notes |
| --- | --- | --- |
| `COVERAGEID` | Required | Exactly one bare integer layer ID. |
| `FORMAT` | Optional | Defaults to `image/tiff`. Supported values: `image/tiff`, `image/geotiff`, `tiff`, `tif`, `image/png`, `png`, `image/jpeg`, `jpg`, `jpeg`. |
| `SUBSET` | Optional | WCS trim syntax `axis(low,high)`. Supported axes: `x`, `y`, `E`, `N`, `Long`, `Lat`. One-axis trims fill the other axis from the raster extent. |
| `BBOX` | Optional | Convenience trim alias: `xmin,ymin,xmax,ymax`. Do not combine with `SUBSET`. |
| `SUBSETTINGCRS` / `BBOXCRS` | Optional | Parsed by the shared CRS parser. Defaults to the raster native CRS. |
| `OUTPUTCRS` | Optional | Parsed by the shared CRS parser and passed to `RasterQuery.OutputSrid`. |

Unsupported WCS extensions fail with OWS `ExceptionReport` XML instead of being ignored. Deferred parameters include `RANGESUBSET`, scaling parameters, interpolation parameters, `MEDIATYPE`, `datetime`/`TIME`, XML POST bodies, polygon trims, NetCDF, and multi-dimensional coverage slicing.

## Examples

```text
/rest/services/0/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1
/rest/services/0/ImageServer/WCS?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=0
/rest/services/0/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/tiff
/rest/services/0/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/png&SUBSET=Long(-122.4,-122.3)&SUBSET=Lat(37.7,37.8)
/ogc/services/test/wcs?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/jpeg&BBOX=-122.4,37.7,-122.3,37.8
```

## Relationship To Other Raster APIs

WCS is for raw coverage export. ImageServer remains the Esri-compatible raster surface for service metadata, rendering, identify, catalog query, tiles, statistics, histograms, and legend. OGC API Maps remains the modern OGC rendered-map surface. All three surfaces reuse the same raster store and raster query/export infrastructure.

`GetCoverage` returns the buffered `RasterResult.Data` byte array from the existing raster store. Large-raster streaming, range subset/band selection, scaling extensions, temporal/multidimensional coverage, strict schema-safe coverage aliases, and multi-raster WCS mosaic selection are follow-up scope.

## Errors

Invalid requests return OWS 2.0 `ExceptionReport` XML with a stable exception code such as `MissingParameterValue`, `InvalidParameterValue`, `InvalidAxisLabel`, `InvalidSubsetting`, `VersionNegotiationFailed`, `OperationNotSupported`, `NoSuchCoverage`, or `NoApplicableCode`. Responses do not expose SQL, stack traces, filesystem paths, connection strings, or provider internals.
