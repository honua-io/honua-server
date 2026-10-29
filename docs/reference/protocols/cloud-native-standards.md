---
type: reference
title: "Cloud-native geospatial standards and evidence"
description: "Specification targets, supported subsets, conformance evidence and known limitations for Honua Server's cloud-native formats."
---
# Cloud-native geospatial standards and evidence

Reviewed 2026-10-27 against Server source
`89a966cd342e88c8fd5ab157e564d14c3d9a68e2`.
Tracked by [#6704](https://github.com/honua-io/honua-server/issues/6704).

The [Cloud-Native Geospatial guide](https://guide.cloudnativegeo.org/) describes
formats and access practices; it is not a certification scheme. Honua targets
specific format versions and supported subsets. Read/write interoperability,
efficient cloud access, protocol behavior and release qualification each need
their own evidence. An unsupported optional format is not a failed claim of
support, and a successful library test is not a deployment certificate.

## Format targets and supported boundaries

| Format and specification | Implemented Server scope | Qualification boundary / remaining work |
|---|---|---|
| [COG 1.0, OGC 21-026](https://docs.ogc.org/is/21-026/21-026.html), based on GeoTIFF/TIFF | Managed TIFF/BigTIFF metadata and tile reads; registered cloud objects use the documented aligned EPSG:3857 tile path. Separate `CogArtifactService` publishes raster-store COG exports. | Real PostGIS publication/storage/GET/HEAD/range and an independently validated empty-extent COG with overview and pixel readback landed in [#6741](https://github.com/honua-io/honua-server/pull/6741). Deployed HTTPS and cloud transfer efficiency remain unqualified. |
| [Zarr v2](https://zarr-specs.readthedocs.io/en/latest/v2/v2.0.html) and [v3 core](https://zarr-specs.readthedocs.io/en/latest/v3/core/index.html) | Bounded numeric subsets; C-order little-endian v2 with supported null/zlib/gzip compression; v3 regular chunks, default/v2 chunk keys and a limited bytes/gzip pipeline. | Supported pipelines now validate required metadata, codec declarations, decoded chunk integrity and typed fill values. Full v3 codec composition, sharding, extensions and general group discovery remain outside the supported subset. |
| [GeoZarr conventions](https://geozarr.org/) / [OGC technical paper 26-050](https://docs.ogc.org/techpaper/26-050/26-050.html) | Existing custom Zarr CRS/extent metadata and CF axes are not a GeoZarr implementation. | Draft/convention work; no GeoZarr compliance claim. Any future claim must name the convention revision and demonstrated CRS, spatial and multiscale facets. |
| [Cloud-optimized HDF5/NetCDF4 guide](https://guide.cloudnativegeo.org/cloud-optimized-netcdf4-hdf5/index.html) | Optional GDAL worker extracts multidimensional metadata and converts registered sources to derived Zarr using `gdalmdimtranslate`. | A layout/access practice, not one standalone certification standard. Conversion may stage a whole source. No native in-process HDF5/NetCDF bounded-read claim; default fixture-only cells remain unqualified. |
| [COPC 1.0](https://copc.io/) / LAS 1.4 and LAZ | Worker-assisted LAZ/COPC-to-LAS conversion followed by managed point-cloud processing. Managed reader supports uncompressed LAS 1.1–1.4 record formats 0, 1, 2, 3, 6, 7, 8. | No native COPC hierarchy/range reader or COPC writer qualification. Existing work: [#2442](https://github.com/honua-io/honua-server/issues/2442), [#3290](https://github.com/honua-io/honua-server/issues/3290). |
| [GeoParquet 1.1.0](https://geoparquet.org/releases/v1.1.0/) | Shared WKB producer with `geo` metadata, primary column, geometry types, CRS, covering bounds and same-CRS coordinate epoch; importer accepts one planar WKB geometry column, preserves nullable rows and representable nested attributes, and validates required metadata before writes. | Native GeoParquet geometry encodings, spherical edges and additional geometry columns are refused explicitly. Epoch-aware reprojection is unsupported. 2.0 prereleases are not the release target. |
| [FlatGeobuf binary specification](https://flatgeobuf.org/) (major 3) | PostGIS indexed `ST_AsFlatGeobuf` export through native/storage-mapped readers, and FlatGeobuf import. | PostGIS export capability does not apply to every provider. A query-generated response is not a static object range-serving claim. Independent full reads must decode features, not just inspect headers. |
| [PMTiles v3](https://github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md) | MVT archive writer with Hilbert ordering, directories and metadata; published archive GET/HEAD with bounded single-range access and HTTP validators bound to one storage revision. | No arbitrary raster PMTiles writer claim. Native archive verification is separate from HTTP/client, publication API and real-cloud evidence. |
| [GeoArrow 0.2](https://geoarrow.org/format.html) and [extension metadata](https://geoarrow.org/extension-types.html) (related) | Arrow IPC stream with `geoarrow.wkb` binary extension and CRS metadata; current formatter accepts EPSG:4326 and rejects M. | No native GeoArrow importer or dedicated external-client CNG qualification. Shares geometry normalization with GeoParquet. |
| [Kerchunk references](https://fsspec.github.io/kerchunk/) / virtual Zarr (related) | No exposed reference-store adapter identified. | Unsupported; upstream GDAL/fsspec features do not establish Server support. |
| [STAC asset model](https://github.com/radiantearth/stac-spec/blob/master/item-spec/item-spec.md) (related) | GeoJSON assets and resolved, currently published COG artifacts in catalog mapping. | Catalog validity does not validate asset bytes. General PMTiles/COPC/GeoParquet asset publication remains separate work ([#4718](https://github.com/honua-io/honua-server/issues/4718), [#4597](https://github.com/honua-io/honua-server/issues/4597)). |

3D Tiles 1.1 remains in the existing CNG lane. This audit does not add formats to
its frozen 24-row evidence denominator or alter release gates.

## What must be demonstrated

**Raster files and arrays.** A readable GeoTIFF is not necessarily a COG. Test
layout with independent tooling and compare decoded windows against known sample
values, including nodata, compression/predictor behavior and overview selection.
Measure actual requests and bytes; successful decoding alone does not establish
cloud efficiency. The direct COG tile path has explicit grid, sample-type and
compression limits in [the format reference](cloud-native-formats.md).

For Zarr, metadata shape, dtype, chunk grid/keys, fill values and codec order must
agree with decoded bytes. An unsupported codec sequence must fail before pixels
are returned; silently applying only one codec is data corruption. Validate
coordinate axes and the selected subset against an independent reader. Honua's
custom group variable manifest does not imply generic v3 consolidated-metadata or
GeoZarr discovery. Unsupported required extensions and malformed mandatory fields
remain follow-up validation work, not evidence of general v3 conformance.

The HDF5/NetCDF worker boundary is deliberate: native libraries remain outside the
AOT Server. Compare converted multidimensional data and coordinates to the source,
retain worker/source/output identities, and account for staging/conversion cost.
The runner-local derived-Zarr rehearsal retains `qualification=false`; it does not
prove a published-worker or frozen-candidate deployment.

**Vector files.** GeoParquet metadata must describe the actual WKB, including
nested geometry dimensions, empty versus null geometries, CRS axis order and
bounds. The importer accepts one planar XY/XYZ WKB geometry column, preserves
null-geometry rows and representable nested attributes, and refuses unsupported
semantics before writes. Explicit spherical edges and additional geometry columns
are rejected; neither is silently converted to planar single-geometry data.
Dynamic CRS epochs survive import, publication and same-CRS export; an epoch with
transformed output is rejected because epoch-aware reprojection is unsupported.
Optional spatial ordering is a performance practice, not a universal validity
requirement. GeoArrow needs extension/CRS metadata as well as valid Arrow IPC.

FlatGeobuf header/index readability does not prove valid feature buffers. Both
GDAL harness paths now use complete feature enumeration, buffer verification and
a decoded-count check. The small seeded fixture requires non-null geometries and
properties; this is a fixture oracle, not a ban on all valid nullable FlatGeobuf
files. Pyogrio, GeoPandas and JavaScript readers provide separate observations.

**Delivery and discovery.** Evaluate [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html)
conditions as well as 206/416 status, exact `Content-Range`, MIME type, HEAD,
byte budgets and authorization. A stale `If-Range` must select the full response
subject to the existing full-download cap; it cannot authorize oversized reads.
Validators must identify an actual object revision, including same-size rewrites.
Storage metadata and bytes must also remain consistent during concurrent writes;
conditional HTTP parsing alone does not solve mutable-object read races.

The configured CORS policy allows range/conditional headers and exposes response
validators/range metadata, but a source check cannot prove a deployed browser
origin policy. Demonstrate allowed and denied origins against the deployment used
for the claim. Likewise, STAC asset URLs must resolve to authorized, published
artifacts; an asset media type does not certify the artifact.

## Evidence map

Paths below are relative to the repository root. Test names identify existing
coverage to inspect or run; their presence is not a claim of execution on the
review baseline.

| Area | Primary implementation | Existing tests / independent evidence path |
|---|---|---|
| COG parsing, publication and serving | `src/Honua.Core/Features/Raster/CogParser/`; `src/Honua.Server/Features/Protocols/Cog/`; `src/Honua.Server/Features/Protocols/Rasters/CogArtifacts/` | `CogMetadataExtractorTests`, `CogEncodedTileTests`, `CogArtifactEndpointTests`; rasterio, rio-cogeo and GDAL CNG cells |
| Zarr subsets | `src/Honua.Core/Features/Raster/ZarrParser/` | `ZarrMetadataExtractorTests`, `ZarrSubsetReaderTests`, `CuratedZarrCorpusReaderTests`; independent xarray/formula/coordinate comparisons |
| Derived multidimensional coverage | `src/Honua.Worker.Gdal/Execution/GdalMultidimCoverageMetadataJobExecutor.cs` | `test_derived_zarr.py`, `test_derived_fsspec.py`; opt-in receipt-bound rehearsal |
| GeoParquet / GeoArrow | `src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs`; `src/Honua.Geometry/Features/FileImport/Services/GeoParquetReader.cs`; `src/Honua.Protocols.GeoServices/FeatureServer/Services/GeoArrowQueryFormatter.cs` | Formatter/import tests, `gpq validate`, PyArrow/GeoPandas/GDAL |
| FlatGeobuf | `src/Honua.Db/Postgres/Features/FeatureStore/Services/PostgresStorageMappedFeatureReader.EncodedFormats.cs`; `src/Honua.Geometry/Features/FileImport/Services/FlatGeobufFormatReader.cs` | External format corpus/import tests; GDAL, Pyogrio, GeoPandas and flatgeobuf-js |
| PMTiles | `src/Honua.Core/Features/Tiles/PMTiles/`; `src/Honua.Server/Features/Protocols/Tiles/PMTilesProxy/` | `PMTilesPublishEndpointTests`, go-pmtiles verify, Python/JavaScript clients and measured HTTP reads |
| Point clouds | `src/Honua.Scene/PointCloud/LasPointCloudReader.cs`; `src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs` | LAS reader/worker tests; no native COPC conformance cell |

[Current CNG status](../../cng-status.md) names the inspected run evidence and its
limits. A release claim needs a complete run bound to the exact Server SHA, image
digest, harness revision, fixture/client versions and candidate cut time. Preserve
failed and unrun observations. Green validator self-tests, third-party fixtures
and a partial archive-verification result cannot substitute for that receipt.

## Follow-up and release scope

The compliance goal remains open until the applicable requirements are implemented
and verified. Thursday's release is not gated on completing that entire program;
remaining compliance work continues afterward. The bounded Zarr, geometry-dimension,
generated-column, artifact HTTP, empty FlatGeobuf and conformance-harness child
repairs listed in the initial assessment have landed and their issues are closed.
The GeoParquet import repairs for edges, required metadata, row/attribute
preservation and coordinate epochs landed in [#6732](https://github.com/honua-io/honua-server/pull/6732);
independently authored input and endpoint-error evidence is completed in the
corresponding acceptance follow-up.

Broader capabilities remain separate work: native COPC range serving
([#2442](https://github.com/honua-io/honua-server/issues/2442),
[#3290](https://github.com/honua-io/honua-server/issues/3290)) and portable
catalog/static-asset publication ([#4718](https://github.com/honua-io/honua-server/issues/4718),
[#4597](https://github.com/honua-io/honua-server/issues/4597)) are still open.
Independent deployment and cloud qualification is required before those claims.

Run existing qualification through its normal owner-controlled workflow after
repairs land. Preserve candidate pins and the Thursday release scope. Evaluate
optional encodings, GeoZarr conventions, native COPC and Kerchunk against their
explicit specification targets; absence of an optional capability is distinct
from a correctness defect in an implemented capability. Until qualified evidence
exists, the accurate assurance is:
**Honua implements documented cloud-native format subsets and maintains explicit
conformance checks and known limitations.**
