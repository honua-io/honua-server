# R `sf` / `ows4R` certification suite

This is the R canonical-client lane of the cross-client certification matrix
(honua-server#3392, parent #3389). It drives a real R geospatial client —
[`sf`](https://r-spatial.github.io/sf/) over GDAL for feature reads and
[`ows4R`](https://github.com/eblondel/ows4R) for the WFS capabilities /
DescribeFeatureType / GetFeature-option surface — against a running Honua
server, and writes one `.cert.json` evidence envelope per protocol.

| | |
|---|---|
| Lane id (`client_lane`) | `r-sf` |
| Protocols | `ogc-features` (1.0), `wfs` (2.0.0) |
| Container | `docker/client-compat/r-sf/` (`rocker/geospatial:4.6.1`) |
| Compose service | `r-sf` |
| Envelopes | `{run_id}-r-sf-ogc-features.cert.json`, `{run_id}-r-sf-wfs.cert.json` |

## Layout

| File | Purpose |
|---|---|
| `certification/cert_envelope.R` | Envelope writer — the R mirror of `tests/python/shared/cert_envelope.py`. Same field order, status vocabulary, worst-status-wins rule, and the fail-closed "applicable but not executed ⇒ `skip`" rule. |
| `certification/canonical_fixture.R` | Fixture expectations — the R mirror of `tests/python/shared/canonical_fixture.py`. Do not invent lane-local numbers here. |
| `certification/run_sf_lane.R` | The driver: every CERT/NB case, one function each, each independently `tryCatch`-trapped. |

## What it asserts

* The **16 applicable common-core IDs** (`CERT-CONN/AUTH/DISC/SCHM/QFLT/PAGE/GEOM/ERRH-*`)
  on both protocols. The eight `CERT-RNDR-*` IDs are emitted as
  `not-applicable`: `sf`/`ows4R` is a data-access client with no drawing
  surface.
* A lane-specific **`NB-RSF-*` extension suite** in the envelope's
  `extensions[]` array (never in `results[]`, which stays exactly the 24
  common-core IDs in order): attribute typing (`TYP`), null handling (`NUL`),
  geometry fidelity (`GEO`), CRS and axis order (`CRS`), paging invariants
  (`PAG`), format round-trips (`FMT`), the error surface (`ERR`), the admin
  credential path (`AUT`), cross-protocol agreement (`XPR`), OGC API Features
  specifics (`OAF`), and the ows4R depth cases (`OWS`).

Feature reads go through `sf::st_read()` — on the GDAL `OAPIF:`/`WFS:` DSNs, or
on a fully parameterised protocol URL when the case is about a protocol
parameter GDAL does not expose through the DSN. `httr` is used only for the
`CERT-AUTH-*` control-plane probe and for transport-shape checks (status codes,
headers, `numberMatched`); every such result says so in its `notes`.

## Run it locally

Against the client-compat compose stack (from the repository root):

```bash
docker compose -f docker/client-compat/compose.yml --profile r-sf run --rm r-sf
```

Against an already-running server, using the lane image directly:

```bash
docker build -f docker/client-compat/r-sf/Dockerfile -t honua-clientcompat-r-sf:dev .
docker run --rm --network client-compat_compat \
  -v "$PWD/tests:/workspace/tests:ro" \
  -v "$PWD/docker/client-compat/output/r-sf:/output" \
  -e HONUA_BASE_URL=http://honua:5000 \
  honua-clientcompat-r-sf:dev
```

Or on the host, with R 4.6 + `sf`, `ows4R`, `jsonlite`, `digest`, `httr`
installed:

```bash
HONUA_R_SF_BASE_URL=http://localhost:5000 \
HONUA_R_SF_OUTPUT_DIR="$PWD/tests/TestResults" \
Rscript tests/r/certification/run_sf_lane.R
```

## Environment

| Variable | Meaning |
|---|---|
| `HONUA_R_SF_BASE_URL` / `HONUA_BASE_URL` | **Required.** Server base URL; the lane has no local-server fallback and fails clearly when neither is set. |
| `HONUA_R_SF_OUTPUT_DIR` | Envelope output directory (the container sets `/output`). Defaults to `tests/TestResults`. |
| `HONUA_R_SF_SERVICE_ID` / `HONUA_R_SF_COLLECTION_ID` | Override the seeded service / collection (`test_service` / `0`). |
| `HONUA_R_SF_SERVER_COMMIT` / `HONUA_R_SF_SERVER_VERSION` | Override the receipt fields instead of probing `git` / `/api/v1/admin/version`. |
| `HONUA_R_CERT_DIR` | Directory holding this suite; only needed when the driver is not launched via `Rscript <path>`. |
| `CI` | Sets the envelope's `environment` to `ci` (otherwise `local`). |

## Notes for maintainers

* The driver never aborts on a single failure: each case is wrapped in
  `tryCatch`, and both envelopes are written even when the protocol run throws.
  An applicable common-core case that never executed is recorded as `skip`,
  which the strict baseline diff treats as a fail-closed signal.
* `run_id` is UTC `%Y%m%dT%H%M%SZ` and must never contain `-`:
  `scripts/client-compat/refresh-baselines.sh` strips the envelope filename up
  to the first `-` to derive the stable baseline name.
* `fixture_revision` and `server_config_revision` are `sha256:` digests of
  `tests/seed/client-compat-v1.sql` and
  `tests/config/client-compat-server-v1.json`, computed with `digest::digest()`
  and verified to match `sha256sum`.
* When the fixture changes, update `tests/python/shared/canonical_fixture.py`
  and `docs/gis/data/client-certification-fixture.v1.json` first, then mirror
  the constants into `certification/canonical_fixture.R`.
