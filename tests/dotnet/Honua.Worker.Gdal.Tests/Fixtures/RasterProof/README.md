# Raster execution correctness fixtures

`RasterExecutionProofTests` runs production catalog executors with the production
`DockerGdalCommandRunner`, its hardening policy, and the digest-pinned GDAL 3.13.1
base from `docker/worker-gdal/Dockerfile`. The test transport maps host scratch
paths into Docker so the same tests run with Windows dotnet and Docker Desktop.
Missing Docker, missing GDAL, failed execution, and malformed output fail tests.
The required `PR Gate` runs the suite and uploads its TRX receipt.

The committed GeoTIFFs are inputs, not golden outputs. `generate.py` documents
every source pixel, band, CRS, nodata value, and affine transform. Rebuild using
the pinned GDAL image with this directory mounted at `/proof` and entrypoint
`python3 /proof/generate.py`. Expected results are computed independently in C#;
`decode.py` only decodes the published output through GDAL's Python reader.

| Issue / operation | Independent oracle |
| --- | --- |
| #3923 clip | L polygon selects source column 1 and row 2; explicit inside, boundary-adjacent, outside, and source-nodata cells in both bands. A source without nodata separately proves the internal TIFF mask excludes exterior zero fill while preserving an interior valid zero. |
| #3924 zonal statistics | Disjoint sets {10,20,50}, {30,40,70,80}, overlapping set {20,30,70}, and an all-nodata zone; count/min/max/sum/mean and original band 2. A 513-column fixture separately verifies merging counts, sums, and population variance across read windows. |
| #3925 spectral index | Red/NIR/blue inputs from the committed multiband reflectance TIFF; NDVI and EVI equations, Float32 tolerance, undefined denominator and source nodata. Explicit output nodata and a source without nodata verify sentinel override and NaN fallback. |
| #3926 reproject | Spherical Mercator forward bounds with R=6378137, transformed diagonal cell size, inverse-mapped output cell centers, nearest/bilinear weights and nodata in both bands. |
| #3927 mosaic | Two 3x2 two-band tiles overlap one column; explicit arrays for first/last source precedence, nodata fallback and a remaining hole. |
| #3928 resample | Half-sized cells use independently calculated separable linear weights or nearest selection; nodata central samples stay masked and other weights renormalize. |
| #3929 IDW | Five known point values, reciprocal squared-distance weighting, exact coincident center (including valid zero), and NaN nodata for empty radius searches. |
| #3930 histogram | Counts 3,2,5,1 for values 0,1,2,3; all other buckets zero, including excluded nodata 255; valid count 11. |

Every raster output checks CRS, all six affine ordinates, dimensions, band count,
pixel type, nodata metadata, GDAL mask-band values, and decoded pixel values. The
no-nodata zonal fixture counts the five selected cells (including valid zero),
excludes the cutline exterior, and independently sums their values to 39.
Fixtures and assertions
distinguish well-formed but wrong results such as copied inputs, swapped bands,
wrong resamplers, reversed mosaic precedence, nodata counted as data, and global
statistics substituted for zone selection.

IDW's unbounded default uses GDAL's documented reduced-precision SSE/AVX path,
even with Float64 output. The interior tolerance is eight Float32 rounding units
at the fixture maximum magnitude (100), about 0.0000954; bounded searches retain
1e-9 and coincident source values require exact equality. See
[GDALGridCreate](https://gdal.org/en/stable/api/gdal_alg.html).

## Qualification boundary

This is pre-cut operation correctness evidence for the whole-catalog GP GA
promise. It does not claim an exact-candidate lifecycle receipt. Candidate-bound
qualification consumes #3848 once server and worker candidate digests exist.
The shared matrix keeps that dependency. #3855 covers transactional
database/restart proof; these fixtures use inline inputs and inline output
artifacts and do not cross a Postgres data or transaction boundary, so that cell
is not applicable here. No database-restart success is inferred from this suite.


## Conversion proofs (#3941?#3943)

`generate-conversion.py` creates inputs, never expected outputs. The 3?2 RGB
GeoTIFF has red [10,20,30;40,50,255], green red+1 and blue red+2 except the
shared 255 nodata pixel. EPSG:4326 transform is [10,.5,0,20,0,-.5]. PNG must
preserve all channels and transparency; GTiff/COG must also preserve the CRS
and transform. COG is checked by its layout metadata and GDAL's structural
COG validator. PNG cannot embed a GIS CRS; the single published PNG does not
include GDAL's temporary PAM georeferencing sidecar.

The classified grid is [1,1,0;1,0,2;0,2,0], zero nodata, with unit cells at
[10,1,0,20,0,-1]. Four-connectivity has three regions of areas 3,1,1;
eight-connectivity has two of areas 3,2. The latter class-2 region must be a
valid corner-touching MultiPolygon. Expected WKT comes directly from cell
edges. The initial execution failed the validity assertion for eight-connectivity;
normalizing with ogr2ogr -makevalid preserves class, region count and occupied
cells without publishing the invalid intermediate.

`conversion.raster-reproject` uses the existing committed two-band grid.tif
and the analytical spherical-Mercator/inverse-sampling oracle for both nearest
and bilinear resampling. Every cell, nodata/mask, band type, CRS and affine
grid is checked. These cases run unconditionally in the required PR Gate's
`Category=RasterExecutionProof` step against the production GDAL digest.

Release promise: the entire built-in GP catalog is GA in 2026.1, including
conversion output correctness. Exact-candidate execution/storage qualification
consumes #3848 and native-worker canary coordination consumes #3857 after the
candidate cut; local/PR head proofs do not claim to qualify an uncut digest.

Windows focused verification: 14 passed, 0 failed, 0 skipped after the topology fix.
Initial run: 8 passed, 1 failed specifically on invalid eight-connected topology.

## Generic warp and OGR source proofs

`Reproject_GeographicToMercator_MatchesAnalyticalGridAndInverseMappedSamples`
also executes the distinct `gdal.gdalwarp` production executor with its `targetSrs`
contract and GDAL's default nearest-neighbor resampling. Both input bands, the
nodata hole, target EPSG:3857, affine grid and every decoded pixel use the same
independent Mercator oracle described above. `WarpOracle_ChangedPixelInValidGeoTiff_IsRejected`
changes one output pixel through GDAL while keeping a valid GeoTIFF and demonstrates
that the value assertion rejects it. This is the heavier-operation correctness
fixture for #3947 / #3857; the required PR Gate retains the execution TRX. The
seven real scheduled intervals and exact-candidate identity remain #3857's bill.

`Ogr_MultilayerGeoPackage_SelectsLayerAndPreservesGeometryAndAttributes` drives
`source.ogr` through real OGR with `survey.gpkg`, generated from the explicit
source literals in `generate-ogr.py`. It selects the `survey` layer rather than
the first, `decoy`, layer; checks four features, Point/LineString/Polygon/null
geometry, every XYZ ordinate, WGS84 longitude/latitude CRS, Unicode names,
nulls and numeric attributes. A second execution selects the valid decoy layer
and proves the same semantic oracle rejects its well-formed output. Missing
layers and option-shaped layer names must fail without an artifact. The optional
catalog `layerName` input selects one exact layer; omission retains OGR's default
behavior. Shared plan validation rejects malformed supplied names before dispatch,
and the worker retains the same boundary check. Planning tests cover optional
values, Unicode names, control characters, option-shaped names and the 1024-character
length boundary. These inline artifact proofs do not cross a staged-output or database
boundary; #3852/#3855 are not inferred to pass from them.

For local Linux hosts whose user differs from the container default 1001:1001,
set `HONUA_GDAL_PROOF_USER` to the host UID:GID before running the test. This only
maps ownership at the test's bind-mount boundary; the production executor and
container hardening remain in use.
