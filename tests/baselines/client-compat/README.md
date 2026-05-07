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
```

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

The current full-matrix contract is 16 pairs: 4 `js-cesium`, 6 `js`, 2
`desktop-qgis`, 2 `cli`, and 2 `arcgis-stub` envelopes. A
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
