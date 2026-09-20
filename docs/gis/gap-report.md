# Cross-Client Certification Gap Report

_Generated: 2026-09-19T15:36:27.512724+00:00_

This report is auto-refreshed by the `client-interop-nightly` workflow.
It compares the latest `.cert.json` envelopes from each Docker client lane
against the committed baselines under `tests/baselines/client-compat/`.

## Lane coverage summary

| Lane | Protocol | Total | Pass | Fail | Skip | N/A |
|------|----------|-------|------|------|------|-----|
| desktop-qgis ⚠ no current run | 3d-tiles | 2 | 2 | 0 | 0 | 0 |
| desktop-qgis | cog | 4 | 4 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | featureserver | 10 | 6 | 0 | 0 | 4 |
| desktop-qgis ⚠ no current run | mapserver | 4 | 4 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | ogc-api-styles | 3 | 3 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | ogc-features | 24 | 21 | 0 | 3 | 0 |
| desktop-qgis ⚠ no current run | pmtiles | 6 | 6 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | sensorthings | 3 | 3 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | stac | 4 | 4 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | vectortileserver | 3 | 3 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | wcs | 8 | 8 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | wfs | 19 | 15 | 0 | 4 | 0 |
| desktop-qgis ⚠ no current run | wms | 9 | 9 | 0 | 0 | 0 |
| desktop-qgis ⚠ no current run | wmts | 7 | 7 | 0 | 0 | 0 |

## Missing from current run (102)

| Lane | Protocol | Test case | Baseline | Current | Notes |
|------|----------|-----------|----------|---------|-------|
| desktop-qgis | 3d-tiles | NB-PQG-3DT-01 | pass | — | QgsTiledSceneLayer loaded /scenes/cert-browser-polygons/tileset.json through the cesiumtiles provider: scene CRS EPSG:4978, layer extent -122.4250,37.7700 : -12 |
| desktop-qgis | 3d-tiles | NB-PQG-3DT-02 | pass | — | 1 content tile(s) enumerated from the cesiumtiles index at geometric error 0 and fetched through retrieveContent; each is glTF binary ([1420] bytes), addressed  |
| desktop-qgis | featureserver | CERT-AUTH-01 | pass | — | applyEdits insert from the QGIS arcgisfeatureserver provider landed and was assigned objectid 48, confirmed by an independent query; the request carried the exp |
| desktop-qgis | featureserver | CERT-CONN-01 | not-applicable | — | No client path: the arcgisfeatureserver provider exposes no createReplica or synchronizeReplica API - QGIS implements offline work through its own Offline Editi |
| desktop-qgis | featureserver | CERT-CONN-02 | not-applicable | — | No client path: neither QgsVectorLayer nor the arcgisfeatureserver provider exposes any attachment API, and QGIS 3.44 manual 11.1.7.3 documents none. The fixtur |
| desktop-qgis | featureserver | CERT-DISC-01 | pass | — | AFS data item provider read /rest/services and /test_service/FeatureServer?f=json and enumerated ['Test Layer', 'WFS-T Insert Scratch', 'WFS-T Update Scratch',  |
| desktop-qgis | featureserver | CERT-DISC-02 | not-applicable | — | No client path: the arcgisfeatureserver provider exposes no queryRelatedRecords or relationship API and discoverRelations() returns []; QGIS 3.44 manual 11.1.7. |
| desktop-qgis | featureserver | CERT-GEOM-01 | pass | — | Envelope query identified 'pt-alpha' via esriGeometryEnvelope/esriSpatialRelEnvelopeIntersects; an empty envelope correctly returned nothing. |
| desktop-qgis | featureserver | CERT-QFLT-01 | pass | — | Subset string "status = 'active'" was pushed down as a where= query parameter and returned 5 of 10 features. |
| desktop-qgis | featureserver | CERT-QFLT-02 | not-applicable | — | No client path: the server answers outStatistics correctly, but the arcgisfeatureserver provider computed min/max/unique/Sum locally from a full outFields=* dow |
| desktop-qgis | featureserver | CERT-SCHM-01 | pass | — | Layer document parsed: 14 fields, esriGeometryPoint -> point, wkid 4326, extent honoured; an unpublished layer id yielded an invalid layer. |
| desktop-qgis | featureserver | CERT-SCHM-02 | pass | — | coded-value domain StatusDomain on test_service/0.status reached QGIS as a ValueMap widget offering exactly the 3 published codes; queryDomains answers the same |
| desktop-qgis | mapserver | CERT-CONN-01 | pass | — | MapServer info parsed by the arcgismapserver provider: extent matched the advertised fullExtent and CRS resolved to EPSG:4326. |
| desktop-qgis | mapserver | CERT-RNDR-01 | pass | — | export decoded through the arcgismapserver provider for all three sublayers with distinct opaque-pixel counts {'2000': 44, '2001': 258, '2002': 1882} at 256x256 |
| desktop-qgis | mapserver | CERT-RNDR-URL-01 | pass | — | legend composed a 96x64 image with 165 distinct colours and 1869 opaque pixels through the arcgismapserver provider's legend fetcher. |
| desktop-qgis | mapserver | CERT-SCHM-01 | pass | — | identify returned 3 feature(s) through the arcgismapserver provider including 'pt-alpha' (objectid 13), and nothing at an empty location. |
| desktop-qgis | ogc-api-styles | CERT-DISC-01 | pass | — | 8 styles advertised at /ogc/styles; 'Test Layer' among them. |
| desktop-qgis | ogc-api-styles | CERT-ERRH-01 | pass | — | An unpublished style id returned 404 while the advertised one served 200. |
| desktop-qgis | ogc-api-styles | CERT-RNDR-SYM-01 | pass | — | SLD 1.0 fetched by content negotiation and applied via loadSldStyle; renderer fill became (45, 105, 165) alpha 216, the server's published symbology rather than |
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
| desktop-qgis | ogc-features | CERT-RNDR-01 | pass | — | Headless render produced 1453 byte PNG. |
| desktop-qgis | ogc-features | CERT-RNDR-02 | pass | — | Post-reload render: 1488 byte PNG (pre-reload: 1488 bytes). |
| desktop-qgis | ogc-features | CERT-RNDR-FIL-01 | pass | — | Marker fill produced 7858 pixels matching the declared fill color (30, 100, 200) (tolerance 35). Substantiated via marker fill until the polygon-geometry fixtur |
| desktop-qgis | ogc-features | CERT-RNDR-LIN-01 | pass | — | Marker outline produced 7182 pixels matching the declared stroke color (26, 26, 46) (tolerance 35). Substantiated via marker outline until the line-geometry fix |
| desktop-qgis | ogc-features | CERT-RNDR-SYM-01 | pass | — | QgsMarkerSymbol render produced 2160 pixels matching the declared symbol color (30, 100, 200) (tolerance 35). |
| desktop-qgis | ogc-features | CERT-SCHM-01 | pass | — | All 12 expected fields present. |
| desktop-qgis | ogc-features | CERT-SCHM-02 | pass | — | Geometry type: Point |
| desktop-qgis | ogc-features | NB-PQG-OAPIFT-01 | pass | — | OGC API Features Part 4 create through the QGIS OAPIF provider added 'pyqgis-oapift-create', confirmed by an independent /items read rather than by commitChange |
| desktop-qgis | ogc-features | NB-PQG-OAPIFT-02 | pass | — | OGC API Features Part 4 update rewrote 'name' from 'pyqgis-oapift-update-seed' to 'pyqgis-oapift-update' in place, confirmed server-side, with no extra row crea |
| desktop-qgis | ogc-features | NB-PQG-OAPIFT-03 | pass | — | OGC API Features Part 4 delete removed 'pyqgis-oapift-delete' and left every other row in place, confirmed server-side. |
| desktop-qgis | pmtiles | CERT-CONN-01 | pass | — | QgsVectorLayer opened the range-proxied PMTiles archive through the stock ogr provider (GDAL PMTiles driver). |
| desktop-qgis | pmtiles | CERT-DISC-01 | pass | — | querySublayers resolved ['layer'] via provider(s) ['ogr']. |
| desktop-qgis | pmtiles | CERT-DISC-02 | pass | — | HEAD advertised Accept-Ranges: bytes over 6597 bytes; a ranged GET returned 206 with the PMTiles v3 magic. |
| desktop-qgis | pmtiles | CERT-ERRH-01 | pass | — | unpublished artifact returned 404 with a 0-byte body and yielded no usable layer, while the published archive opened in the same test. |
| desktop-qgis | pmtiles | CERT-GEOM-01 | pass | — | all 9 seeded points round-tripped in EPSG:3857; worst position error 0.0074 deg (delta). |
| desktop-qgis | pmtiles | CERT-SCHM-01 | pass | — | 15 attribute column(s) survived the MVT encode. |
| desktop-qgis | sensorthings | CERT-DISC-01 | pass | — | All 5 advertised entity sets resolved through the sensorthings provider with the seeded counts {'Thing': 1, 'Sensor': 1, 'ObservedProperty': 1, 'Datastream': 1, |
| desktop-qgis | sensorthings | CERT-PAGE-01 | pass | — | The provider followed @iot.nextLink to all 48 observations; $count=true reported 48, $top bounded the page and advertised a next link, and the final page return |
| desktop-qgis | sensorthings | CERT-SCHM-01 | pass | — | expandTo='Datastream' on Observation added ['Datastream_description', 'Datastream_id', 'Datastream_name', 'Datastream_observationType', 'Datastream_phenomenonTi |
| desktop-qgis | stac | CERT-CONN-01 | pass | — | QgsBlockingNetworkRequest + QgsStacParser parsed http://honua:5000/stac as a QgsStacCatalog id='honua-stac-catalog' stac_version=1.0.0, advertising 8 child coll |
| desktop-qgis | stac | CERT-DISC-01 | pass | — | QgsStacController.fetchCollections(http://honua:5000/stac/collections) returned 8 collections (numberReturned=8), covering all seeded ids ['0', '2000', '2001',  |
| desktop-qgis | stac | CERT-QFLT-01 | pass | — | QgsStacController.fetchItemCollection issued GET item-search against http://honua:5000/stac/search scoped to collection '0': limit=3 returned 3 of numberMatched |
| desktop-qgis | stac | CERT-RNDR-URL-01 | pass | — | Item 1 exposed asset 'geojson' (media=application/geo+json, roles=['data']) through QgsStacItem.assets(); QGIS downloaded http://host.docker.internal:18080/ogc/ |
| desktop-qgis | vectortileserver | CERT-CONN-02 | pass | — | VectorTileServer info parsed by the arcgisvectortileservice provider for browser_compat: zoom 0..18. |
| desktop-qgis | vectortileserver | CERT-RNDR-02 | pass | — | VectorTileServer served a 128-byte .pbf at 12/1583/655 and the arcgisvectortileservice provider rendered 31 opaque pixels over the seeded cluster. |
| desktop-qgis | vectortileserver | CERT-RNDR-SYM-01 | pass | — | The service style (1 style layers) converted into 3 QGIS vector tile style(s) through the arcgisvectortileservice provider. |
| desktop-qgis | wcs | CERT-CONN-01 | pass | — | QgsRasterLayer opened the coverage through the stock wcs provider. |
| desktop-qgis | wcs | CERT-DISC-01 | pass | — | GridEnvelope resolved to 64x64. |
| desktop-qgis | wcs | CERT-DISC-02 | pass | — | DescribeCoverage yielded 1 band(s). |
| desktop-qgis | wcs | CERT-ERRH-01 | pass | — | Unknown coverage yielded an invalid layer rather than a silent empty one. |
| desktop-qgis | wcs | CERT-GEOM-01 | pass | — | Coverage extent and CRS agree with the seeded georeferencing. |
| desktop-qgis | wcs | CERT-QFLT-01 | pass | — | Corner landmarks differ as seeded: NW=10.0 SE=40.0. |
| desktop-qgis | wcs | CERT-RNDR-01 | pass | — | Provider returned a 32x32 block containing real pixel values. |
| desktop-qgis | wcs | CERT-SCHM-01 | pass | — | Band 1 data type reported as 6. |
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
| desktop-qgis | wfs | NB-PQG-WFST-01 | pass | — | WFS-T Insert through the QGIS provider created 'pyqgis-wfst-insert', confirmed by an independent OGC API Features read rather than by commitChanges(). Authentic |
| desktop-qgis | wfs | NB-PQG-WFST-02 | pass | — | WFS-T Update rewrote 'name' from 'pyqgis-wfst-update-seed' to 'pyqgis-wfst-update' in place, confirmed server-side, with no extra row created. |
| desktop-qgis | wfs | NB-PQG-WFST-03 | pass | — | WFS-T Delete removed 'pyqgis-wfst-delete' and left every other row intact, confirmed server-side. |
| desktop-qgis | wms | CERT-CONN-01 | pass | — | GetCapabilities parsed and the requested layer resolved. |
| desktop-qgis | wms | CERT-DISC-01 | pass | — | All three browser_compat layers resolved: ['Browser Points', 'Browser Lines', 'Browser Polygons']. |
| desktop-qgis | wms | CERT-ERRH-01 | pass | — | Unknown layer yielded an invalid layer rather than a silent blank. |
| desktop-qgis | wms | CERT-GEOM-01 | pass | — | Layer CRS resolved to EPSG:4326. |
| desktop-qgis | wms | CERT-QFLT-01 | pass | — | Advertised time Dimension '2024-01-01T12:00:00Z/2024-01-10T12:00:00Z/PT0S' accepted by QGIS as 1 available range(s); time-aware GetMap rendered. |
| desktop-qgis | wms | CERT-RNDR-01 | pass | — | GetMap returned a 256x256 block through the wms provider. |
| desktop-qgis | wms | CERT-RNDR-SYM-01 | pass | — | Style 'default' rendered through the provider; the server rejected an unadvertised style with 400. |
| desktop-qgis | wms | CERT-RNDR-URL-01 | pass | — | GetLegendGraphic returned a 122x32 legend image via the capabilities LegendURL. |
| desktop-qgis | wms | CERT-SCHM-01 | pass | — | GetFeatureInfo returned a provider-parsable identify result. |
| desktop-qgis | wmts | CERT-CONN-01 | pass | — | WMTS capabilities parsed; tileMatrixSet WebMercatorQuad selected. |
| desktop-qgis | wmts | CERT-DISC-01 | pass | — | Both WebMercatorQuad and WorldCRS84Quad resolved. |
| desktop-qgis | wmts | CERT-DISC-02 | pass | — | RESTful ResourceURL resolved and served 509 bytes. |
| desktop-qgis | wmts | CERT-ERRH-01 | pass | — | Unknown tileMatrixSet yielded an invalid layer rather than a blank one. |
| desktop-qgis | wmts | CERT-ERRH-02 | pass | — | Unadvertised layer identifier yielded an invalid layer. |
| desktop-qgis | wmts | CERT-RNDR-01 | pass | — | GetTile returned a 256x256 block through the tiled provider path. |
| desktop-qgis | wmts | CERT-SCHM-01 | pass | — | GetFeatureInfo identified 'pt-alpha' through the tiled provider path. |

