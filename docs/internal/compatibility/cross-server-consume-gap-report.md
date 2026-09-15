# Cross-Server Consume Gap Report

Last refreshed: 2026-09-15T12:37:05Z

This report is generated from the nightly cross-server consume suite. It tracks Honua-as-client reads against reference GeoServer and MapServer sources for WMS 1.3, WFS 2.0, and WMTS 1.0.

Source TRX: `tests/TestResults/cross-server-consume.trx`

| Outcome | Count |
|---|---:|
| Passing | 14 |
| Open gaps | 0 |
| Failures | 0 |

## Open Gaps

No open compatibility gaps were reported by skipped tests.

## Failures

No failing consume tests were reported.

## Passing

| Source | Protocol | Test | Details |
|---|---|---|---|
| MapServer | WMS 1.3 | `WmsGetMap MapServer ReturnsImageForKnownLayer` | Passed |
| MapServer | WFS 2.0 | `WfsGetFeature MapServer ReturnsExpectedFeatures` | Passed |
| MapServer | WMS 1.3 | `WmsGetCapabilities MapServer ReturnsLayerDocument` | Passed |
| GeoServer | WFS 2.0 | `WfsGetFeature GeoServer ReturnsExpectedFeatures` | Passed |
| GeoServer | WMS 1.3 | `WmsGetFeatureInfo GeoServer ReturnsFeatureInfoPayload` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetTile GeoServer ReturnsAdvertisedTile` | Passed |
| GeoServer | WFS 2.0 | `WfsGetCapabilities GeoServer ReturnsFeatureTypeDocument` | Passed |
| MapServer | WMTS 1.0 | `WmtsGetCapabilities MapServer ReturnsLayerDocument` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| MapServer | WMS 1.3 | `WmsGetFeatureInfo MapServer ReturnsFeatureInfoPayload` | Passed |
| GeoServer | WMS 1.3 | `WmsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| MapServer | WFS 2.0 | `WfsGetCapabilities MapServer ReturnsFeatureTypeDocument` | Passed |
| MapServer | WMTS 1.0 | `WmtsGetTile MapServer ReturnsAdvertisedTile` | Passed |
| GeoServer | WMS 1.3 | `WmsGetMap GeoServer ReturnsImageForKnownLayer` | Passed |
