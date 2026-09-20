# Cross-Client Certification Gap Report

> **Auto-generated — do not hand-edit.** Refreshed nightly by the
> `client-interop-nightly` workflow; any manual changes will be overwritten on
> the next run.

_Generated: 2026-05-17T09:16:45.557558+00:00_

This report is auto-refreshed by the `client-interop-nightly` workflow.
It compares the latest `.cert.json` envelopes from each Docker client lane
against the committed baselines under `tests/baselines/client-compat/`.

## Lane coverage summary

| Lane | Protocol | Total | Pass | Fail | Skip | N/A |
|------|----------|-------|------|------|------|-----|
| arcgis-stub ⚠ no current run | featureserver | 24 | 14 | 0 | 9 | 1 |
| arcgis-stub ⚠ no current run | mapserver | 24 | 14 | 0 | 9 | 1 |
| cli ⚠ no current run | ogc-features | 24 | 4 | 0 | 0 | 20 |
| cli ⚠ no current run | wfs | 24 | 4 | 0 | 0 | 20 |
| desktop-qgis ⚠ no current run | ogc-features | 21 | 18 | 0 | 3 | 0 |
| desktop-qgis ⚠ no current run | wfs | 16 | 12 | 0 | 4 | 0 |
| js ⚠ no current run | mvt | 24 | 2 | 0 | 10 | 12 |
| js ⚠ no current run | ogc-features | 24 | 6 | 0 | 16 | 2 |
| js ⚠ no current run | ogc-maps | 24 | 5 | 0 | 3 | 16 |
| js ⚠ no current run | wfs | 24 | 5 | 0 | 6 | 13 |
| js ⚠ no current run | wms | 24 | 4 | 0 | 4 | 16 |
| js ⚠ no current run | wmts | 24 | 4 | 0 | 4 | 16 |
| js-cesium ⚠ no current run | ogc-maps | 24 | 2 | 0 | 6 | 16 |
| js-cesium ⚠ no current run | ogc-tiles | 24 | 3 | 0 | 5 | 16 |
| js-cesium ⚠ no current run | wms | 24 | 4 | 0 | 4 | 16 |
| js-cesium ⚠ no current run | wmts | 24 | 3 | 0 | 5 | 16 |

## Missing from current run (373)

| Lane | Protocol | Test case | Baseline | Current | Notes |
|------|----------|-----------|----------|---------|-------|
| arcgis-stub | featureserver | CERT-AUTH-01 | pass | — | HTTP 200 |
| arcgis-stub | featureserver | CERT-AUTH-02 | skip | — | Credential exchange covered by other lanes; stub does not authenticate. |
| arcgis-stub | featureserver | CERT-CONN-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-CONN-02 | skip | — | TLS termination occurs in front of the test docker network; not exercised by stub. |
| arcgis-stub | featureserver | CERT-DISC-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-DISC-02 | pass | — | HTTP 200 |
| arcgis-stub | featureserver | CERT-ERRH-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-ERRH-02 | pass | — | Server may accept the where clause and return zero results; both outcomes acceptable. |
| arcgis-stub | featureserver | CERT-GEOM-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-GEOM-02 | pass | — |  |
| arcgis-stub | featureserver | CERT-PAGE-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-PAGE-02 | pass | — |  |
| arcgis-stub | featureserver | CERT-QFLT-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-QFLT-02 | pass | — | HTTP 200 |
| arcgis-stub | featureserver | CERT-RNDR-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-02 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-FIL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-LBL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-LIN-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-SPR-01 | not-applicable | — |  |
| arcgis-stub | featureserver | CERT-RNDR-SYM-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-RNDR-URL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | featureserver | CERT-SCHM-01 | pass | — |  |
| arcgis-stub | featureserver | CERT-SCHM-02 | pass | — |  |
| arcgis-stub | mapserver | CERT-AUTH-01 | pass | — | HTTP 200 |
| arcgis-stub | mapserver | CERT-AUTH-02 | skip | — | Credential exchange covered by other lanes; stub does not authenticate. |
| arcgis-stub | mapserver | CERT-CONN-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-CONN-02 | skip | — | TLS termination occurs in front of the test docker network; not exercised by stub. |
| arcgis-stub | mapserver | CERT-DISC-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-DISC-02 | pass | — | HTTP 200 |
| arcgis-stub | mapserver | CERT-ERRH-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-ERRH-02 | pass | — | Server may accept the where clause and return zero results; both outcomes acceptable. |
| arcgis-stub | mapserver | CERT-GEOM-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-GEOM-02 | pass | — |  |
| arcgis-stub | mapserver | CERT-PAGE-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-PAGE-02 | pass | — |  |
| arcgis-stub | mapserver | CERT-QFLT-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-QFLT-02 | pass | — | HTTP 200 |
| arcgis-stub | mapserver | CERT-RNDR-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-02 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-FIL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-LBL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-LIN-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-SPR-01 | not-applicable | — |  |
| arcgis-stub | mapserver | CERT-RNDR-SYM-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-RNDR-URL-01 | skip | — | pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run. |
| arcgis-stub | mapserver | CERT-SCHM-01 | pass | — |  |
| arcgis-stub | mapserver | CERT-SCHM-02 | pass | — |  |
| cli | ogc-features | CERT-AUTH-01 | not-applicable | — |  |
| cli | ogc-features | CERT-AUTH-02 | not-applicable | — |  |
| cli | ogc-features | CERT-CONN-01 | pass | — |  |
| cli | ogc-features | CERT-CONN-02 | not-applicable | — |  |
| cli | ogc-features | CERT-DISC-01 | pass | — |  |
| cli | ogc-features | CERT-DISC-02 | not-applicable | — |  |
| cli | ogc-features | CERT-ERRH-01 | not-applicable | — |  |
| cli | ogc-features | CERT-ERRH-02 | not-applicable | — |  |
| cli | ogc-features | CERT-GEOM-01 | not-applicable | — |  |
| cli | ogc-features | CERT-GEOM-02 | not-applicable | — |  |
| cli | ogc-features | CERT-PAGE-01 | not-applicable | — |  |
| cli | ogc-features | CERT-PAGE-02 | not-applicable | — |  |
| cli | ogc-features | CERT-QFLT-01 | pass | — |  |
| cli | ogc-features | CERT-QFLT-02 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-02 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-FIL-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-LBL-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-LIN-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-SPR-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-SYM-01 | not-applicable | — |  |
| cli | ogc-features | CERT-RNDR-URL-01 | not-applicable | — |  |
| cli | ogc-features | CERT-SCHM-01 | pass | — |  |
| cli | ogc-features | CERT-SCHM-02 | not-applicable | — |  |
| cli | wfs | CERT-AUTH-01 | not-applicable | — |  |
| cli | wfs | CERT-AUTH-02 | not-applicable | — |  |
| cli | wfs | CERT-CONN-01 | pass | — |  |
| cli | wfs | CERT-CONN-02 | not-applicable | — |  |
| cli | wfs | CERT-DISC-01 | pass | — |  |
| cli | wfs | CERT-DISC-02 | not-applicable | — |  |
| cli | wfs | CERT-ERRH-01 | not-applicable | — |  |
| cli | wfs | CERT-ERRH-02 | not-applicable | — |  |
| cli | wfs | CERT-GEOM-01 | not-applicable | — |  |
| cli | wfs | CERT-GEOM-02 | not-applicable | — |  |
| cli | wfs | CERT-PAGE-01 | not-applicable | — |  |
| cli | wfs | CERT-PAGE-02 | not-applicable | — |  |
| cli | wfs | CERT-QFLT-01 | pass | — |  |
| cli | wfs | CERT-QFLT-02 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-02 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-FIL-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-LBL-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-LIN-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-SPR-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-SYM-01 | not-applicable | — |  |
| cli | wfs | CERT-RNDR-URL-01 | not-applicable | — |  |
| cli | wfs | CERT-SCHM-01 | pass | — |  |
| cli | wfs | CERT-SCHM-02 | not-applicable | — |  |
| desktop-qgis | ogc-features | CERT-AUTH-01 | skip | — | client-compat-v1.sql seed allows anonymous access; auth rejection not exercised. |
| desktop-qgis | ogc-features | CERT-AUTH-02 | skip | — | client-compat-v1.sql seed allows anonymous access; credential grant not exercised. |
| desktop-qgis | ogc-features | CERT-CONN-01 | pass | — | OAPIF provider connected and layer loaded successfully. |
| desktop-qgis | ogc-features | CERT-CONN-02 | skip | — | Compatibility seed runs on HTTP-only localhost; TLS not exercised. |
| desktop-qgis | ogc-features | CERT-DISC-01 | pass | — | Collection 0 discovered with 10 features. |
| desktop-qgis | ogc-features | CERT-DISC-02 | pass | — | Extent: -122.5000000000000000,37.7000000000000028 : -122.3499999999999943,37.8400000000000034 |
| desktop-qgis | ogc-features | CERT-ERRH-01 | pass | — | Invalid collection rejected:  |
| desktop-qgis | ogc-features | CERT-ERRH-02 | pass | — | Malformed filter correctly returned zero features. |
| desktop-qgis | ogc-features | CERT-GEOM-01 | pass | — | Alpha at (-122.49, 37.71), delta=0.0. |
| desktop-qgis | ogc-features | CERT-GEOM-02 | pass | — | CRS: EPSG:4326 |
| desktop-qgis | ogc-features | CERT-PAGE-01 | pass | — | Server returned 3 features for limit=3 (first page). QGIS auto-pagination yielded 10 total. |
| desktop-qgis | ogc-features | CERT-PAGE-02 | pass | — | Server pages 1 and 2 (limit=3) returned disjoint feature sets. QGIS delivered 10 unique features. |
| desktop-qgis | ogc-features | CERT-QFLT-01 | pass | — | Attribute filter active=true returned 5 features. |
| desktop-qgis | ogc-features | CERT-QFLT-02 | pass | — | Bbox filter returned 4 features (total 10). |
| desktop-qgis | ogc-features | CERT-RNDR-01 | pass | — | Headless render produced 1464 byte PNG. |
| desktop-qgis | ogc-features | CERT-RNDR-02 | pass | — | Post-reload render: 1489 byte PNG (pre-reload: 1489 bytes). |
| desktop-qgis | ogc-features | CERT-RNDR-FIL-01 | pass | — | Marker fill produced 7858 pixels matching the declared fill color (30, 100, 200) (tolerance 35). Substantiated via marker fill until the polygon-geometry fixtur |
| desktop-qgis | ogc-features | CERT-RNDR-LIN-01 | pass | — | Marker outline produced 7182 pixels matching the declared stroke color (26, 26, 46) (tolerance 35). Substantiated via marker outline until the line-geometry fix |
| desktop-qgis | ogc-features | CERT-RNDR-SYM-01 | pass | — | QgsMarkerSymbol render produced 2160 pixels matching the declared symbol color (30, 100, 200) (tolerance 35). |
| desktop-qgis | ogc-features | CERT-SCHM-01 | pass | — | All 12 expected fields present. |
| desktop-qgis | ogc-features | CERT-SCHM-02 | pass | — | Geometry type: Point |
| desktop-qgis | wfs | CERT-AUTH-01 | skip | — | client-compat-v1.sql seed allows anonymous access; auth rejection not exercised. |
| desktop-qgis | wfs | CERT-AUTH-02 | skip | — | client-compat-v1.sql seed allows anonymous access; credential grant not exercised. |
| desktop-qgis | wfs | CERT-CONN-01 | pass | — | WFS provider connected and layer loaded successfully. |
| desktop-qgis | wfs | CERT-CONN-02 | skip | — | Compatibility seed runs on HTTP-only localhost; TLS not exercised. |
| desktop-qgis | wfs | CERT-DISC-01 | pass | — | WFS typename honua:test_layer discovered with 10 features. |
| desktop-qgis | wfs | CERT-DISC-02 | pass | — | Extent: -122.5000000000000000,37.7000000000000028 : -122.3499999999999943,37.8400000000000034 |
| desktop-qgis | wfs | CERT-ERRH-01 | pass | — | Invalid typename rejected:  |
| desktop-qgis | wfs | CERT-ERRH-02 | pass | — | Malformed filter correctly returned zero features. |
| desktop-qgis | wfs | CERT-GEOM-01 | pass | — | Alpha at (-122.49, 37.71), delta=0.0. |
| desktop-qgis | wfs | CERT-GEOM-02 | pass | — | CRS: EPSG:4326 |
| desktop-qgis | wfs | CERT-PAGE-01 | skip | — | Server first page correct (3 features), but QGIS returned only 3 — auto-pagination not supported. |
| desktop-qgis | wfs | CERT-PAGE-02 | pass | — | Server pages 1 and 2 (COUNT=3) returned disjoint feature sets. QGIS delivered 3 unique features. |
| desktop-qgis | wfs | CERT-QFLT-01 | pass | — | Attribute filter active=true returned 5 features. |
| desktop-qgis | wfs | CERT-QFLT-02 | pass | — | Bbox filter returned 4 features (total 10). |
| desktop-qgis | wfs | CERT-SCHM-01 | pass | — | All 12 expected fields present. |
| desktop-qgis | wfs | CERT-SCHM-02 | pass | — | Geometry type: Point |
| js | mvt | CERT-AUTH-01 | skip | — | Covered by JS/featureserver automated tests. |
| js | mvt | CERT-AUTH-02 | skip | — | Covered by JS/featureserver automated tests. |
| js | mvt | CERT-CONN-01 | pass | — |  |
| js | mvt | CERT-CONN-02 | skip | — | Covered by JS/featureserver automated tests. |
| js | mvt | CERT-DISC-01 | not-applicable | — |  |
| js | mvt | CERT-DISC-02 | not-applicable | — |  |
| js | mvt | CERT-ERRH-01 | skip | — | Covered by JS/featureserver automated tests. |
| js | mvt | CERT-ERRH-02 | not-applicable | — |  |
| js | mvt | CERT-GEOM-01 | not-applicable | — |  |
| js | mvt | CERT-GEOM-02 | not-applicable | — |  |
| js | mvt | CERT-PAGE-01 | not-applicable | — |  |
| js | mvt | CERT-PAGE-02 | not-applicable | — |  |
| js | mvt | CERT-QFLT-01 | not-applicable | — |  |
| js | mvt | CERT-QFLT-02 | not-applicable | — |  |
| js | mvt | CERT-RNDR-01 | pass | — |  |
| js | mvt | CERT-RNDR-02 | not-applicable | — |  |
| js | mvt | CERT-RNDR-FIL-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-RNDR-LBL-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-RNDR-LIN-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-RNDR-SPR-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-RNDR-SYM-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-RNDR-URL-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md |
| js | mvt | CERT-SCHM-01 | not-applicable | — |  |
| js | mvt | CERT-SCHM-02 | not-applicable | — |  |
| js | ogc-features | CERT-AUTH-01 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-AUTH-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-CONN-01 | pass | — | OGC Features landing page returns links array |
| js | ogc-features | CERT-CONN-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-DISC-01 | pass | — | 26 conformance classes returned |
| js | ogc-features | CERT-DISC-02 | pass | — | Collection '2000' metadata retrieved |
| js | ogc-features | CERT-ERRH-01 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-ERRH-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-GEOM-01 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-GEOM-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-PAGE-01 | pass | — | limit=2 honoured |
| js | ogc-features | CERT-PAGE-02 | pass | — | offset=2 returned different feature set |
| js | ogc-features | CERT-QFLT-01 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-QFLT-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-RNDR-01 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-RNDR-02 | skip | — | Not covered by this test suite |
| js | ogc-features | CERT-RNDR-FIL-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by this lane; tracked in visual-style-certification-slice.md |
| js | ogc-features | CERT-RNDR-LBL-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by this lane; tracked in visual-style-certification-slice.md |
| js | ogc-features | CERT-RNDR-LIN-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by this lane; tracked in visual-style-certification-slice.md |
| js | ogc-features | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js | ogc-features | CERT-RNDR-SYM-01 | skip | — | pending-fixture: visual / style slice ID not yet substantiated by this lane; tracked in visual-style-certification-slice.md |
| js | ogc-features | CERT-RNDR-URL-01 | not-applicable | — |  |
| js | ogc-features | CERT-SCHM-01 | pass | — | Queryables JSON Schema returned |
| js | ogc-features | CERT-SCHM-02 | skip | — | Not covered by this test suite |
| js | ogc-maps | CERT-AUTH-01 | skip | — | Not covered by this test suite |
| js | ogc-maps | CERT-AUTH-02 | skip | — | Not covered by this test suite |
| js | ogc-maps | CERT-CONN-01 | pass | — | OGC API Maps landing page returned links array |
| js | ogc-maps | CERT-CONN-02 | skip | — | Not covered by this test suite |
| js | ogc-maps | CERT-DISC-01 | pass | — | 10 OGC API Maps conformance classes returned |
| js | ogc-maps | CERT-DISC-02 | pass | — | OGC API Maps OpenAPI advertises collection map route |
| js | ogc-maps | CERT-ERRH-01 | pass | — | Unsupported OGC API Maps image format returned JSON error response |
| js | ogc-maps | CERT-ERRH-02 | not-applicable | — |  |
| js | ogc-maps | CERT-GEOM-01 | not-applicable | — |  |
| js | ogc-maps | CERT-GEOM-02 | not-applicable | — |  |
| js | ogc-maps | CERT-PAGE-01 | not-applicable | — |  |
| js | ogc-maps | CERT-PAGE-02 | not-applicable | — |  |
| js | ogc-maps | CERT-QFLT-01 | not-applicable | — |  |
| js | ogc-maps | CERT-QFLT-02 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-01 | pass | — | OGC API Maps rendered image/png for collection '2000' |
| js | ogc-maps | CERT-RNDR-02 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js | ogc-maps | CERT-RNDR-URL-01 | not-applicable | — |  |
| js | ogc-maps | CERT-SCHM-01 | not-applicable | — |  |
| js | ogc-maps | CERT-SCHM-02 | not-applicable | — |  |
| js | wfs | CERT-AUTH-01 | skip | — | Not covered by this test suite |
| js | wfs | CERT-AUTH-02 | skip | — | Not covered by this test suite |
| js | wfs | CERT-CONN-01 | skip | — | Not covered by this test suite |
| js | wfs | CERT-CONN-02 | skip | — | Not covered by this test suite |
| js | wfs | CERT-DISC-01 | pass | — | GetCapabilities returned valid WFS_Capabilities XML |
| js | wfs | CERT-DISC-02 | pass | — | 4 FeatureType(s) found, first: honua:test_layer |
| js | wfs | CERT-ERRH-01 | skip | — | Not covered by this test suite |
| js | wfs | CERT-ERRH-02 | skip | — | Not covered by this test suite |
| js | wfs | CERT-GEOM-01 | pass | — | 5/5 features have geometry, first type: Point |
| js | wfs | CERT-GEOM-02 | not-applicable | — |  |
| js | wfs | CERT-PAGE-01 | not-applicable | — |  |
| js | wfs | CERT-PAGE-02 | not-applicable | — |  |
| js | wfs | CERT-QFLT-01 | pass | — | GetFeature for 'honua:test_layer' returned 5043 bytes |
| js | wfs | CERT-QFLT-02 | not-applicable | — |  |
| js | wfs | CERT-RNDR-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-02 | not-applicable | — |  |
| js | wfs | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js | wfs | CERT-RNDR-URL-01 | not-applicable | — |  |
| js | wfs | CERT-SCHM-01 | pass | — | Feature has 14 properties: shape, objectid, name, description, status, count, ratio, active, created_at, event_date, event_time, uid, tags, numbers |
| js | wfs | CERT-SCHM-02 | not-applicable | — |  |
| js | wms | CERT-AUTH-01 | skip | — | Not covered by this test suite |
| js | wms | CERT-AUTH-02 | skip | — | Not covered by this test suite |
| js | wms | CERT-CONN-01 | pass | — | WMS GetCapabilities returned XML |
| js | wms | CERT-CONN-02 | skip | — | Not covered by this test suite |
| js | wms | CERT-DISC-01 | pass | — | ol/format/WMSCapabilities parsed WMS 1.3.0 metadata |
| js | wms | CERT-DISC-02 | pass | — | 3 named WMS layer(s) discovered |
| js | wms | CERT-ERRH-01 | skip | — | Not covered by this test suite |
| js | wms | CERT-ERRH-02 | not-applicable | — |  |
| js | wms | CERT-GEOM-01 | not-applicable | — |  |
| js | wms | CERT-GEOM-02 | not-applicable | — |  |
| js | wms | CERT-PAGE-01 | not-applicable | — |  |
| js | wms | CERT-PAGE-02 | not-applicable | — |  |
| js | wms | CERT-QFLT-01 | not-applicable | — |  |
| js | wms | CERT-QFLT-02 | not-applicable | — |  |
| js | wms | CERT-RNDR-01 | pass | — | WMS GetMap rendered image/png for layer 'Browser Points' |
| js | wms | CERT-RNDR-02 | not-applicable | — |  |
| js | wms | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js | wms | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js | wms | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js | wms | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js | wms | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js | wms | CERT-RNDR-URL-01 | not-applicable | — |  |
| js | wms | CERT-SCHM-01 | not-applicable | — |  |
| js | wms | CERT-SCHM-02 | not-applicable | — |  |
| js | wmts | CERT-AUTH-01 | skip | — | Not covered by this test suite |
| js | wmts | CERT-AUTH-02 | skip | — | Not covered by this test suite |
| js | wmts | CERT-CONN-01 | pass | — | WMTS GetCapabilities returned XML |
| js | wmts | CERT-CONN-02 | skip | — | Not covered by this test suite |
| js | wmts | CERT-DISC-01 | pass | — | ol/format/WMTSCapabilities parsed WMTS 1.0.0 metadata |
| js | wmts | CERT-DISC-02 | pass | — | 3 WMTS layer(s), 1 tile matrix set(s) discovered |
| js | wmts | CERT-ERRH-01 | skip | — | Not covered by this test suite |
| js | wmts | CERT-ERRH-02 | not-applicable | — |  |
| js | wmts | CERT-GEOM-01 | not-applicable | — |  |
| js | wmts | CERT-GEOM-02 | not-applicable | — |  |
| js | wmts | CERT-PAGE-01 | not-applicable | — |  |
| js | wmts | CERT-PAGE-02 | not-applicable | — |  |
| js | wmts | CERT-QFLT-01 | not-applicable | — |  |
| js | wmts | CERT-QFLT-02 | not-applicable | — |  |
| js | wmts | CERT-RNDR-01 | pass | — | WMTS GetTile rendered image/png for layer '2000' and matrix set 'WebMercatorQuad' |
| js | wmts | CERT-RNDR-02 | not-applicable | — |  |
| js | wmts | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js | wmts | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js | wmts | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js | wmts | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js | wmts | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js | wmts | CERT-RNDR-URL-01 | not-applicable | — |  |
| js | wmts | CERT-SCHM-01 | not-applicable | — |  |
| js | wmts | CERT-SCHM-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-AUTH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-AUTH-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-CONN-01 | pass | — |  |
| js-cesium | ogc-maps | CERT-CONN-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-DISC-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-DISC-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-ERRH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-maps | CERT-ERRH-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-GEOM-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-GEOM-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-PAGE-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-PAGE-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-QFLT-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-QFLT-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-01 | pass | — |  |
| js-cesium | ogc-maps | CERT-RNDR-02 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-RNDR-URL-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-SCHM-01 | not-applicable | — |  |
| js-cesium | ogc-maps | CERT-SCHM-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-AUTH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-tiles | CERT-AUTH-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-tiles | CERT-CONN-01 | pass | — |  |
| js-cesium | ogc-tiles | CERT-CONN-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-tiles | CERT-DISC-01 | pass | — |  |
| js-cesium | ogc-tiles | CERT-DISC-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-tiles | CERT-ERRH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | ogc-tiles | CERT-ERRH-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-GEOM-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-GEOM-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-PAGE-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-PAGE-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-QFLT-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-QFLT-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-01 | pass | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-02 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-RNDR-URL-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-SCHM-01 | not-applicable | — |  |
| js-cesium | ogc-tiles | CERT-SCHM-02 | not-applicable | — |  |
| js-cesium | wms | CERT-AUTH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wms | CERT-AUTH-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wms | CERT-CONN-01 | pass | — |  |
| js-cesium | wms | CERT-CONN-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wms | CERT-DISC-01 | pass | — |  |
| js-cesium | wms | CERT-DISC-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wms | CERT-ERRH-01 | pass | — |  |
| js-cesium | wms | CERT-ERRH-02 | not-applicable | — |  |
| js-cesium | wms | CERT-GEOM-01 | not-applicable | — |  |
| js-cesium | wms | CERT-GEOM-02 | not-applicable | — |  |
| js-cesium | wms | CERT-PAGE-01 | not-applicable | — |  |
| js-cesium | wms | CERT-PAGE-02 | not-applicable | — |  |
| js-cesium | wms | CERT-QFLT-01 | not-applicable | — |  |
| js-cesium | wms | CERT-QFLT-02 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-01 | pass | — |  |
| js-cesium | wms | CERT-RNDR-02 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js-cesium | wms | CERT-RNDR-URL-01 | not-applicable | — |  |
| js-cesium | wms | CERT-SCHM-01 | not-applicable | — |  |
| js-cesium | wms | CERT-SCHM-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-AUTH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wmts | CERT-AUTH-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wmts | CERT-CONN-01 | pass | — |  |
| js-cesium | wmts | CERT-CONN-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wmts | CERT-DISC-01 | pass | — |  |
| js-cesium | wmts | CERT-DISC-02 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wmts | CERT-ERRH-01 | skip | — | Not exercised by Cesium imagery lane for this protocol. |
| js-cesium | wmts | CERT-ERRH-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-GEOM-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-GEOM-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-PAGE-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-PAGE-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-QFLT-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-QFLT-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-01 | pass | — |  |
| js-cesium | wmts | CERT-RNDR-02 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-FIL-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-LBL-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-LIN-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-SPR-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-SYM-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-RNDR-URL-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-SCHM-01 | not-applicable | — |  |
| js-cesium | wmts | CERT-SCHM-02 | not-applicable | — |  |
