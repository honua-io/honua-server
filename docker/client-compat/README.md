# Client compatibility Docker harnesses

Real-client interop test matrix for Honua Server. Each lane runs the actual GIS
client software customers use (or, for ArcGIS Pro, a stub that mirrors its
REST consumption pattern), so regressions caused by client-specific quirks —
header tolerance, content-type handling, capability-document parsing, axis
order, GML flavor — surface before customers see them.

CITE conformance suites (`docker/cite/`) prove spec compliance. These harnesses
prove that real clients can consume the server.

## Lanes

| Lane | Image | Protocols exercised |
|------|-------|--------------------|
| `gdal` | `ghcr.io/osgeo/gdal:ubuntu-small-latest` | OGC API Features, WFS 2.0 |
| `pyqgis` | `qgis/qgis:ltr` | OGC API Features, WFS 2.0 |
| `openlayers` | `mcr.microsoft.com/playwright:v1.59.1-noble` | OGC API Features, OGC API Maps, OGC API Tiles, WFS, WMS, WMTS |
| `cesium` | `mcr.microsoft.com/playwright:v1.59.1-noble` | WMS, WMTS, OGC API Tiles, OGC API Maps |
| `arcgis-stub` | `python:3.12-slim` | GeoServices REST FeatureServer / MapServer (REST stub) |

ArcGIS Pro itself runs only on Windows + a paid license. The stub lane exercises
the same REST endpoints the Pro client consumes and emits a comparable cert
envelope. Full Pro visual coverage still requires a licensed runner; until then
the stub records those cases as `skip` with a documented pending-runner note.

## Runtime contract

The compose stack starts `postgres` and `redis`, then `honua`, then the
one-shot `seed` service, and finally the requested client lane. Honua completes
its production migrations and readiness checks before fixture data is seeded. The Honua container binds HTTP/1 on
port `5000` for browser and CLI base URLs and h2c gRPC on port `5001` so the
container uses the same split-transport shape as production images. Redis is
part of the stack because server workflows that require a cache or durable
coordination backend must fail in the interop lane the same way they would fail
in a real deployment.

PostGIS enables GDAL raster drivers. Honua migrations create the raster and
Metadata v2 tables before `client-compat-v1.sql` populates fixture records. The browser
lanes target `browser_compat` layer `2000`; the GDAL and PyQGIS lanes target
`test_service` collection `0`.

## Run a single lane

```bash
docker compose -f docker/client-compat/compose.yml run --rm cesium
```

The compose file mounts the repo's `tests/` directory read-only and a
`./output` directory read/write so each lane writes `.cert.json` envelopes back
to the host for the baseline-diff step.

## Run the entire matrix

Run lanes sequentially via the refresh script — it mirrors the CI matrix
shape and captures every lane's evidence even when one of them fails:

```bash
./scripts/client-compat/refresh-baselines.sh
```

A subset can be passed positionally (`./scripts/client-compat/refresh-baselines.sh
cesium gdal`).

The previous `--profile matrix up --abort-on-container-exit` shortcut is
**not** safe for refresh use: `--abort-on-container-exit` terminates every
container the moment any one of them exits. In this stack, the one-shot
`seed` dependency exits before the client lane runs, so `up` can
tear the stack down before any `.cert.json` evidence is written. Use
`docker compose --profile <lane> run --rm <lane>` when you need to drive
compose by hand.

## Output layout

```
docker/client-compat/output/
  cesium/
    <run-id>-js-cesium-wms.cert.json
    <run-id>-js-cesium-wmts.cert.json
    ...
  openlayers/
    <run-id>-js-ogc-features.cert.json
    ...
  gdal/
    gdal-ogr-results.json                       # raw GDAL evidence (kept for inspection)
    <run-id>-cli-gdal-ogc-features.cert.json    # converted by scripts/client-compat/convert-gdal-results.py
    <run-id>-cli-gdal-wfs.cert.json
  pyqgis/
    <run-id>-desktop-qgis-ogc-features.cert.json
    ...
  arcgis-stub/
    <run-id>-arcgis-stub-featureserver.cert.json
    <run-id>-arcgis-stub-mapserver.cert.json
```

The baseline-diff step in `.github/workflows/client-interop-nightly.yml` reads
these files and compares them with `tests/baselines/client-compat/`. The
GDAL/OGR pytest suite emits its own per-protocol/category JSON report; the
lane runner converts it into per-protocol `.cert.json` envelopes via
`scripts/client-compat/convert-gdal-results.py` so the diff sees a uniform
shape across lanes.

If a lane exits non-zero in CI, its artifact also contains `lane-exit-code.txt`
and the complete `compose.log`, including initial migration diagnostics. The
lane job fails after uploading evidence. The baseline-diff job still runs,
requires real envelopes from every requested lane, checks their server source
SHA against the workflow's built image, and performs strict baseline comparison.

## Seed data

A one-shot `seed` service (built from `docker/client-compat/seed/`) waits for
`honua: service_healthy`. Starting it before migrations creates unjournaled
migration-owned tables and trips the production schema-floor guard. It applies:

- `tests/seed/client-compat-v1.sql` — `test_service` layer `0`, transaction
  scratch layers `10`–`12`, and the Metadata v2 fixture snapshot helper. Its
  compatibility `CREATE TABLE IF NOT EXISTS` statements see tables already
  created and journaled by Honua migrations.
- `tests/seed/browser-compat.yaml` — `browser_compat` layers `2000`–`2002`.
- `tests/seed/client-compat-auth-wave1.yaml` — protected mirrors at `2010`
  (browser points) and `2011` (canonical vector data). These IDs preserve the
  transaction scratch layers. The final snapshot includes every fixture layer.

Every client depends on both `honua: service_healthy` and
`seed: service_completed_successfully`, so no client observes an empty fixture.

## Real multidimensional raster fixture

The `multidim-fixture` profile proves the production multidimensional path,
not a metadata-only catalog projection. It creates a two-time-slice CF NetCDF
artifact, uploads it to the pinned LocalStack S3 emulator, registers it through
the admin API, dispatches `coverage.multidim.metadata` through Redis to the
native GDAL worker, and verifies the derived Zarr store plus an ImageServer PNG
render for both time slices. An independent formula checks all 32 Zarr values,
coordinate arrays, dimensions, units, fill value and nodata locations. A fixed
0–40 stretch and nearest-neighbor sampling make both 4×4 PNGs comparable against
independently computed north-up RGBA pixels, including transparent nodata.

Run it from the repository root:

```bash
docker/client-compat/multidim-fixture/run.sh
```

The script opts this profile into the development Pro grant and S3 provider;
the ordinary client-compat lanes retain their Production and local-storage
defaults. Remove the disposable stack afterward with:

```bash
docker compose -f docker/client-compat/compose.yml --profile multidim-fixture down --remove-orphans
```

## Adding a new client lane

1. Add `docker/client-compat/<lane>/Dockerfile`.
2. Add a service block to `compose.yml`:
   - `profiles: ["matrix", "<lane>"]` — the per-lane profile is what the
     CI workflow targets via `docker compose --profile <lane> run --rm <lane>`,
     and the `matrix` profile is what `--profile matrix up` selects to run
     the full set.
   - Dependencies on `honua: service_healthy` and
     `seed: service_completed_successfully`.
   - `HONUA_BASE_URL=http://honua:5000`
   - `volumes: [ ../../tests:/workspace/tests:ro, ./output/<lane>:/output ]`
3. Wire the lane into `.github/workflows/client-interop-nightly.yml` — three
   places in the `prepare` job must stay in sync or the workflow will
   reject the matrix at dispatch time:
   - the `workflow_dispatch.inputs.lanes` default value,
   - the `allowed` lane list in the resolver step (an unknown lane fails
     the run with `::error::Unknown lane '<lane>'` rather than silently
     producing an empty matrix), and
   - the `LANE_TO_CLIENT_LANE` associative array, which maps the lane
     name to the `client_lane` value the lane writes into its
     `.cert.json` envelopes (a missing entry fails the run with
     `::error::No client_lane mapping for '<lane>'`).
4. Add the lane to `scripts/client-compat/refresh-baselines.sh` (its
   `DEFAULT_LANES` and `ALLOWED_LANES` arrays mirror the workflow matrix).
5. Add the `(client_lane, protocol)` pair(s) the lane will emit to
   [`tests/baselines/client-compat/expected-pairs.json`](../../tests/baselines/client-compat/expected-pairs.json)
   and seed real baselines via
   `scripts/client-compat/refresh-baselines.sh` — strict mode fails
   when any expected pair has no committed baseline at all, so the
   workflow will start failing the moment the manifest entry lands
   without a paired baseline file.

The lane's command must produce one or more `.cert.json` envelopes under
`/output` so the baseline diff can compare deterministically.
