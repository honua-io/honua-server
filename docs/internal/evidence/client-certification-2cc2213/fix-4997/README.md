# WCS outside-subset regression (#4997)

Release promise: the declared WCS 2.0.1 profile must work through the governed
OWSLib `serve.wcs` external-client roster cell, including its boundary facet.
An entirely outside subset is an OWS `InvalidSubsetting` client error, not HTTP
500 `NoApplicableCode`. This addresses the existing must-fix-before-cut item.

The handler validates the spatial window before raster export. It compares
the actual clip polygon in the coverage's native CRS using the shared
coordinate transform service, rejects disjoint and edge-touching windows with HTTP 404 and an OWS
`InvalidSubsetting` report, and leaves intersecting windows to the canonical
raster backend. Both `SUBSET` and the accepted `BBOX` alias carry the correct
error locator. Ordinary raster failures retain their existing error mapping.

## Independent fixture and assertions

The existing `certification/bounded-roster/regressions/coverage-fixture.sql`
authors a 4x4 Float32 raster in EPSG:4326, upper-left `(-124,40)`, pixel size
`(1,-1)`. Its zero-based value is `10*row+column`, except `(1,2)` is nodata
`-9999`. Expected results are computed from that definition:

- Central window `Long(-123,-121),Lat(37,39)`: `[11,-9999;21,22]`, transform
  `(-123,1,0,39,0,-1)`, in either named-axis order.
- Corner-straddling window `Long(-125,-122),Lat(38,41)`: `[0,1;10,11]`, transform
  `(-124,1,0,40,0,-1)`.
- Both outputs: exactly 2x2, one Float32 band, nodata `-9999`, EPSG:4326,
  `image/tiff`. Rasterio decodes the bytes returned by OWSLib's public API.
- Seven outside windows cover east/west/north/south, omitted axes, a touching
  edge, and EPSG:3857 subsetting. Each must return HTTP 400/404, an OWS 2.0
  `ExceptionReport`, `InvalidSubsetting`, and locator `SUBSET`.

The endpoint regression additionally asserts that outside windows never invoke
raster export, covers `BBOX` and its CRS alias, and checks intersecting windows
in both 4326→3857 and 3857→4326 directions. Their independently chosen numeric
domains would be disjoint if the validation forgot to transform them.

## Reproduction

Use an isolated `docker/client-compat/compose.yml` project and the current
`docker/client-compat/seed/run.sh` with its normal SQL/YAML seeds. An older local
seed image refers to a removed raster seed file: mount and invoke the current
script when using that image. Apply `coverage-fixture.sql` after the normal
seed. The legacy seed also resets the raster columns' storage mode after
migration journaling. Before current-trunk startup, reapply the owning
`055_SetRasterDataExternalStorage.sql` to this isolated database with schema
`honua`; this restores EXTERNAL storage on `raster_data.raster` and
`raster_tiles.tile_data` without changing fixture pixel values. The current
server correctly refuses readiness while those migration-owned settings are
missing. Do not reuse another lane's database or Redis.

Publish the fixed server with the lane's PATH `dotnet`, `PublishAot=false`,
`UseAppHost=false`, and `--self-contained false`. Package that entire output
in a local runtime image and replace only the isolated project's server.
Mount the regression directory at `/regressions:ro` in the image built from
`coverage-client.Dockerfile`, join the project's network, and run:

```bash
python /regressions/wcs_subset_clients.py --base-url http://honua:5000
```

For the governed cell, use the unchanged
`certification/bounded-roster/lanes/python/owslib_cells.py` and `lib/` from this
revision, the requirements JSON, and `tests/python/shared`. Supply the isolated
server's fixture API-key/OIDC environment through `rosterenv`; execute
`owslib_cells.wcs_service()` in the pinned OWSLib 0.36.0 lane. No roster test or
denominator changes are used.

The before/after artifacts are focused local fix-candidate evidence. They do
not replace the original immutable `nightly-2cc2213` receipts or certify an
official release artifact.

## Local build and formatting

The server published successfully with:

```bash
HONUA_MSBUILD_NODE_CAP=4 dotnet publish src/Honua.Server/Honua.Server.csproj \
  -c Debug --no-restore -p:PublishAot=false -p:UseAppHost=false \
  --self-contained false -o artifacts/wcs4997
```

Both changed projects were formatted successfully with `timeout 20m dotnet
format <project> --no-restore --include <changed-files>`: the protocol project
included only `Wcs20Handler.cs` and `Wcs20CoverageBackend.cs`; the test project
included only `Wcs20EndpointsTests.cs`. The formatter expanded the new test's
extent initializer; no unrelated files changed.

## External-client results

| Check | Before | Fixed published image |
| --- | --- | --- |
| Seven outside windows, including projected coordinates and an edge touch | Seven HTTP 500 `NoApplicableCode` failures | Seven HTTP 404 `InvalidSubsetting` responses, locator `SUBSET` |
| Three independent pixel/nodata/CRS/geotransform assertions | Pass | Pass |
| Unchanged OWSLib `serve.wcs` roster cell | Boundary fails; other five facets pass | All six facets pass |

`owslib-before.json` and `owslib-after.json` contain the real-client results.
`roster-before/` and `roster-after/` contain the unchanged governed cell's
observations. `identities.json` records the local image IDs, published and
endpoint-test assembly hashes, source revision, and fixture/harness hashes.
The published image uses the project's default trimming; the endpoint tests
exercise the untrimmed build from the same runtime source.

The eight new endpoint cases also failed before the fix: their substituted
export backend returned its test PNG because invalid requests reached export.
That deliberately isolates the missing pre-export validation; the real PostGIS
client replay above independently reproduces and resolves the HTTP 500.
`endpoint-before.json` retains the eight assertion failures, with no skipped
cases. No acceptance criterion is released.

## Focused .NET result

The initial focused run passed **24/24**, with zero failures or skipped tests:
eight new outside-window endpoint cases, two new valid cross-CRS cases,
eight request-CRS unit cases, the two existing Zarr trim cases, and four
existing plain/subset/malformed/reversed-bound endpoint cases.
`endpoint-after.json` records every test name, outcome, counter, and the exact
filter. The test project was rebuilt using `BuildProjectReferences=false`
after the complete local dependency build; its copied WCS assembly hash was
verified against the rebuilt protocol assembly. No tests were weakened,
removed, or skipped.

The pre-PR script's dry run selected the WFS/WCS/WPS shard. The local checks
above replace its solution-targeted formatting step with the operator-required
changed-project-only invocations. The PR's normal required gates remain the
admission checks for its exact head.

## Cross-CRS review regressions

The review identified two cases beyond the original boundary replay: failure
of the shared coordinate transform must remain a server error, and a projected
rectangle's enclosing envelope can overlap a raster that its polygon misses.
The guard now transforms the actual clip polygon's vertices through the shared
batch transform service, matching the raster backend's ST_Transform input.
It requires interior/interior intersection; edge or point contact is empty.
An unavailable or non-finite transform raises the existing sanitized HTTP 500
`NoApplicableCode`, instead of incorrectly blaming the request.

`wcs-subset-utm-fixture.sql` authors a second 4x4 Float32 gradient, with the same
pixel formula and nodata, in EPSG:32610: upper-left `(587850,4095500)`, pixel
size `(25,-25)`. The geographic subset `Long(-122,-121),Lat(37,38)` has an
intersecting projected envelope but a disjoint polygon. The independent
Rasterio/PROJ oracle transforms its four corners and evaluates the west edge
at both raster Y limits: every fixture coordinate lies at least 1025 metres
west of that edge. `utm-before.json` reproduces HTTP 500 against the initial
fix, while its valid geographic control already returns the exact 4x4 values,
Float32 type, nodata, native CRS, and transform. Apply the UTM SQL after the
normal seed and run `python /regressions/wcs_subset_utm_client.py` in the same
pinned client image to reproduce both assertions.

The additional endpoint tests exercise that disjoint UTM polygon and a shared
transform service returning its documented failure sentinel. `endpoint-review-before.json` records both regressions failing against the
initial implementation: the fake export returns HTTP 200 for the disjoint
polygon, and the unavailable transform is mislabeled HTTP 404. Neither case
was skipped.

The corrected protocol and server builds passed with zero warnings/errors.
The final server was republished in Debug with trimming enabled and AOT
explicitly disabled. Both changed projects were formatted again with the
20-minute timeout and explicit `--include` paths; the test invocation included
`Wcs20EndpointsTests.cs` and `Wcs20SubsetTransformFailureTests.cs`. Formatting
made no further source changes.

The corrected published image passes the real-client replays:

- `utm-after.json`: the disjoint polygon now returns HTTP 404
  `InvalidSubsetting`; the valid geographic subset retains all 16 independently
  computed values, nodata, Float32 type, native CRS, and geotransform.
- `owslib-final.json`: all seven original outside checks return HTTP 404, and
  all three independent pixel/metadata checks still pass.
- `roster-final/`: the unchanged governed OWSLib `serve.wcs` cell passes all six
  facets. `final-identities.json` binds these results to the rebuilt source,
  published image, assembly hashes, UTM fixture, and independent oracle.
