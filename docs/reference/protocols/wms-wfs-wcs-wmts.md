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
| `GetLegendGraphic` | `LAYER`, optional `STYLE` (`default`), `FORMAT` (`image/png`), `WIDTH`/`HEIGHT` (swatch size hint, default 20x20), `SCALE`. |

Axis-order quirk: WMS 1.3.0 `BBOX` follows the CRS-defined axis order (lat,lon for EPSG:4326); WMS 1.1.1 always uses lon,lat. Honua applies the correct order per negotiated version.

```bash
curl -o map.png "https://server.example.com/ogc/services/roads/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=0&STYLES=&CRS=EPSG:4326&BBOX=37.7,-122.5,37.9,-122.3&WIDTH=800&HEIGHT=600&FORMAT=image/png"
```

### GetLegendGraphic

`GetLegendGraphic` is not part of the WMS 1.3.0 core specification, but it is universally
implemented and Honua serves it on both WMS routes. Legends are generated from the
canonical MapLibre GL style through the same style-resolution and SkiaSharp rendering path
`GetMap` draws with, so a swatch always shows the paint the map actually applies.

`WIDTH`/`HEIGHT` size the individual swatch (GeoServer's convention); the returned image
grows to fit the stacked entries and their labels. `SCALE` is a scale denominator and
filters entries by each style layer's `minzoom`/`maxzoom`. It is converted to a zoom level
through the same shared derivation every render path uses: a scale denominator is defined
against the OGC standardized 0.28 mm pixel, which fixes a ground resolution, and that
resolution derives the MapLibre zoom (whose world spans `512 * 2^zoom` pixels). A legend
therefore gates at exactly the zoom a `GetMap` of the same ground resolution gates at.
Omitting `SCALE` leaves the zoom underivable, so `minzoom`/`maxzoom` do not apply and every
style layer contributes — matching a `GetMap` with no zoom context.

Capabilities advertise `LegendURL` inside a layer's `<Style>` element **only** for layers
that can actually produce a legend — a layer whose style contains no painted
(`fill`/`line`/`circle`) layer is not advertised, because `GetMap` draws nothing for it.
`LegendURL` deliberately omits the optional `width`/`height` attributes: the composed image
size depends on label metrics that are not known until render time.

Data-driven expressions resolve to discrete entries where the expression permits it:

| Expression | Legend behavior |
| --- | --- |
| `match` on an attribute | One entry per label (grouped labels expand), plus an `Other` entry for the fallback arm. |
| `step` on an attribute | One entry per band, labelled `< first`, `lo - hi`, `>= last`. |
| `interpolate` over colors | One entry per ramp stop, labelled with the stop value. A stop is resolved exactly — the evaluator returns that stop's own output rather than blending toward a neighbour — so `linear`, `exponential` and `cubic-bezier` ramps all sample identically and each swatch is the color `GetMap` paints for that value. The ramp remains continuous between the labelled stops. |
| `case`, or any input that is not a plain attribute read | **Not representable.** Branches are arbitrary predicates with no finite attribute domain to enumerate; a single representative entry is returned and a `Warning` header explains why. |

Where a legend cannot faithfully represent the style, the response carries a `Warning`
header describing the limitation instead of silently showing a misleading swatch. Legends
are capped at 64 entries; truncation is reported the same way.

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

`GetCapabilities` advertises the transformable output/subsetting CRS values in the `wcs:ServiceMetadata` `wcs:Extension` slot (`crs:crsSupported` per value), covering each visible coverage's native CRS plus the default WGS84 (`EPSG:4326`) and WebMercator (`EPSG:3857`) identifiers, filtered to what the CRS registry can resolve. Because `wcs:Extension` is an `xs:any` slot in `wcsAll.xsd`, this is purely additive and keeps the document valid for the WCS core ETS — the WCS CRS-extension conformance class (OGC 11-053r1) is deliberately **not** declared in `ows:Profile` or OperationsMetadata; only the advertisement values are emitted. `GetCoverage` validates a client-supplied `OUTPUTCRS`/`SUBSETTINGCRS`/`BBOXCRS` against that same bounded set: a malformed identifier returns `InvalidParameterValue`, and a well-formed but non-transformable value returns `OutputCrs-NotSupported` or `SubsettingCrs-NotSupported` (both HTTP 400) instead of failing downstream in the reprojection path. The coverage native CRS is always accepted.

`SUBSET` axis labels resolve in three tiers: the spatial axes (`x`/`E`/`Long`/`Lon` and `y`/`N`/`Lat`) trim the grid; `phenomenonTime` slices against the coverage acquisition time; and any further named axis is treated as an additional dimension subset. When the layer has a readable registered multidimensional (Zarr) store, a single coordinate selection on a declared additional axis is served through the shared bounded slice reader as a native-CRS, nearest-neighbor grayscale PNG. Out-of-range coordinates return `InvalidSubsetting`; an undeclared axis returns `InvalidAxisLabel`; and malformed values return `InvalidSubsetting`. Multi-coordinate trims, TIFF/JPEG output, reprojection, and advanced interpolation are rejected explicitly rather than falling back to the dimension-collapsed primary raster.

Two intentional divergences apply to the Zarr slice path (they are covered by dedicated adapter tests):

- **Spatial trims are clamped, not required to be contained.** An over-extent spatial trim is clamped to the intersection with the coverage extent before the read — matching the plain `IRasterStore` GetCoverage path (and CITE) — so a client that echoes the DescribeCoverage-advertised extent round-trips even when float rounding places it a hair outside the Zarr metadata extent. Only an empty intersection yields `InvalidSubsetting`, mirroring temporal subsetting.
- **`RANGESUBSET` band selection is rejected, not applied.** The slice path renders a single-band grayscale PNG from one range field, so a `RANGESUBSET` selection returns `OperationNotSupported` (locator `RANGESUBSET`) rather than silently returning the primary-variable render. Full multi-band Zarr composition is a non-goal.

When the coverage's native grid exceeds the per-axis pixel limit and no scaling operator is supplied, the oversize `InvalidParameterValue` reports locator `COVERAGEID` (the coverage must be down-scaled); when a scaling operator produced the oversize, it reports `SCALESIZE`.

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
