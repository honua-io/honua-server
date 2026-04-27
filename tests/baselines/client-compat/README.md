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

## Expected-pairs manifest

`expected-pairs.json` enumerates every `(client_lane, protocol)` pair the
matrix is contractually required to emit. Strict mode in
`diff-baselines.py` fails when any expected pair is missing from **both**
the committed baseline and the current run, so a never-baselined lane
cannot silently disappear from the gate. Bump the manifest in lockstep
with `docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md` when a lane × protocol
is added or retired.

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
   entry (a brand-new failure in an unbaselined lane).

## Updating

When the server intentionally changes behavior (e.g., a new protocol reaches
GA, an error envelope is reshaped), refresh the baselines by running:

```bash
./scripts/client-compat/refresh-baselines.sh
```

Review the generated diff carefully — only `pass`→`fail` transitions are
regressions; `skip`→`pass` is an improvement and `pass`→`skip` is acceptable
when the matrix explicitly drops a case (call this out in the commit message).

The baseline-bump cadence is scheduled quarterly via `/schedule`. Don't bump
baselines as part of an unrelated feature PR — that hides regressions inside
unrelated review.
