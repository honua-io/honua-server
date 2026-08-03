# Raster semantic conformance

Tracking: RAST-016 / #3100

Honua routes a canonical raster process between PostGIS and the isolated native GDAL worker only
when the selected engine advertises executable evidence for the requested semantic variant. An
available executor alone is insufficient. `unverified` implementations are eliminated before
placement, so a newly added PostGIS executor cannot silently enter dynamic routing before parity
evidence exists.

## Pinned matrix

| Engine | Tested versions | Execution lane |
| --- | --- | --- |
| PostGIS Raster | 3.4, 3.5, 3.6 | `Honua.Postgres.Tests` in the PostgreSQL 16/17/18 compatibility matrix |
| GDAL | 3.12.4 | Patch-tag-pinned `ghcr.io/osgeo/gdal:ubuntu-full-3.12.4`, the `docker/worker-gdal/Dockerfile` base |

PostGIS integration evidence uses real raster SQL, not mocks. Native evidence invokes the same
`GdalSurfaceJobExecutor` and CLI runner as the worker, then decodes the resulting GeoTIFF into the
provider-neutral oracle. The ordinary lean test runner may skip CLI tests when GDAL is absent; the
pinned worker-image conformance lane must not.

## Canonical comparison

The checked-in manifest is embedded from
`tests/dotnet/Honua.TestKit/RasterSemantics/Fixtures/raster-semantic-fixtures.json`. Results are
decoded into grids, bands, NoData cells, and named scalars. Encoded TIFF/PNG bytes are never used as
the parity assertion.

The oracle compares these dimensions independently:

- affine grid, dimensions, and CRS;
- band count, pixel type, and color interpretation;
- exact NoData topology;
- decoded cell values;
- named statistics and histogram scalars;
- stable error and cancellation outcomes.

Pixel types, scalar names, CRS, and NoData topology are exact. Numeric tolerance applies only when
the fixture declares a finite non-negative absolute or relative tolerance. Broad image-snapshot
tolerance is prohibited. Diagnostics are capped at 100 entries to keep hostile or badly divergent
results bounded.

## Variant and divergence policy

Each `RasterProcessCapability` advertises its canonical semantic variants. Each engine advertises a
subset that has evidence. The statuses mean:

- `canonicalBaseline`: the pinned native implementation defines the initial golden result;
- `verified`: all advertised variants passed cross-engine fixtures;
- `restricted`: only the advertised subset passed, with every excluded behavior documented in
  `knownSemanticDivergences`;
- `unverified`: the planner must refuse the engine for dynamic routing.

Adding an executor therefore requires all of the following in one dependency-consistent change:

1. mark the implementation available;
2. add real provider-runner coverage for each claimed variant;
3. link stable fixture IDs in `semanticEvidenceFixtureIds`;
4. set the tested upstream runtime version;
5. record deliberate divergences as capability restrictions, never by widening tolerance.

The request factory derives a bounded canonical variant from validated process inputs before the
planner runs. Reprojection and resampling normalize their algorithm (`nearestneighbor` becomes
`nearest`); mosaic pins overlap order; spectral index and slope pin their named mode; and operations
with fixed contracts pin identifiers such as `pixel-center`, `closed-open`, `population`,
`equal-width`, `horn`, or `three-by-three`. Map algebra distinguishes the executable `a-plus-b`
golden case from the broader `allowlisted-expression` family. The chosen variant is persisted in
decision schema version 2 and must match on every mutating retry. An input-derived variant absent
from the process descriptor fails closed, and an engine absent from that variant's verified subset
is eliminated before locality, cost, health, or preference can affect routing.

## Focused commands

```bash
dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
  --filter FullyQualifiedName~RasterSemanticOracleTests

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
  --filter FullyQualifiedName~RasterSemanticOraclePostgisTests

dotnet test tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj \
  --filter FullyQualifiedName~GdalRasterSemanticOracleTests
```

The focused manifest covers clipping/window boundaries, reprojection and resampling, mosaic order,
map algebra, reclassification, spectral index behavior, statistics, histograms, zonal statistics,
slope, multiband promotion/color interpretation, antimeridian handling, invalid CRS, empty input,
cancellation, and partial-result cleanup. Additional PostGIS executors must extend real executable
coverage before claiming those variants.

The focused `Raster Semantic Conformance` workflow builds only the worker Dockerfile's
`raster-semantic-validation` stage. That stage executes the native slope fixture inside the pinned
GDAL base, so a lean runner cannot turn missing GDAL into a skipped proof. The ordinary full and
nightly CI matrices continue to exercise the provider-neutral oracle and the supported PostGIS
16/3.4, 17/3.5, and 18/3.6 images. No GDAL library or utility enters the serving image.
