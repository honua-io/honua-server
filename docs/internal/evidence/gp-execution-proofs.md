# Catalog execution proofs: kriging, classification and conversion

The 2026.1 whole-catalog GP GA promise requires production execution and semantic
output assertions. Rows are promoted in `certification/gp-operation-matrix.v1.json`
only after the corresponding real execution suite passes.

## Native raster checkpoint

On 2026-09-06, native Windows .NET 10.0.100 Release builds used warnings as errors
and `-maxcpucount:4`. The changed worker and managed GP production assemblies were
rebuilt before the affected tests, reusing unchanged dependency outputs.
All **26 RasterExecutionProof tests passed**, zero failures/skips, in **1m15s**
(`proofs-gp-results/gp-raster-proofs-2.trx`). The required GDAL CI lane runs this
category with the pinned production GDAL image; absent Docker/native tools fail.

- `raster.interpolate-kriging` computes ordinary kriging in the native worker with
  a frozen linear, isotropic, zero-nugget variogram. For committed samples
  `(0,0,0)` and `(4,4,8)`, the independent two-point solution gives
  `prediction = 4 * (1 + (distanceToFirst - distanceToSecond) / sqrt(32))`.
  Every cell of the 4×4 output is asserted. Four withheld diagonal locations have
  independent truth `z=x+y`; residuals and RMSE must be below `1e-10`. The proof
  also asserts EPSG:4326, exact geotransform, Float64, NaN nodata, validity mask
  and model metadata. A shipped GDAL Python encoder preserves the VRT driver ban.
- `imagery.classify` invokes the configured production HTTP adapter and executor
  against the separately running nearest-centroid model backend. Two raster bands
  have the relation `band2=10*band1`; centroids at 2, 8 and 14 give independent
  decision boundaries 5 and 11, with earlier-class tie handling. All 16 output
  classes and the 3×3 confusion matrix are asserted, plus byte type, nodata 255,
  mask, CRS/grid and immutable model identity/hash. This exposed and fixed the
  rejection of unchanged small grids; shifted and overly coarse resampled grids
  remain rejected by dedicated regression assertions.
- `conversion.rasterize` runs real `gdal_rasterize` in fixed-burn and attribute
  modes over a committed L and upper-right rectangle. Boundary pixels are tested
  with the rectangle edge at x=2.25 and x=2.75, on opposite sides of their x=2.5
  centers. Every inside/outside/boundary value, nodata mask, grid, CRS and data type
  is asserted. [GDAL leaves exact center-on-edge ties unspecified](https://gdal.org/en/stable/programs/gdal_rasterize.html);
  both documented center-rule boundary cases are covered with frozen expectations.

## Hosted managed checkpoint

After integrating trunk, the full native Release build and canonical catalog
emitter passed (1 test, 18s). The full architecture suite passed **287 tests**,
zero failures/skips, in **2m26s** (`gp-architecture-final.trx`). The full solution
formatter passed. A trunk cache-test analyzer finding was corrected by using its
concrete inferred type; every cache assertion was retained.

The final integrated managed selection passed **402 tests**, zero failures/skips,
in **39s** (`gp-managed-final-2.trx`): catalog/plan conformance, real geometry-format
execution, imagery/georeferencing guards and cache replica assertions. Its manually
constructed dispatcher fixture now includes the actual geometry-format executor.

The native Windows Release hosted suite passed **174 tests**, zero failures/skips,
in **55s** (`gp-managed-proofs-1.trx`), including the real geometry-format job and
the imagery inference/georeferencing regression guards. The calculate-field
PostGIS/HTTP proof passed **1 test**, zero failures/skips, in **36s**
(`gp-calculate-proofs-1.trx`). Both receipts are in `proofs-gp-results`.

- `conversion.geometry-format` executes all four targets against a polygon with
  an independently specified 10-by-8 shell and 4-by-3 hole: area 68, exact rings,
  coordinates, topology, format metadata and SRID behavior are asserted.
- `data-management.calculate-field` reads back filtered integer and floating-point
  arithmetic through HTTP and SQL: 23/35 and 0.875/1.25, exact JSON number types,
  untouched excluded features, and no writes when a later expression is invalid.

This local checkpoint does not replace exact-candidate qualification: shared runtime/image
binding consumes #3848, database/restart evidence consumes #3855 where applicable,
and heavier-operation canary coordination consumes #3857. Those shared obligations
remain in the matrix; no lifecycle waiver or candidate receipt is claimed here.
