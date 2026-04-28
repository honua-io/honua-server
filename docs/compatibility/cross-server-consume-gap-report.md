# Cross-Server Consume Gap Report

Last refreshed: 2026-04-28T09:25:09Z

This report is generated from the nightly cross-server consume suite. It tracks Honua-as-client reads against reference GeoServer, MapServer, and explicitly licensed ArcGIS Server sources for WMS 1.3, WFS 2.0, WMTS 1.0, and ArcGIS MapServer tile read paths.

Source TRX: `tests/TestResults/cross-server-consume.trx`

| Outcome | Count |
|---|---:|
| Passing | 12 |
| Open gaps | 2 |
| Configuration skips | 7 |
| Failures | 0 |

## Open Gaps

| Source | Protocol | Test | Details |
|---|---|---|---|
| MapServer | WMTS 1.0 | `WmtsGetCapabilities MapServer ReturnsLayerDocument` | gap: camptocamp/mapserver:8.0 exposes WMS/WFS but does not include WMTS_SERVER support; add a MapCache-backed reference source for WMTS. |
| MapServer | WMTS 1.0 | `WmtsGetTile MapServer ReturnsAdvertisedTile` | gap: camptocamp/mapserver:8.0 exposes WMS/WFS but does not include WMTS_SERVER support; add a MapCache-backed reference source for WMTS. |

## Configuration Skips

| Source | Protocol | Test | Details |
|---|---|---|---|
| ArcGIS Server | MapServer tile | `MapServerTile ArcGisServer ReturnsConfiguredTile` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_MAPSERVER_TILE_URL |
| ArcGIS Server | WMS 1.3 | `WmsGetCapabilities ArcGisServer ReturnsLayerDocument` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WMS_URL, HONUA_TEST_ARCGIS_WMS_LAYER |
| ArcGIS Server | WMTS 1.0 | `WmtsGetTile ArcGisServer ReturnsAdvertisedTile` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WMTS_URL, HONUA_TEST_ARCGIS_WMTS_LAYER |
| ArcGIS Server | WMTS 1.0 | `WmtsGetCapabilities ArcGisServer ReturnsLayerDocument` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WMTS_URL, HONUA_TEST_ARCGIS_WMTS_LAYER |
| ArcGIS Server | WMS 1.3 | `WmsGetMap ArcGisServer ReturnsImageForKnownLayer` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WMS_URL, HONUA_TEST_ARCGIS_WMS_LAYER, HONUA_TEST_ARCGIS_WMS_BBOX |
| ArcGIS Server | WFS 2.0 | `WfsGetCapabilities ArcGisServer ReturnsFeatureTypeDocument` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WFS_URL, HONUA_TEST_ARCGIS_WFS_TYPENAME |
| ArcGIS Server | WFS 2.0 | `WfsGetFeature ArcGisServer ReturnsExpectedFeatures` | Missing required environment variables: HONUA_TEST_ARCGIS_SERVER_CONSUME, HONUA_TEST_ARCGIS_WFS_URL, HONUA_TEST_ARCGIS_WFS_TYPENAME |

## Failures

No failing consume tests were reported.

## Passing

| Source | Protocol | Test | Details |
|---|---|---|---|
| MapServer | WFS 2.0 | `WfsGetFeature MapServer ReturnsExpectedFeatures` | Passed |
| GeoServer | WFS 2.0 | `WfsGetFeature GeoServer ReturnsExpectedFeatures` | Passed |
| MapServer | WMS 1.3 | `WmsGetMap MapServer ReturnsImageForKnownLayer` | Passed |
| GeoServer | WMS 1.3 | `WmsGetFeatureInfo GeoServer ReturnsFeatureInfoPayload` | Passed |
| MapServer | WMS 1.3 | `WmsGetFeatureInfo MapServer ReturnsFeatureInfoPayload` | Passed |
| GeoServer | WMS 1.3 | `WmsGetMap GeoServer ReturnsImageForKnownLayer` | Passed |
| MapServer | WFS 2.0 | `WfsGetCapabilities MapServer ReturnsFeatureTypeDocument` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetTile GeoServer ReturnsAdvertisedTile` | Passed |
| GeoServer | WMTS 1.0 | `WmtsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| GeoServer | WMS 1.3 | `WmsGetCapabilities GeoServer ReturnsLayerDocument` | Passed |
| GeoServer | WFS 2.0 | `WfsGetCapabilities GeoServer ReturnsFeatureTypeDocument` | Passed |
| MapServer | WMS 1.3 | `WmsGetCapabilities MapServer ReturnsLayerDocument` | Passed |
