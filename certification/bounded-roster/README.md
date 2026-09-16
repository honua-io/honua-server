# Bounded 2026.1 external-client roster harness

Runs the 59 governed cells of `certification/client-protocol-requirements.v1.json`
(QGIS, GDAL/OGR, GDAL, MapLibre GL JS, OWSLib, PySTAC-Client) against one immutable
imaged candidate and writes `client-interop-cert-v1` receipts that
`scripts/certification/verify-client-certification-receipts.py --mode release`
judges fail-closed. Tracking issue: honua-io/honua-server#3434.

```bash
certification/bounded-roster/run-roster.sh \
    --tag nightly-<sha7> --expect-digest sha256:<index digest> \
    --evidence docs/internal/evidence/client-certification-<sha7> \
    --run-root <real-disk directory for raw run output>
```

The script refuses to run with uncommitted producer inputs, because every receipt
names the harness commit as `producer_source_sha`.

## What makes a cell pass

1. **Real client, public API.** Each lane image pins the governed client release
   (`lanes/*/Dockerfile`) and drives it through its own API: OWSLib and
   PySTAC-Client objects, GDAL/OGR drivers, PyQGIS providers with credentials in the
   QGIS authentication database, and MapLibre GL JS in headless Chromium with
   `transformRequest`.
2. **Every governed facet checked.** `lib/cellkit.py` requires at least one check per
   `scenario_facets` entry of the row, and every check to pass. A facet the client
   cannot exercise fails the cell with the reason; nothing is ever `skip`.
3. **On the wire, against the candidate.** Clients reach the candidate only through
   `proxy/record_wire.py`, a recording reverse proxy on an internal network. The
   emitter rewrites a claimed pass to `fail` when the cell's execution window holds no
   exchange, and takes `request_url` from the wire (never a URL carrying a
   credential).
4. **Joined to the exact candidate.** `lib/emit_receipts.py` binds each receipt to
   the image's source SHA and index digest, echoes the governed
   fixture/config/auth revision strings as join keys, and records the digests of what
   was actually applied beside them (`honua_evidence`).

Each cell runs in its own interpreter so no client cache (QGIS capabilities and tiles,
GDAL curl/WCS caches, OWSLib's process-global headers) can carry evidence between cells.

## Fixture

In order, on a fresh stack: the client-compat seeds (`docker/client-compat/seed`),
`docker/cng/seed.sql` (the governed fixture label), a recompile of the Metadata v2
compat snapshot, `fixture/roster-raster-fixture.sql` (single-publication raster
services for a COG, a gradient DEM with Terrain enabled and a Zarr datacube, each with
an access-controlled mirror) and `fixture/apply_fixture.py` (authors the COG and Zarr
objects with GDAL on the S3-compatible fixture store and registers them through the
admin API). The candidate is never restarted after seeding.

The auth profile is the client-compat one (admin API key and deterministic HS256
OIDC issuer), read at run time from `docker/client-compat/compose.yml`; receipts and
the wire log carry only credential schemes.

## Layout

| Path | Role |
| --- | --- |
| `run-roster.sh` | Orchestrator: candidate, stack, fixture, lanes, receipts, verdict |
| `compose.candidate.yml` | Overlay on `docker/client-compat/compose.yml`: digest-pinned candidate, proxy, lanes |
| `lib/cellkit.py`, `lib/runlane.py` | Cell verdict rules and per-cell process isolation |
| `lib/gdalkit.py`, `lib/qgiskit.py`, `lib/browserkit.py` | Client drivers |
| `lib/emit_receipts.py` | Observations + wire log → governed receipts |
| `lanes/*/` | Client images and cells |
| `fixture/` | Raster fixture SQL and cloud object authoring |
| `tests/` | Emitter and cell-kit rules (`python3 -m unittest discover -s certification/bounded-roster/tests`) |
