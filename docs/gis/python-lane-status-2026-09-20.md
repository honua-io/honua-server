# Python client certification status, September 20, 2026

Six previously excluded SDK cells now have native evidence: ArcPy STAC
catalog-landing, collections and item-search; PyQGIS WCS 2.0.1 GetCapabilities,
DescribeCoverage and GetCoverage. The checklist retains all 85 original disputed
exclusions, with six resolved reviews preserved in `previous_review`.

The aggregate now has **159 passes, 4 failures, 79 blocked, 52 not started and
82 exclusions** across 376 cells: **241 closed, 135 open**. The denominator is
unchanged. All Pro UI and QGIS UI states are unchanged; no GUI was operated.
These totals combine historical receipts and the new SDK runs, rather than
claiming a complete fresh certification of one server image.

## Fresh work

| Surface | Observed outcome | Remaining limitation |
| --- | --- | --- |
| PyQGIS WCS 1.0/2.0 | Stock provider discovery/description, SQL-verified corner pixels, exact full/subset TIFF affine grids, and project reload pass | Current evidence is SDK-only and Development/JIT |
| ArcPy WCS 1.0/2.0 | Both native MakeWCSLayer calls still fail with ERROR 999999 | Failure occurs before pixel or layer-file validation; remains open |
| ArcPy STAC | Native GetSTACInfo validates catalog, collection extent, and filtered item search | RasterCollection cannot read this fixture's GeoJSON assets; asset-download remains open |
| PyQGIS STAC | Native controller metadata/search and advertised GeoJSON asset through stock OGR succeed | Refreshes existing SDK coverage; no UI credit |
| ArcPy GPServer/SOAP | Native remote area job returns 12 with correct CRS/result metadata and completed status | Toolbox catalog omits GetToolInfo/GetTravelModes; task discovery remains incomplete |
| ArcPy branch versioning | Clean URL and token-URL workspace probes retained | Native workspace authentication/validation prevents CreateVersion and dependent reconciliation |
| PyQGIS OGC API Tiles | Stock OGR with explicit projected bounds and zoom 10 returns the expected feature within tile quantization tolerance, including reload | Default vector bounds handling remains an upstream GDAL issue; configured success does not close the default workflow |

## Raster repair and fixture

[PR 5038](https://github.com/honua-io/honua-server/pull/5038), on the existing
`feat/wcs-10-serving` branch, repairs two independent spatial errors:
`ST_Transform` could alter non-square pixels even for an unchanged CRS, and
`ST_Resize` could alter their geographic extent even at unchanged dimensions.
Single exports, mosaics and map rendering now skip redundant projection and
resize in pixel coordinates before restoring the geographic grid. Rotated basis
vectors and nearest-neighbor samples are preserved.

All 43 targeted Postgres integration tests pass, including decoded TIFF bounds
and every output pixel of rotated pattern grids. The fixed image is
`sha256:0ad6f6c9d81ead9772cf0f0a2e282321811bc5d059c53dc78570a4f6f07f7780`,
from source `25fa17d9cfa72340c9de4a33a743f19ff0911800`. The failed partial-fix
image and earlier receipts remain preserved.

PostGIS and postgis_raster 3.4.3 are installed. Experimental capability flags and
the Development Enterprise grant were already enabled; they did not fix these
spatial errors. The local database needed its already-journaled raster column
storage migration reapplied. Missing raster support or Basic licensing was not
established as the cause of the remaining ArcPy WCS/versioning failures.

## SDK status across protocols

Each value is **pass / fail / blocked / not-started / excluded**. Exclusions
remain visible in the denominator and are not automatically endorsed by this
follow-up. These are the same aggregate cells as the authoritative checklist.

| Protocol/version | Operations | ArcPy | PyQGIS |
| --- | ---: | --- | --- |
| wms 1.3.0 | 6 | 2/0/3/0/1 | 6/0/0/0/0 |
| wmts 1.0.0 | 4 | 0/0/4/0/0 | 4/0/0/0/0 |
| wfs 2.0.0 | 8 | 3/0/0/0/5 | 6/0/0/0/2 |
| wcs 1.0.0 | 3 | 0/0/3/0/0 | 3/0/0/0/0 |
| wcs 2.0.1 | 3 | 0/0/3/0/0 | 3/0/0/0/0 |
| ogc-api-features 1.0 | 8 | 0/0/8/0/0 | 8/0/0/0/0 |
| ogc-api-tiles 1.0 | 2 | 0/0/2/0/0 | 0/0/2/0/0 |
| stac 1.0.0 | 4 | 3/0/1/0/0 | 4/0/0/0/0 |
| sensorthings 1.1 | 3 | 0/0/3/0/0 | 3/0/0/0/0 |
| featureserver GeoServices REST | 10 | 7/0/0/0/3 | 6/0/0/0/4 |
| mapserver GeoServices REST | 4 | 2/0/0/0/2 | 4/0/0/0/0 |
| imageserver GeoServices REST | 3 | 3/0/0/0/0 | 0/0/3/0/0 |
| vectortileserver GeoServices REST | 3 | 3/0/0/0/0 | 3/0/0/0/0 |
| gpserver GeoServices REST | 6 | 6/0/0/0/0 | 0/0/0/0/6 |
| geocodeserver GeoServices REST | 4 | 2/0/0/0/2 | 0/0/0/0/4 |
| geometryserver GeoServices REST | 4 | 0/0/0/0/4 | 0/0/0/0/4 |
| naserver GeoServices REST | 2 | 0/0/0/0/2 | 0/0/2/0/0 |
| versionmanagementserver GeoServices REST | 2 | 0/2/0/0/0 | 0/0/0/0/2 |
| geoservices-soap GeoServices SOAP | 1 | 0/0/1/0/0 | 0/0/0/0/1 |
| odata v4 | 2 | 0/0/2/0/0 | 0/0/2/0/0 |
| ogc-api-maps 1.0 | 1 | 0/0/1/0/0 | 0/0/1/0/0 |
| ogc-api-coverages 1.0 | 1 | 0/0/1/0/0 | 0/0/1/0/0 |
| ogc-api-records 1.0 | 1 | 0/0/1/0/0 | 0/0/1/0/0 |
| ogc-api-processes 1.0 | 1 | 0/0/1/0/0 | 0/0/1/0/0 |
| ogc-api-styles 1.0 | 1 | 0/0/1/0/0 | 1/0/0/0/0 |
| ogc-api-edr 1.0 | 1 | 0/0/1/0/0 | 0/0/1/0/0 |
| pmtiles 3 | 1 | 0/0/1/0/0 | 1/0/0/0/0 |
| tilejson 3.0.0 | 1 | 0/0/1/0/0 | 0/0/0/0/1 |
| cog GeoTIFF | 1 | 1/0/0/0/0 | 1/0/0/0/0 |
| i3s-sceneserver 1.x | 1 | 1/0/0/0/0 | 0/0/1/0/0 |
| 3d-tiles 1.0 | 1 | 0/0/1/0/0 | 1/0/0/0/0 |
| elevation Esri | 1 | 1/0/0/0/0 | 0/0/0/0/1 |

## Receipts

- `honua-client-compat/evidence/pyqgis-sdk-roundtrip-20260920-i/observations.json`: fixed WCS replay, native caches, project reload and separate raw TIFF controls.
- `honua-client-compat/docs/reports/pyqgis-wcs-fixed-grid-2026-09-20.md`: final successful replay (commit 2452daa); the linked earlier report retains failed grid investigations and SQL controls.
- `honua-esri-compat/evidence/arcpy-stac-metadata-20260920-d/observations.json`: verified native metadata operations on source 6ac9debbccdd / image 737851273827.
- `honua-esri-compat/evidence/arcpy-wcs-pixelgrid-fix-20260920/wcs/observations.json`: remaining ArcPy WCS failures on the fixed image.
- `honua-esri-compat/docs/arcpy-sdk-status-20260920.md`: GP, workspace authentication and remaining ArcPy work.

ArcPy's reported 3.7.1 build 1901 is preserved alongside the same installation's
independently hashed ArcGISPro.exe file/product version 3.7.1.1904. PyQGIS reports
3.44.14-Solothurn and GDAL 3.13.3. No native UI or shipping/AOT certificates are
issued from these runs. Shared compatibility gate snapshots were unchanged.
