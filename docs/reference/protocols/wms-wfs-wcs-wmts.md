# WMS, WFS, WCS, WMTS (classic OGC)

Honua serves the classic OGC KVP/XML web services for clients that have not moved to the OGC API family: desktop GIS, legacy integrations, and CITE-certified workflows.

## Supported versions

| Service | Versions | Notes |
| --- | --- | --- |
| WMS | 1.3.0, 1.1.1 | Version negotiated per request; 1.3.0 is default. |
| WFS | 2.0.0, 1.1.0, 1.0.0 | Single dispatcher endpoint; version negotiated via `VERSION`/`ACCEPTVERSIONS`. |
| WCS | 2.0.1 | KVP only. |
| WMTS | 1.0.0 | KVP and RESTful tile paths. |

## Base endpoints

| Method | Path | Service |
| --- | --- | --- |
| GET, POST | `/wfs` | WFS 2.0/1.1/1.0 (POST accepts XML request bodies). |
| GET | `/ogc/services/{serviceId}/wms` | WMS, scoped to one service. |
| GET | `/rest/services/{serviceId}/MapServer/WMS` | WMS alias on the GeoServices route. |
| GET | `/ogc/services/{serviceId}/wmts` | WMTS KVP. |
| GET | `/rest/services/{serviceId}/MapServer/WMTS`, `.../WMTS/{**restPath}` | WMTS KVP + RESTful tile paths. |
| GET | `/ogc/services/{serviceId}/wcs` | WCS 2.0.1, scoped to one service. |
| GET | `/rest/services/{serviceId}/ImageServer/WCS` | WCS 2.0.1, layer-scoped (`COVERAGEID` is the bare integer layer id). |

## WMS operations

| Operation | Key parameters |
| --- | --- |
| `GetCapabilities` | `SERVICE=WMS`, optional `VERSION`. |
| `GetMap` | `LAYERS`, `STYLES`, `BBOX`, `CRS` (1.3.0) / `SRS` (1.1.1), `WIDTH`, `HEIGHT`, `FORMAT` (`image/png`, `image/jpeg`), `TRANSPARENT`, `BGCOLOR`, `EXCEPTIONS`. |
| `GetFeatureInfo` | `GetMap` parameters plus `QUERY_LAYERS`, `I`/`J` (1.3.0) or `X`/`Y` (1.1.1), `INFO_FORMAT`, `FEATURE_COUNT`. |

Axis-order quirk: WMS 1.3.0 `BBOX` follows the CRS-defined axis order (lat,lon for EPSG:4326); WMS 1.1.1 always uses lon,lat. Honua applies the correct order per negotiated version.

```bash
curl -o map.png "https://server.example.com/ogc/services/roads/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=0&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=800&HEIGHT=600&FORMAT=image/png"
```

## WFS operations

All WFS versions share `GET/POST /wfs`. WFS 2.0 operations:

| Operation | Notes |
| --- | --- |
| `GetCapabilities` | Version negotiation via `ACCEPTVERSIONS`. |
| `DescribeFeatureType` | GML 3.2 application schema. |
| `GetFeature` | `TYPENAMES`, `BBOX`, `FILTER` (FES 2.0 XML), `COUNT`, `STARTINDEX`, `SORTBY`, `OUTPUTFORMAT`, `SRSNAME`, stored query `GetFeatureById`. |
| `GetPropertyValue` | Single property projection. |
| `Transaction` | Insert/Update/Delete (CITE-validated transactional slice). |
| `ListStoredQueries`, `DescribeStoredQueries`, `CreateStoredQuery`, `DropStoredQuery` | Stored query management. |

```bash
curl "https://server.example.com/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAMES=roads&COUNT=10"
```

## WCS 2.0.1 operations

| Operation | Key parameters |
| --- | --- |
| `GetCapabilities` | `ACCEPTVERSIONS` (must include `2.0.1` when supplied), `SECTIONS`. |
| `DescribeCoverage` | `COVERAGEID` — one or more bare integer layer ids (`COVERAGEID=0,1`). |
| `GetCoverage` | `COVERAGEID` (exactly one), `FORMAT` (default `image/tiff`; also GeoTIFF/PNG/JPEG aliases), `SUBSET=axis(low,high)` trims, `BBOX` (convenience alias, not combinable with `SUBSET`), `SUBSETTINGCRS`/`BBOXCRS`, `OUTPUTCRS`, `RANGESUBSET` (band selection), scaling (`SCALESIZE`/`SCALEFACTOR`/`SCALEAXES`/`SCALEEXTENT`), `INTERPOLATION` (nearest/linear/cubic resampling), and temporal subsetting (`SUBSET=phenomenonTime(...)`, plus `DATETIME`/`TIME` aliases). `SUBSET` also accepts any other named axis (e.g. a vertical/elevation axis), validated against the coverage's registered dimension axes. |

Temporal subsetting selects the coverage only when its acquisition instant falls inside the requested window; a non-intersecting window yields an `InvalidSubsetting` exception. Unsupported `GetCoverage` parameters (`SIZE`/`WIDTH`/`HEIGHT`/`RESOLUTION`, `MEDIATYPE`, XML POST) return a 501 OWS `ExceptionReport` rather than being silently ignored; an unsupported `INTERPOLATION` method returns an `InterpolationMethodNotSupported` exception. The additive read parameters are CITE-neutral (no new conformance class is advertised). Errors use OWS 2.0 `ExceptionReport` XML with stable exception codes.

`SUBSET` axis labels resolve in three tiers: the spatial axes (`x`/`E`/`Long`/`Lon` and `y`/`N`/`Lat`) trim the grid; `phenomenonTime` slices against the coverage acquisition time; and any further named axis is treated as an additional dimension subset. When the layer has a registered multidimensional (Zarr) store, its declared additional axes (vertical/elevation/named) are resolved: a coordinate-valued slice on a declared axis is resolved to a concrete grid-index slice via the shared coordinate-axis indexer (out-of-range coordinates return `InvalidSubsetting`). A slice on an axis the coverage does not declare returns `InvalidAxisLabel`; a malformed value returns `InvalidSubsetting`. Because the classic `GetCoverage` export path serves over the primary 2D raster and cannot yet read Zarr-slice pixels, a resolved-but-unservable slice returns `OperationNotSupported` pointing clients at the OGC API - Coverages endpoint (which serves per-slice CoverageJSON). Wiring the Zarr export pipeline into classic WCS is the open part of issue #1872.

```bash
curl -o coverage.tif "https://server.example.com/rest/services/0/ImageServer/WCS?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCoverage&COVERAGEID=0&FORMAT=image/tiff&SUBSET=Long(-122.4,-122.3)&SUBSET=Lat(37.7,37.8)"
```

## WMTS operations

| Operation | Notes |
| --- | --- |
| `GetCapabilities` | KVP and RESTful (`.../WMTS/1.0.0/WMTSCapabilities.xml` style paths via `{**restPath}`). Advertises the reserved built-in gridsets (`WebMercatorQuad`, `WorldCRS84Quad`) plus any operator-defined custom gridsets from the `TileMatrixSets` configuration section, with per-layer links. `TopLeftCorner` follows the advertised CRS axis order (CRS84 is longitude/latitude; geographic EPSG identifiers are latitude/longitude) and preserves configured origin precision. |
| `GetTile` | `LAYER`, `STYLE`, `TILEMATRIXSET`, `TILEMATRIX`, `TILEROW`, `TILECOL`, `FORMAT`, optional `TIME` (temporal layers) and `ELEVATION` (elevation-aware layers); serves built-in and custom gridsets. RESTful tile paths also supported. |
| `GetFeatureInfo` | Tile-coordinate identify with `I`/`J` and `INFOFORMAT`; resolves the requested gridset through the same `ITileMatrixSetRegistry` as `GetTile`, so the built-in `WebMercatorQuad`/`WorldCRS84Quad` gridsets and operator-defined custom gridsets are supported. The clicked pixel is mapped to a world coordinate using the gridset's own origin, cell size and matrix dimensions (WebMercatorQuad stays byte-identical to before); unsupported gridsets are rejected with `InvalidParameterValue`. |

```bash
curl -o tile.png "https://server.example.com/ogc/services/roads/wmts?SERVICE=WMTS&VERSION=1.0.0&REQUEST=GetTile&LAYER=0&STYLE=default&TILEMATRIXSET=EPSG:3857&TILEMATRIX=12&TILEROW=1586&TILECOL=655&FORMAT=image/png"
```

## Conformance

All four classic services are OGC CITE certified at 100% (WMS 1.3: 199/199, WFS 1.0/1.1/2.0: 162/39/167, WCS 2.0: 82/82, WMTS 1.0: 60/60). The certified counts cover the built-in gridsets and parameters; custom-gridset and elevation-dimension behaviour is additive and CITE-neutral (re-validation pending). Authoritative status: [API standards summary](../compatibility/ogc-conformance.md) and [cite-status.md](../../cite-status.md).

## Guides that use this

- [Connect from QGIS](../../guides/connect/qgis.md)
- [Migrate from GeoServer](../../guides/migrate/from-geoserver.md)
- [Publish rasters](../../guides/publish/publish-rasters.md)
