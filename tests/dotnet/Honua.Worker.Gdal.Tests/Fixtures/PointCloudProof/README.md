# Point-cloud execution correctness fixtures

`generate.py` writes real LAS 1.4 / point-format-3 input bytes with Python `struct`.
GDAL's OSR only supplies the source WKT metadata; PDAL does not generate the input
or the expected result. Both files contain three deliberately distinct points,
file source ID 7, XYZ, intensity, return number/count, classification, scan angle,
user data, point source ID, GPS time and RGB. Geographic XY scales are 1e-7 degrees;
projected XY and all Z scales are 0.001 metres. Source values are explicit literals
in the generator and independently declared in `PdalExecutionProofTests`.

The production `PdalPointCloudConvertJobExecutor` and `DockerGdalCommandRunner`
execute the real PDAL built by `docker/worker-gdal/Dockerfile`'s `native-tools`
stage. Supply that build's immutable Docker image ID in `HONUA_PDAL_PROOF_IMAGE`.
Missing image/dependency fails rather than skips. Required PR Gate and full CI
build the production stage and retain the execution TRX.

The test decodes LAS headers, VLRs and every point record directly with C# binary
reads, independently of Honua's managed LAS reader and PDAL's JSON output. It
asserts uncompressed layout, point count/format, source metadata, scale, bounds,
all XYZ and attributes. OSR interprets the published WKT VLR solely to assert its
EPSG identity. Cases cover an explicit geographic source, omitted source CRS
(projected pass-through), and projected-source reprojection to EPSG:4979. The
last computes inverse spherical Mercator with R=6378137 from the declared source
coordinates; tolerance is half the output scale plus floating-point roundoff.

[PDAL's documented forwarding contract](https://pdal.io/en/stable/tutorial/las.html)
explains why preserving source scale/header and selecting geographic output scale
are necessary. Dropping RGB, rounding coordinates to the default 0.01 scale,
copying projected coordinates under a geographic CRS, or changing an elevation
cannot satisfy these assertions.

These are pre-cut whole-catalog GA operation proofs for #3951. Candidate-bound
lifecycle qualification consumes #3848; shared staged-output/database recovery
qualification remains #3852/#3855 where those substrates are used. This fixture
uses inline artifacts and makes no restart, retention or staging claim.

For local Linux hosts whose user differs from the container default 1001:1001,
set `HONUA_GDAL_PROOF_USER` to the host UID:GID before running the test. This only
maps ownership at the test's bind-mount boundary; the production executor and
container hardening remain in use. CI records its own host UID:GID likewise.
