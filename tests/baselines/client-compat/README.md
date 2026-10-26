# Client compatibility baselines

`.cert.json` envelopes that represent the expected pass/fail/skip status for
each `(client_lane, protocol)` pair the `client-interop-nightly` workflow
exercises. Each nightly run compares the current envelopes from
`docker/client-compat/output/` against these baselines via
`scripts/client-compat/diff-baselines.py` and fails the workflow on any
**regression** — a baseline `pass` that does not stay `pass` in the current
run (including `pass`→`skip` and `pass`→`not-applicable`, which would
otherwise hide an endpoint that became unavailable).

## Layout

```
tests/baselines/client-compat/
  expected-pairs.json       # manifest of (client_lane, protocol) pairs
                            # the matrix must produce evidence for
  cesium/
    js-cesium-wms.cert.json
    js-cesium-wmts.cert.json
    js-cesium-ogc-tiles.cert.json
    js-cesium-ogc-maps.cert.json
  openlayers/
    ...
  gdal/
    cli-gdal-ogc-features.cert.json
    cli-gdal-wfs.cert.json
  pyqgis/
    ...
  arcgis-stub/
    arcgis-stub-featureserver.cert.json
    arcgis-stub-mapserver.cert.json
    arcgis-stub-portal.cert.json
  geopandas/            # py-geopandas   (#3392)
    py-geopandas-ogc-features.cert.json
    py-geopandas-wfs.cert.json
  owslib/               # py-owslib      (#3392)
    py-owslib-{ogc-features,wfs,wms,wmts}.cert.json
  duckdb/               # duckdb         (#3392)
    duckdb-ogc-features.cert.json
  r-sf/                 # r-sf           (#3392)
    r-sf-{ogc-features,wfs}.cert.json
  pystac/               # py-pystac      (#3392)
    py-pystac-stac.cert.json
```

The five canonical analyst lanes also carry lane-specific `NB-*` extension cases in the
envelope's `extensions` array (224 of them across the five). Those IDs are declared in
`docs/gis/data/client-certification-matrix.v1.json` and documented in
`docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md`; `DocumentationMatrixDriftTests` joins all
three in both directions, so an extension ID cannot appear in evidence without also being
declared and documented.

Filenames omit the `run_id` prefix so the baseline is content-stable across
runs. The diff script identifies envelopes by `(client_lane, protocol)` from
the JSON body, so the directory layout is documentary rather than load-bearing.

Placeholder baselines (e.g. an envelope where every applicable CERT-\* ID
is `skip` with a `pending: first-baseline run` note) are **not** acceptable
substitutes for real baselines. They mask current failures because
`skip`→`fail` would otherwise look like an acceptable transition; the diff
script now treats any non-pass baseline going to `fail` as a `new-fail`,
but the cleaner bootstrap path is to commit real envelopes from
`scripts/client-compat/refresh-baselines.sh`.

## Expected-pairs manifest

`expected-pairs.json` enumerates every `(client_lane, protocol)` pair the
matrix is contractually required to emit. Strict mode in
`diff-baselines.py` fails when any expected pair is missing from **both**
the committed baseline and the current run, AND fails additionally if any
expected pair has no committed baseline at all (even when the current run
produced evidence) — so a never-baselined lane cannot silently disappear
from the gate or pass it by emitting unreviewed evidence. Bump the
manifest in lockstep with `docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md`
when a lane × protocol is added or retired.

The full-matrix contract is the complete set enumerated by `expected-pairs.json`: 4 `js-cesium`, 6 `js`, 2
`desktop-qgis`, 2 `cli`, 3 `arcgis-stub`, 2 `py-geopandas`, 4 `py-owslib`, 1 `duckdb`, 2 `r-sf`, and 1
`py-pystac` envelopes — 27 pairs in total. A
`workflow_dispatch` subset run passes `--client-lanes` so strict mode evaluates
only the requested `client_lane` values; the scheduled nightly run evaluates the
entire manifest.

## Strict-mode failure conditions

`scripts/client-compat/diff-baselines.py --strict` fails the workflow when
any of these hold:

1. A baseline envelope's `(client_lane, protocol)` is missing from the
   current run (lane crashed without producing evidence).
2. A baseline `test_case_id` is missing from a current-run envelope (lane
   ran but truncated its output).
3. A baseline `pass` regresses to current `fail`, `skip`, or
   `not-applicable`.
4. The current-run directory contained no envelopes at all.
5. An `expected-pairs.json` entry is absent from both baseline and current
   run.
6. A current envelope reports `fail` for a test case with no baseline
   entry, or with a non-fail baseline (`skip`/`not-applicable`/placeholder).
   This catches both brand-new failures and regressions that a placeholder
   skip baseline would otherwise hide.
7. An `expected-pairs.json` entry has no committed baseline, even when the
   current run produced evidence — bootstrap real baselines via
   `scripts/client-compat/refresh-baselines.sh` before the gate releases.
8. A baseline `pass` stays `pass` but its `measured_count` changed, or its
   `measured_delta` grew beyond the case's explicit
   `measured_delta_tolerance` (#6514). A changed count is a behavior change
   until a reviewed baseline bump attributes it.

When a CI lane exits non-zero, its artifact should still contain
`lane-exit-code.txt` and `compose.log`. Those files are diagnostic only; the
strict-mode decision is still made from the current `.cert.json` envelopes and
the committed baseline contract above.

## Updating

When the server intentionally changes behavior (e.g., a new protocol reaches
GA, an error envelope is reshaped), refresh the baselines by running:

```bash
./scripts/client-compat/refresh-baselines.sh
```

Review the generated diff carefully. Any baseline `pass` that does not stay
`pass` is treated as a regression (see the failure conditions above), so a
deliberate `pass`→`skip` (or `pass`→`not-applicable`) only lands when the
baseline bump explicitly drops the case in the same commit — call this out in
the commit message and trim the case from the baseline so the gate stops
flagging it. `skip`→`pass` and `fail`→`pass` are improvements and need no
special handling.

The baseline-bump cadence is scheduled quarterly via `/schedule`. Don't bump
baselines as part of an unrelated feature PR — that hides regressions inside
unrelated review.

### Reviewed measured-count reconciliation (2026-10-26)

Condition 8 arrived after most envelopes here were captured (2026-05-07 to
2026-08-29), so 36 passing cases carried counts from an older fixture and
server. Only those cases' `measured_count` and `notes` were bumped, from
nightly run 40335248083 at `4cdb0847d6`; statuses and every other case are
unchanged. Each change is attributed to the commit that caused it:

| Change | Cases | Cause |
|---|---|---|
| Collections / feature types 4-8 -> 11, services 5 -> 12, admin services 6 -> 8, authenticated WFS layers 7 -> 14, portal search 9 -> 12 | `CERT-DISC-01/02`, `CERT-CONN-01`, `CERT-AUTH-02`, `CERT-PRTL-SRCH-01`, `JS-EXT-OL-COLL-01`, `NB-*-COLL/CONF/CAP/OWS/AUT` | Fixture growth: WFS-T scratch layers 10-12 (#3684), authenticated mirrors (#3682), `cert_relations` layers 20-22 (#5300) |
| OGC API Features conformance 26 -> 21 | `js` `CERT-DISC-01`, `NB-OWS-OAF-CONF-01` | Six unevidenced CQL2 extension classes withdrawn by the exact-candidate evidence gate (#4240); Part 4 `update` added (#5147) |
| OGC API Maps conformance 10 -> 11 | `js` `CERT-DISC-01` | `conf/styled-map` (#1413) |
| Collection links 13 -> 14 | `py-owslib` `CERT-DISC-02` | `queryables` link and CRS84 `storageCrs` (#5040) |
| WFS CRS options 2 -> 5, CRS spellings 4 -> 11 | `py-owslib` `CERT-DISC-02`, `NB-OWS-WFS-CRS-02` | CRS84 identifiers advertised (#3641) |
| Attribute fields 14 -> 15, 15 -> 16 | `js`/`py-geopandas` WFS `CERT-SCHM-01` | `eo:cloud_cover` fixture column (`aa20e7e3d1`); Arrow schema includes geometry `shape` (#4750) |
| Paging 1 -> 2 | `js` `CERT-PAGE-02` | Case now measures page size, rejecting empty pages (#6309) |
| STAC item assets 1 -> 2 | `NB-STAC-ITEM-01` | COG asset published by the seed (`88407d7d5b`) |
| Legend PNG bytes 3904 -> 4320, 509 -> 995 | `NB-OWS-WMS-CAP-04`, `NB-OWS-WMTS-CAP-05` | Shared swatch legend replaces the 256x256 world-tile legend; optional LegendURL size dropped (#5747) |
| WMTS tile bytes 558 -> 546 | `CERT-RNDR-01`, `NB-OWS-WMTS-TILE-04` | PNG encoder output; tile content is now asserted by authored-style pixels (#6312) |

### Reviewed OpenLayers WFS false-positive correction (#6304)

The original `js/wfs` baseline labeled an unfiltered GetFeature read as
`CERT-QFLT-01` filtering. The reviewed correction keeps that required core ID
present as `skip`, rather than claiming filtering or removing its applicability.
The actual immutable-image rehearsal at producer `549043d645d5ef4539450fccb6739b8a33df422e`
observed that skip plus a passing unfiltered-parsing extension
`JS-EXT-OL-WFS-GETFEATURE-01`. Only that false pass and the new extension are
corrected; historical envelope metadata and other case statuses are retained.
The baseline is an expected-status comparison, not release qualification:
the canonical fixture still requires `CERT-QFLT-01` and the release verifier
rejects skipped required filtering even when unfiltered retrieval passes.
