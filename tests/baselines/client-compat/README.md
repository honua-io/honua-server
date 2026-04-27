# Client compatibility baselines

`.cert.json` envelopes that represent the expected pass/fail/skip status for
each `(client_lane, protocol)` pair the `client-interop-nightly` workflow
exercises. Each nightly run compares the current envelopes from
`docker/client-compat/output/` against these baselines via
`scripts/client-compat/diff-baselines.py` and fails the workflow if any
test case regresses from `pass` → `fail`.

## Layout

```
tests/baselines/client-compat/
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
