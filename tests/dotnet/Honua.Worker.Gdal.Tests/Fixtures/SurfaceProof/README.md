# Surface execution correctness fixtures

`SurfaceExecutionProofTests.cs` extends the `RasterExecutionProofTests` partial
class and shares its production executors, `DockerGdalCommandRunner`, immutable
GDAL base-image pin from `docker/worker-gdal/Dockerfile`, and Python TIFF decoder.
The required PR Gate selects `Category=RasterExecutionProof`, including all 24
surface cases (21 value proofs plus the three #3913 discrimination oracles). These are ordinary facts/theories: missing Docker, GDAL, fixtures,
or output fails; no native-tool availability skip is used. Windows dotnet drives
Docker Desktop locally; no Linux build host is needed.

Run the required selection from the repository root:

```text
dotnet test tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj --configuration Release -maxcpucount:4 --filter Category=RasterExecutionProof
```

The Windows regression run before the viewshed fix passed 18 of 21 surface
cases; all three viewshed cases failed on the absent nodata declaration. After
the fix, all 43 combined surface/raster proof and viewshed unit cases passed,
with zero skips. Required PR CI retains the head's TRX as its execution receipt.

`generate.py` deterministically creates the committed **input** GeoTIFFs. It
never computes expected outputs. Run it with the pinned image, mounting this
directory at `/proof`, using entrypoint `python3 /proof/generate.py`.
All inputs are Float32, EPSG:3857, origin (1000,2000), north-up, with 2m pixels
except the 1m viewshed DEM. The decoder only reads output; independent C# formulas
and geometric constraints supply the expected results.

| Issue / operation | Independent oracle |
| --- | --- |
| #3922 slope | A 5x5 plane rises 2m east and 3m south per 2m cell. With catalog unit ratio 2, gradient magnitude is sqrt(1 + 1.5^2)/2. Degree and percent results are atan(gradient)*180/pi and 100*gradient. |
| #3915 aspect | Downslope east/north vector (-2,3) gives clockwise-from-north atan2(-2,3); separate east-rising and north-rising planes give 270 and 180 degrees. Flat pixels are undefined/nodata. |
| #3913 TPI | 5x5 background 2, center 12, depression -4 at (1,1): center minus eight-neighbor mean. Center TPI is 10.75; depression is -7.25. |
| #3913 TPI discrimination | Three `TpiOracle_*_IsRejected` cases prove the assertions above are semantic, not structural. Each substitute is produced by real execution and keeps every grid, CRS, type, sentinel and mask property. |
| #3920 TRI | Same fixture, Riley TRI = sqrt(sum of eight squared center-neighbor differences). Center is sqrt(956); depression is sqrt(508). This distinguishes the Wilson mean-absolute-difference algorithm. |
| #3921 roughness | Same fixture, maximum minus minimum over all nine cells, including the center: center/depression range 16, other neighborhoods 10 or 16. |
| #3914 hillshade | Ridge rises 2 per 2m cell on the west and falls on the east. Scale 2, vertical exaggeration 1, altitude 20, azimuth 90 and 270. Unit surface normal dotted with unit sun direction gives intensity round(1 + 254*max(0,dot)). Explicit lit, shadow=1, and edge nodata=0 pixels. |
| #3916 viewshed | A 7x7 DEM has a 10m ridge in column 3, observer at row 3/column 1. Low observer sees through the crest; cells beyond are hidden. Separate 50m observer/target cases clear the crest. Radius 5m classifies out-of-range cells independently by squared cell distance >25. |
| #3918 contour | Ramp z=10*c at x=1001+2*c, interval 10, base 5: exactly four lines at levels 5,15,25,35 with x=1001+level/5, y extent [1990,2000], length 10. Check every x/y ordinate, monotonic vertex order, simple valid nonempty LineString topology and EPSG:3857. |

Every raster checks all cells, dimensions, CRS, six affine ordinates, band count,
pixel type, nodata metadata and mask. Float32 analytical values use 1e-5 absolute
tolerance (rounding at these small fixture magnitudes); integer classes and
intensities are exact. Contour interpolation tolerance is 1e-5m. Border cells are
nodata for neighborhood operations. Separate source-corner nodata fixtures prove
that the touching interior 3x3 neighborhood is also nodata for slope, aspect,
TPI, TRI and roughness. Aspect flat cells are all nodata.

Viewshed uses a complete DEM. Its classes are visible 255, hidden 0, and
out-of-range nodata 127. Curvature over this <=5m fixture is below 2 micrometers,
far below visibility margins. Source DEM holes are not inferred to be handled:
[GDAL documents that input nodata is not specially processed](https://gdal.org/en/stable/programs/gdal_viewshed.html).
The output nodata assertion covers the bounded computation domain. Formula and
edge conventions follow [gdaldem](https://gdal.org/en/stable/programs/gdaldem.html)
and [gdal_contour](https://gdal.org/en/stable/programs/gdal_contour.html).

## TPI discrimination oracles (#3913)

An execution proof is only evidence if it would reject a wrong result, so
`surface.rugosity-tpi` carries three rejection cases alongside its value proof.
None synthesises or corrupts a raster by hand; each runs the real toolchain and
then shows that the *only* assertion separating it from a correct run is the
signed per-cell oracle.

| Substitute | Why it is plausible and well formed | What rejects it |
| --- | --- | --- |
| A sibling gdaldem neighborhood statistic (TRI) over the same DEM | The operation's prior coverage asserted the gdaldem subcommand string against a fake runner, so a dispatch defect routing `rugosity-tpi` to a sibling was exactly the defect it could not see. TRI shares the dimensions, CRS, affine transform, band count, Float32 type, -9999 sentinel and border mask. | The signed TPI value oracle, at the first interior cell. |
| An unsigned magnitude index | TPI's sign *is* its meaning: positive is a peak, negative a depression. The magnitude index keeps every grid property and leaves the +10.75 peak cell byte-identical to a correct result. | The depression cell, whose -7.25 becomes +7.25. |
| A source hole consumed as an elevation | Dropping the source nodata declaration makes real execution average the -9999 sentinel in, filling the hole and its neighbours with finite, plausible numbers. This is the defect class the family already produced once, in #3916's viewshed out-of-range domain. | The nodata mask and the hole cell, which reads 1242.875 instead of nodata. |

## Qualification boundary

Release promise: every BuiltInProcessCatalog operation is GA in 2026.1, with
concrete execution and semantic correctness evidence. These are pre-cut
operation proofs, not server/worker candidate lifecycle or canary receipts.
The matrix retains its candidate-binding dependency on #3848, which is now
closed with its qualification lanes executable and receipt-complete; what is
still missing is the cut candidate those lanes must consume. Exact-candidate
qualification is released from these eight issues to that lane because the
release decision record says the candidate digest has not been cut. Once it
exists, qualification must consume #3848's same-source server/worker identities.
Heavier native surface canaries can reuse this fixture version and these oracles
under #3857; this PR does not claim seven scheduled candidate-bound intervals.
No candidate identity or lifecycle success is inferred from a local/PR TRX.

## Public API execution proof (#4400)

`OgcProcessesExecutionProofTests` retains and strengthens the three execution
cases moved from `OgcProcessesEndpointsTests`: direct slope submission, direct
buffer submission, and explicit value output selection. Each requires real
Redis and the production job loop, requires HTTP 201, polls to `successful`,
and asserts the HTTP result document. It runs in the existing required
`Category=RasterExecutionProof` selection. Missing dependencies fail the proof.

Slope uses `plane-hole.tif`, the native production dispatcher/executor and the
same pinned GDAL transport as the executor proofs. The HTTP artifact is decoded
with `RasterProof/decode.py`; the independent rise/run formula checks all 25
cells, including border/hole nodata and masks, EPSG:3857 and every affine
ordinate. Passing through the input GeoTIFF fails the same oracle. Buffer uses
literal POINT(100 200), SRID 3857 and radius 25.5; the decoded polygon must have
that center, envelope and every vertex radius, valid topology and area within
1% of the analytical disk. Explicit value selection must return inline GeoJSON.

These tests supply public API execution evidence for the whole-catalog GP GA
promise. The opaque staged-payload, zonal parser and imagery pass-through tests
remain transport/parser proofs; they are not substituted for these executions.
