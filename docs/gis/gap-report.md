# Cross-Client Certification Gap Report

_Generated: 2026-05-07T14:14:22.353669+00:00_

This report is auto-refreshed by the `client-interop-nightly` workflow.
It compares the latest `.cert.json` envelopes from each Docker client lane
against the committed baselines under `tests/baselines/client-compat/`.

## Lane coverage summary

| Lane | Protocol | Total | Pass | Fail | Skip | N/A |
|------|----------|-------|------|------|------|-----|
| arcgis-stub | featureserver | 24 | 14 | 0 | 9 | 1 |
| arcgis-stub | mapserver | 24 | 14 | 0 | 9 | 1 |
| cli | ogc-features | 24 | 4 | 0 | 0 | 20 |
| cli | wfs | 24 | 4 | 0 | 0 | 20 |
| desktop-qgis | ogc-features | 21 | 18 | 0 | 3 | 0 |
| desktop-qgis | wfs | 16 | 12 | 0 | 4 | 0 |
| js | mvt | 24 | 2 | 0 | 10 | 12 |
| js | ogc-features | 24 | 6 | 0 | 16 | 2 |
| js | ogc-maps | 24 | 5 | 0 | 3 | 16 |
| js | wfs | 24 | 5 | 0 | 6 | 13 |
| js | wms | 24 | 4 | 0 | 4 | 16 |
| js | wmts | 24 | 4 | 0 | 4 | 16 |
| js-cesium | ogc-maps | 24 | 2 | 0 | 6 | 16 |
| js-cesium | ogc-tiles | 24 | 3 | 0 | 5 | 16 |
| js-cesium | wms | 24 | 4 | 0 | 4 | 16 |
| js-cesium | wmts | 24 | 3 | 0 | 5 | 16 |

## No deviations from baseline

All current `.cert.json` envelopes match the committed baseline. ✅
