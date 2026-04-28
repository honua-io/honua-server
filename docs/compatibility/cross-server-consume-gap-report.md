# Cross-Server Consume Gap Report

Last refreshed: 2026-04-27T21:27:30Z

This report is generated from the nightly cross-server consume suite. It tracks Honua-as-client reads against reference GeoServer and MapServer sources for WMS 1.3, WFS 2.0, and WMTS 1.0.

Source TRX: `tests/TestResults/cross-server-consume.trx`

| Outcome | Count |
|---|---:|
| Passing | 12 |
| Open gaps | 2 |
| Failures | 0 |

## Open Gaps

| Source | Protocol | Test | Details |
|---|---|---|---|
| MapServer | WMTS 1.0 | `WmtsGetTile MapServer ReturnsAdvertisedTile` | gap: camptocamp/mapserver:8.0 exposes WMS/WFS but does not include WMTS_SERVER support; add a MapCache-backed reference source for WMTS. |
| MapServer | WMTS 1.0 | `WmtsGetCapabilities MapServer ReturnsLayerDocument` | gap: camptocamp/mapserver:8.0 exposes WMS/WFS but does not include WMTS_SERVER support; add a MapCache-backed reference source for WMTS. |

## Failures

No failing consume tests were reported.

## Passing

| Source | Protocol | Test | Details |
|---|---|---|---|
| GeoServer | WFS 2.0 | `WfsGetFeature GeoServer ReturnsExpectedFeatures` | Passed |
| GeoServer | WMS 1.3 | `WmsGetMap GeoServer ReturnsImageForKnownLayer` | Passed |
| MapServer | WMS 1.3 | `WmsGetCapabilities MapServer ReturnsLayerDocument` | Passed |
| MapServer | WMS 1.3 | `WmsGetFeatureInfo MapServer ReturnsFeatureInfoPayload` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetTile GeoServer ReturnsAdvertisedTile` | Passed |
| MapServer | WFS 2.0 | `WfsGetCapabilities MapServer ReturnsFeatureTypeDocument` | Passed |
| GeoServer | WMS 1.3 | `WmsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| MapServer | WFS 2.0 | `WfsGetFeature MapServer ReturnsExpectedFeatures` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| MapServer | WMS 1.3 | `WmsGetMap MapServer ReturnsImageForKnownLayer` | Passed |
| GeoServer | WFS 2.0 | `WfsGetCapabilities GeoServer ReturnsFeatureTypeDocument` | Passed |
| GeoServer | WMS 1.3 | `WmsGetFeatureInfo GeoServer ReturnsFeatureInfoPayload` | Passed |
