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
| #3923 clip | L polygon selects source column 1 and row 2; explicit inside, boundary-adjacent, outside, and source-nodata cells in both bands. |
| #3924 zonal statistics | Disjoint sets {10,20,50}, {30,40,70,80}, overlapping set {20,30,70}, and an all-nodata zone; count/min/max/sum/mean and original band 2. |
| #3925 spectral index | Red/NIR/blue inputs from the committed multiband reflectance TIFF; NDVI and EVI equations, Float32 tolerance, undefined denominator and source nodata. |
| #3926 reproject | Spherical Mercator forward bounds with R=6378137, transformed diagonal cell size, inverse-mapped output cell centers, nearest/bilinear weights and nodata in both bands. |
| #3927 mosaic | Two 3x2 two-band tiles overlap one column; explicit arrays for first/last source precedence, nodata fallback and a remaining hole. |
| #3928 resample | Half-sized cells use independently calculated separable linear weights or nearest selection; nodata central samples stay masked and other weights renormalize. |
| #3929 IDW | Five known point values, reciprocal squared-distance weighting, exact coincident center (including valid zero), and NaN nodata for empty radius searches. |
| #3930 histogram | Counts 3,2,5,1 for values 0,1,2,3; all other buckets zero, including excluded nodata 255; valid count 11. |

Every raster output checks CRS, all six affine ordinates, dimensions, band count,
pixel type, nodata metadata, and decoded pixel values. Fixtures and assertions
distinguish well-formed but wrong results such as copied inputs, swapped bands,
wrong resamplers, reversed mosaic precedence, nodata counted as data, and global
statistics substituted for zone selection.

IDW's unbounded default uses GDAL's documented reduced-precision SSE/AVX path,
even with Float64 output. The interior tolerance is eight Float32 rounding units
at the fixture maximum magnitude (100), about 0.0000954; bounded searches retain
1e-9 and coincident source values require exact equality. See
[GDALGridCreate](https://gdal.org/en/stable/api/gdal_alg.html#_CPPv414GDALGridCreate17GDALGridAlgorithmPKv8GUInt32PKdPKdPKdddddd8GUInt328GUInt3212GDALDataTypePv16GDALProgressFuncPv).

## Qualification boundary

This is pre-cut operation correctness evidence for the whole-catalog GP GA
promise. It does not claim an exact-candidate lifecycle receipt. Candidate-bound
qualification consumes #3848 once server and worker candidate digests exist.
The shared matrix keeps that dependency. #3855 covers transactional
database/restart proof; these fixtures use inline inputs and inline output
artifacts and do not cross a Postgres data or transaction boundary, so that cell
is not applicable here. No database-restart success is inferred from this suite.
