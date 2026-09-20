# Coverage client regression evidence: #4996

This fixes the existing must-fix-before-cut ruling B item. The release promise
is a working declared OGC API Coverages path in the bounded external-client
roster: GDAL must discover and read a coverage, and OWSLib spatial trims must
return the requested window without axis-order-dependent results.

## Independent raster oracle

`certification/bounded-roster/regressions/coverage-fixture.sql` authors a 4x4
EPSG:4326 float32 raster with upper-left `(-124,40)`, pixel size `(1,-1)`, and
zero-based `value(row,column) = 10*row + column`. Cell `(1,2)` is nodata `-9999`.
The central `Lon(-123:-121),Lat(37:39)` window must therefore be exactly:

```text
11  -9999
21     22
```

Its size is 2x2, its band count is one, and its geotransform is
`(-123,1,0,39,0,-1)`. Both named-axis orders must agree. No expected value is
copied from server output. The SQL SHA-256 is
`4b59980faa879fc74e970333b837fa735917e9dab67619a35c9d7642c992908c`.

`coverage_clients.py` uses GDAL's OGCAPI driver (`API=COVERAGE`) to open the
native 4x4 grid and read the central window, then decodes both direct subset
responses through GDAL to verify values, float32 type, nodata, CRS and transform.
The pinned GDAL virtual coverage band does not expose the GeoTIFF nodata tag;
the actual server response is required to preserve it. OWSLib uses its normal
`Coverages.coverage(subset=...)` API in both orders; Rasterio independently
decodes and checks each returned GeoTIFF. GDAL's block cache can fetch the full
small 4x4 grid for a 2x2 client window; direct subset and OWSLib assertions also
prove that server-side trimming returns exactly 2x2 pixels.

## Identities and results

- Baseline: `ghcr.io/honua-io/honua-server:nightly-2cc2213`, digest
  `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51`.
  GDAL reports `API COVERAGE requested, but not available`; OWSLib gets HTTP 400
  for its normal `subset` request. Raw failure logs are adjacent.
- Initial fixed image: `honua-server:ogc4996-fixed`, digest
  `sha256:968179d47f9e3d7da9ce26ea9160e658b5c967a130fee7e59a3400692009b859`,
  source `d954867713`. Both independent pixel proofs and both roster cells pass.
- Final source additionally retains the canonical `scale-size` x-then-y
  restriction; only GDAL's `scaleSize` alias accepts reversed scale axes.
  This preserves the pre-existing negative regression assertion. Review fixes
  also report PostGIS `8BSI` as `INT8` and validate unscaled subset dimensions
  against the existing 8192-pixel cap before export. The size regression uses
  independently authored 20,000x20,000 grid metadata: full/native-axis and
  omitted-axis trims exceed the cap; a 0.02-degree window at 0.00001 degrees
  per pixel is bounded; explicit 128x64 scaling is accepted. Unknown or rotated
  large grids require explicit scaling rather than guessing a safe window.
- GDAL 3.8.4: `ghcr.io/osgeo/gdal:ubuntu-small-3.8.4`, digest
  `sha256:60d3bc2f8b09ca1a7ef2db0239699b2c03713aa02be6e525e731c0020bbb10a4`.
- OWSLib 0.36.0 + Rasterio 1.4.3: image built with the adjacent
  `coverage-client.Dockerfile`, local image digest
  `sha256:447490232decb5fe40b362a85e45c67c821f7c5c2b964ef011827de0a9048296`.
- Original OWSLib roster image: `client-compat-owslib:latest`, local digest
  `sha256:4c077283c4885d15ed9841f52bfab29d4057595a725c10f650933e881c937210`;
  its observation records the actual OWSLib version.

## Reproduction

Create an isolated `docker/client-compat/compose.yml` project, set
`PUBLIC_BASE_URL=http://honua:5000`, run its seed, then apply `coverage-fixture.sql`
to that project's database. The legacy seed recreates raster tables after
migration journaling; restore migration 055's `SET STORAGE EXTERNAL` statements
in the isolated database before current-trunk startup. When replacing the
baseline image, flush only that project's Redis cache to discard baseline
metadata. Publish the fix with `PublishAot=false`, `UseAppHost=false` and
`--self-contained false`, package the output, and replace the Honua image.

Mount the regression directory at `/regressions:ro` and join the candidate's
Docker network. Run `python3 /regressions/coverage_clients.py gdal` in the pinned
GDAL container. Build `coverage-client.Dockerfile` and run
`python /regressions/coverage_clients.py owslib` in that container.

The unchanged Coverages roster functions come from harness source
`bdb06a126201c7d18c99fa82ed9f0347d28e0ed5` on
`fix/3434-bounded-roster-2cc2213`. Extract its `certification/bounded-roster`,
requirements JSON and `tests/python/shared`; put the harness lib, relevant lane
(`gdal` or `python`) and shared module parent on `PYTHONPATH`. Run
`gdal_ogc_cells.coverages_service()` and `owslib_cells.coverages()` in their
pinned containers (override the OWSLib image entrypoint to Python). Each checks
all six governed facets; the JSON observations are adjacent. No roster test
patch is used for these two functions.

These are focused fix-candidate proofs, not a claim that the other roster cells
or the official release artifact have been recertified. No acceptance criterion
is released and no protocol surface is demoted.

Project-scoped `timeout 20m dotnet format --no-restore --include ...` passed for
the changed protocol handler and the two changed Coverage test files after the
review fixes. The served and developer OpenAPI subset/scaling parameter
definitions match; Python syntax and `git diff --check` also pass.
