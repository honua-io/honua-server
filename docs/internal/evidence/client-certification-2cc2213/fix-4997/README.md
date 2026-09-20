# WCS outside-subset regression (#4997)

Release promise: the declared WCS 2.0.1 profile must work through the governed
OWSLib `serve.wcs` external-client roster cell, including its boundary facet.
An entirely outside subset is an OWS `InvalidSubsetting` client error, not HTTP
500 `NoApplicableCode`. This addresses the existing must-fix-before-cut item.

The handler validates the spatial window before raster export. It compares
bounds in the coverage's native CRS using the shared coordinate transform
service, rejects disjoint and edge-touching windows with HTTP 404 and an OWS
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
seed. Do not reuse another lane's database or Redis.

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
