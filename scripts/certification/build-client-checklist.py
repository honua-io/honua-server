#!/usr/bin/env python3
"""Generate and validate the four-lane client certification checklist.

Writes ``docs/gis/data/client-certification-checklist.v1.json`` and the readable
projection ``docs/gis/CLIENT_CERTIFICATION_CHECKLIST.md``, then validates
fail-closed.

Why this exists: coverage was previously answered from recall and from
``expected-pairs.json``, which lists only the pairs wired into the evidence chain
(11 protocols) rather than the protocols that exist. That produced four wrong
"there is no lane for X" answers in a row. Here every cell carries a state, and
every state that is not a pass carries either an evidence reference or a citation,
so an unreachable cell is closed by proof rather than by assertion.

The two rules that make the target finishable:

* A cell closes as ``pass`` (evidence naming the client build) or ``n/a-*`` (a
  vendor-documentation or provider-registry citation). Nothing else counts.
* ``n/a`` needs operation-specific evidence covering the native paths in scope.
  A failed URI, missing fixture or incomplete module inventory cannot close it.

Run with --check to validate without writing (CI mode).
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DATA_PATH = REPO_ROOT / "docs" / "gis" / "data" / "client-certification-checklist.v1.json"
DOC_PATH = REPO_ROOT / "docs" / "gis" / "CLIENT_CERTIFICATION_CHECKLIST.md"

# The prose in DOC_PATH is hand-authored; only the region between these markers
# is generated, so the tables cannot drift from the data while the argument
# around them stays editable.
DOC_BEGIN = "<!-- BEGIN GENERATED TABLES -->"
DOC_END = "<!-- END GENERATED TABLES -->"

LANES = ("pro-ui", "arcpy", "qgis-ui", "pyqgis")

CLIENT_BUILDS = {
    "pro-ui": "ArcGIS Pro 3.7.1.1904",
    "arcpy": "ArcPy (ships with ArcGIS Pro 3.7.1.1904)",
    "qgis-ui": "QGIS 3.44.14 LTR",
    "pyqgis": "QGIS 3.44.14 LTR",
}

STATES = {
    "pass",             # exercised through the client, correct result
    "fail",             # exercised, wrong result
    "blocked",          # cannot be exercised yet, named cause
    "not-started",      # reachable, never attempted
    "n/a-no-client",    # the client cannot issue this operation
    "n/a-superseded",   # the client negotiates another version we also serve
}
CLOSED_STATES = {"pass", "n/a-no-client", "n/a-superseded"}
NEEDS_CITATION = {"n/a-no-client", "n/a-superseded", "blocked"}

# A pass has to be bound to a build under certification. Evidence naming any
# other build does not count - docs/certification-master-plan.md:18-19, "Version
# changes create a new target revision" - so these tokens are searched for in the
# evidence string. Superseded QGIS builds (3.44.3, 3.40.15) and QGIS 4.2.2 fail
# the check by simply not matching.
CERTIFIED_BUILD_TOKENS = ("3.7.1.1904", "3.44.14")

# --------------------------------------------------------------------------
# Citations. Every n/a in the checklist resolves to one of these, so a reader can
# check the claim instead of trusting it.
# --------------------------------------------------------------------------

CITE = {
    "pro-ogcapi": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/add-ogc-api-services.html: "
        '"Currently, only the OGC API Features and OGC API Tiles (map tiles) '
        'standards are supported in ArcGIS Pro."'
    ),
    "pro-ogc-classic": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/ogc-services.html: Pro "
        "consumes WMS, WMTS, WCS and WFS only among OGC classic services."
    ),
    "pro-wcs-versions": (
        "enterprise.arcgis.com WCS services: a client built for WCS 1.0.0, 1.1.0, "
        "1.1.1, 1.1.2 or 2.0.1 can consume the service; Pro exposes a Version "
        "selector and negotiates the highest, so it uses 2.0.1 here."
    ),
    "pro-wfs-read-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-wfs-services.html: "
        '"WFS with transactions is not yet supported. The layer behaves as a read-only '
        'data source." (fetched 2026-09-19)'
    ),
    "pro-oapif-read-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html: "
        '"Since the OGC API Features layer is not editable, you cannot make edits to the '
        'data or schema through ArcGIS Pro." (fetched 2026-09-19)'
    ),
    "pro-oapi-tiles-map-only": (
        "doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html: "
        '"Currently, the ArcGIS Pro client supports only the map tiles type of the OGC API '
        'Tiles specification." (fetched 2026-09-19); every tileset of the fixture is '
        "dataType vector (GET /ogc/tiles/collections/0/tiles on image sha256:6001b3b8ac05... "
        "lists only Mapbox Vector Tile tilesets), so there is no map-tiles tileset for Pro to add."
    ),
    "pro-no-sta": (
        "No SensorThings client ships in ArcGIS Pro; its OGC API support is limited "
        "to Features and Tiles per the Add OGC API services page."
    ),
    "qgis-registry": (
        "QgsProviderRegistry probe on QGIS 3.44.14-Solothurn: the provider key is "
        "absent from the registry."
    ),
    "qgis-gp-algorithms": (
        "honua-client-compat/evidence/native-gp-supplement-qgis-20260916-a: QGIS "
        "3.44.14 registers 9 processing providers and 440 algorithms, of which zero "
        "reference arcgis, esri or gpserver."
    ),
    "qgis-wcs-provider": (
        "QgsProviderRegistry on 3.44.14: 'OGC Web Coverage Service version 1.0/1.1 "
        "data provider'. The server also serves 1.0.0, which is the version this "
        "client is certified on, so 2.0.1 is superseded for this lane."
    ),
    "qgis-local-geometry": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "performs geometry operations locally through GEOS and GDAL and has no "
        "client for a remote Esri geometry service."
    ),
    "qgis-no-esri-locator": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: stock "
        "QGIS has no Esri locator client."
    ),
    "qgis-no-versioning": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "ships no provider or UI for Esri branch versioning."
    ),
    "qgis-rest-only": (
        "honua-client-compat/evidence/native-uncovered-v1-qgis-20260916-a: QGIS "
        "discovers ArcGIS services over REST only and has no SOAP catalog client."
    ),
    "qgis-wfs-no-propertyvalue": (
        "QGIS 3.44.14 WFS provider probe, 2026-09-18: the provider exposes no "
        "GetPropertyValue member - it always issues GetFeature - and no stored-query "
        "member. A storedQueryId passed in the URI is ignored rather than issued: a "
        "valid GetFeatureById id, a nonsense id and an entirely bogus URI key all "
        "produced the same valid layer with the same 10 features as no stored query "
        "at all, and the provider's decodeUri reports no keys for the URI. The "
        "control is what distinguishes 'ignored' from 'honoured'. Both operations "
        "are therefore unreachable from this client however the server behaves. "
        "Source probe 2026-09-19 of https://github.com/qgis/QGIS/tree/release-3_44/"
        "src/providers/wfs: the provider's request classes are qgswfsgetcapabilities, "
        "qgswfsdescribefeaturetype, qgswfsgetfeature and qgswfstransactionrequest; "
        "no file there mentions GetPropertyValue or ListStoredQueries."
    ),
    "qgis-rest-no-advanced": (
        "docs.qgis.org/3.44/en/docs/user_manual/managing_data_source/"
        "opening_data.html section 11.1.7.3 'Using ArcGIS REST Servers' documents "
        "service-tree browsing, layer loading, expression-builder attribute filters "
        "and a view-extent option. Attachments, related records, replica/sync and "
        "server-side statistics appear nowhere. Confirmed by wire capture through a "
        "logging proxy on 2026-09-17: the provider issues no outStatistics request "
        "and computes min/max/sum locally from an outFields=* download, and exposes "
        "no attachment, relationship or replica member. A binary string scan was "
        "not used - it reports no esriSpatialRel in any shipped QGIS library while "
        "spatial filtering demonstrably works. "
        "This closes the qgis-ui lane as well as pyqgis: every finding above is at "
        "the provider layer - no attachment or replica member on the provider, "
        "discoverRelations returning empty, statistics computed locally from an "
        "outFields=* download - and the desktop UI is driven by that same "
        "arcgisfeatureserver provider. A request the provider never issues cannot "
        "be issued by a panel drawn on top of it, and the cited source is the "
        "user-manual chapter describing that UI."
    ),
    "arcpy-no-geometryserver": (
        "arcpy 3.7.1 module probe, 2026-09-18: no attribute in arcpy or any "
        "server-facing submodule matches 'geometryserver' or 'geometryservice'. "
        "Every geometry entry point is local computation - Buffer_analysis, "
        "Project_management, arcpy.Geometry.projectAs - so a GeometryServer "
        "request is never issued. ArcPy cannot certify this protocol however the "
        "server behaves; Pro's own UI is the client that would."
    ),
    "arcpy-no-soap": (
        "arcpy 3.7.1 module probe, 2026-09-18: no attribute matches 'soap' or "
        "'wsdl'. arcpy speaks the REST surface only, so the GeoServices SOAP "
        "catalog has no arcpy caller."
    ),
    "arcpy-wfs-read-only": (
        "arcpy 3.7.1 module probe, 2026-09-18: the only WFS entry point is "
        "arcpy.conversion.WFSToFeatureClass, and GetParameterInfo lists exactly "
        "input_WFS_server, WFS_feature_type, out_path, out_name, "
        "out_feature_class, is_complex, out_gdb, max_features, expose_metadata, "
        "swap_xy and page_size. It reads a feature type into a feature class: no "
        "transaction, no property-value projection, no stored-query parameter, so "
        "WFS-T, GetPropertyValue and ListStoredQueries have no arcpy caller."
    ),
    "arcpy-no-wms-identify": (
        "arcpy 3.7.1 module probe, 2026-09-18: arcpy exposes no MakeWMSLayer or "
        "MakeWMTSLayer and no identify call against a WMS layer. A WMS is "
        "consumed by adding it to a map, which draws it; GetFeatureInfo is issued "
        "by Pro's Identify tool in the UI, not by arcpy."
    ),
    "arcpy-no-candidates": (
        "arcpy 3.7.1 module probe, 2026-09-18: arcpy.geocoding exposes "
        "GeocodeAddresses, BatchGeocodeServer, ReverseGeocode, RematchAddresses "
        "and locator authoring, but nothing issuing findAddressCandidates or "
        "suggest. Those are interactive REST endpoints Pro's search box calls; "
        "arcpy geocodes tables against a locator. reverseGeocode is deliberately "
        "NOT closed here: arcpy.geocoding.ReverseGeocode exists, so it is "
        "reachable."
    ),
    "arcpy-no-replica": (
        "doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/"
        "create-replica.html: arcpy.management.CreateReplica accepts 'Table View; "
        "Dataset' - layers and tables referencing versioned, editable data from an "
        "enterprise geodatabase. Feature services and REST FeatureServer URLs are "
        "not accepted inputs, so no ArcPy call can create a replica against this "
        "server. Verified 2026-09-17 against the Pro 3.7 reference."
    ),
    "arcpy-modules": (
        "doc.esri.com/en/arcgis-pro/latest/arcpy/get-started/arcpy-modules.html: "
        "ArcPy exposes no module for this protocol."
    ),
    "qgis-no-imageserver-raster": (
        "QGIS 3.44.14-Solothurn provider probe, 2026-09-18: the arcgismapserver "
        "provider accepts the fixture's ImageServer URL and reports the layer valid, "
        "but the raster it exposes is 0x0, identify at the raster centre returns no "
        "values, and a block read returns NaN - while the same point answers "
        "Band_1=100 over REST identify. docs.qgis.org/3.44 'Using ArcGIS REST "
        "Servers' documents Feature and Map services only. There is no QGIS client "
        "for an Esri image service, so an Esri elevation point query is unreachable "
        "from this client however the server behaves."
    ),
    "qgis-no-tilejson": (
        "QGIS 3.44.14-Solothurn provider probe, 2026-09-18: QgsProviderRegistry lists "
        "no TileJSON provider (the tile providers are xyzvectortiles, "
        "mbtilesvectortiles, vtpkvectortiles, arcgisvectortileservice and vectortile), "
        "and QgsVectorTileLayer built on the fixture's TileJSON descriptor URL is "
        "invalid under both type=xyz and a bare url= - the xyz source takes a tile "
        "URL template, not a descriptor. Nothing in this client reads a TileJSON "
        "document, so the cell is unreachable however the server behaves."
    ),
    "arcpy-nax-web-tools-only": (
        "ArcGIS Pro 3.7 arcpy.nax reference (network data source: a network dataset, a portal, or a "
        "stand-alone routing service dictionary whose url names the web-tool GP service) and "
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-p-na/certification/"
        "20260920T000204Z-desktop-arcgis-gp-naserver.cert.json: with the NAServer service and layer "
        "resources, the NetworkAnalysisUtilities tasks, portal helperServices and networkanalysis "
        "privileges all published, arcpy.nax answers 'Portal ... is not configured with the Route web "
        "tool' and, in the stand-alone form, posts to <url>/FindRoutes. arcpy.nax models every analysis "
        "on Esri's asynchronous routing web tools and never issues the NAServer solve; the synchronous "
        "NAServer layers are consumed by the Pro user interface (pro-ui lane)."
    ),
    "arcpy-rest-only-ops": (
        "honua-esri-compat arcpy_probes contract, run arcpy-client-compat-20260918-c: "
        "the operation is a REST operation with no core-arcpy surface - arcpy has "
        "no call that issues attachments, queryRelatedRecords, MapServer identify or "
        "legend; Pro's UI issues them, which is the pro-ui lane. Recorded "
        "not-applicable by the probe with that rule."
    ),
    "arcpy-mp-web-service-types": (
        "arcpy 3.7.1.1904 probe, 2026-09-18: the only arcpy path that consumes a "
        "web service is arcpy.mp Map.addDataFromPath, and its own validation "
        "names the complete set of service kinds it accepts - \"Invalid value for "
        "web_service_type: 'WMTS' (choices are: ['AUTOMATIC', 'ARCGIS_SERVER_WEB', "
        "'KML', 'VECTOR_TILE', 'WMS'])\". AUTOMATIC against the WMTS endpoint "
        "fails with \"AUTOMATIC failed, a more specific web_service_type may need "
        "to be provided\", and the same probe added the WMS endpoint and exported "
        "a drawn PNG, so the boundary is the client's, not the fixture's. WMTS, "
        "WCS, 3D Tiles and the OGC APIs have no arcpy entry point; ArcGIS Pro's "
        "own UI adds them, which is the pro-ui lane."
    ),
}

# --------------------------------------------------------------------------
# Evidence already produced, verified in this repository or the compat repos.
# --------------------------------------------------------------------------

# The QGIS LTR build under certification, as the envelopes record it.
QGIS_LTR_BUILD = "3.44.14-Solothurn"
QGIS_UI_RUN = "native-qgis-ltr-20260919-a"


def _qgis_ui(case_id: str) -> str:
    """Cite one inspected computer-use receipt in the native QGIS LTR operations run."""
    return (
        f"honua-client-compat/evidence/{QGIS_UI_RUN}/results.json - {case_id}, "
        f"QGIS {QGIS_LTR_BUILD} (windows-computer-use receipt, server-log corroborated), pass"
    )


def _pyqgis(
    protocol: str,
    version: str,
    passed: int,
    *,
    cert_id: str | None = None,
    skipped: int = 0,
) -> str:
    """Cite a committed pyqgis baseline envelope, or one cert id inside it."""
    scope = f" - {cert_id}," if cert_id else " -"
    tail = f", {skipped} skipped" if skipped else ""
    return (
        f"tests/baselines/client-compat/pyqgis/desktop-qgis-{protocol}.cert.json"
        f"{scope} protocol {protocol} {version}, client desktop-qgis "
        f"{QGIS_LTR_BUILD}, {passed} passed 0 failed{tail}"
    )


EV = {
    "pyqgis-wcs": _pyqgis("wcs", "1.0.0", 8),
    "pyqgis-oapif": _pyqgis(
        "ogc-features", "1.0", 21,
        skipped=3),
    "pyqgis-wfs": _pyqgis(
        "wfs", "2.0.0", 15,
        skipped=4),
    "arcpy-featureserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe(service) -> dataType=Workspace"
    ),
    "arcpy-featureserver-layer-metadata": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-LAYER-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe + 3 field(s) on the layer"
    ),
    "arcpy-featureserver-query": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-QUERY, ArcGIS Pro/arcpy 3.7.1.1904: da.SearchCursor read 3 row(s) over ['objectid', 'name']"
    ),
    "arcpy-featureserver-identify": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-PRM-GEOMETRY-GEOMETRYTYPE-SPATIALREL-DISTANC, ArcGIS Pro/arcpy 3.7.1.1904: spatialRel via SelectLayerByLocation(INTERSECT extent) -> 3 selected"
    ),
    "arcpy-featureserver-statistics": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-featureserver.cert.json - "
        "operations FS-PRM-OUTSTATISTICS-GROUPBYFIELDSFORSTATISTICS, ArcGIS Pro/arcpy 3.7.1.1904: outStatistics (count) computed over the cursor -> 3"
    ),
    "arcpy-featureserver-domains": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c-edit/certification/arcpy-client-compat-20260918-c-edit-desktop-arcgis-featureserver.cert.json - "
        "operations FS-OP-QUERY-DOMAINS, ArcGIS Pro/arcpy 3.7.1.1904: arcpy ListFields reports domain(s) on ['status']: ['StatusDomain']"
    ),
    "arcpy-mapserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-mapserver.cert.json - "
        "operations MS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: ms-load layer added and loaded in ArcGIS Pro (2 layer(s) in map)"
    ),
    "arcpy-mapserver-export": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-mapserver.cert.json - "
        "operations MS-OP-EXPORT-MAP, ArcGIS Pro/arcpy 3.7.1.1904: added ms-export layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-imageserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-SERVICE-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Describe(ImageServer) -> dataType=RasterLayer"
    ),
    "arcpy-imageserver-exportimage": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-EXPORT-IMAGE, ArcGIS Pro/arcpy 3.7.1.1904: arcpy.Raster(ImageServer) opened (width=64)"
    ),
    "arcpy-imageserver-identify": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "operations IS-OP-IDENTIFY, ArcGIS Pro/arcpy 3.7.1.1904: GetCellValue (identify) at (-122.4150,37.7650) -> 180, matching the service's identify ('180')"
    ),
    "arcpy-wms-getcapabilities": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wms.cert.json - "
        "extensions OGC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: added ogc-wms layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-wms-getmap": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wms.cert.json - "
        "extensions OGC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: added ogc-wms layer and exported a drawn PNG (5697 bytes)"
    ),
    "arcpy-wfs-getcapabilities": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-wfs-describefeaturetype": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-wfs-getfeature": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-ogc-wfs.cert.json - "
        "extensions OGC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: WFS layer 'honua:browser_points' added in ArcGIS Pro; arcpy counted 3 feature(s)"
    ),
    "arcpy-vectortileserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-vectortileserver-tile": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-vectortileserver-style": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-02, ArcGIS Pro/arcpy 3.7.1.1904: wf-vectortile layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-i3s-sceneserver-scene-layer": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-wf-scene-scene-vectortile.cert.json - "
        "extensions WF-3D-01, ArcGIS Pro/arcpy 3.7.1.1904: wf-scene layer added and loaded in ArcGIS Pro (1 layer(s) in map)"
    ),
    "arcpy-gpserver-service-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-GP-SERVICE-INFO, ArcGIS Pro/arcpy 3.7.1.1904: arcpy.ImportToolbox resolved https://host.docker.internal:18443/arcgis/services;test_service and published task 'Buffer' as arcpy.Buffer_testservice"
    ),
    "arcpy-gpserver-task-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-TASK-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: task 'Buffer' publishes signature 'Buffer_testservice(wkb, srid, distance, {geodesic})' and 3 typed parameter(s): wkb (String):; srid (Long):; distanc"
    ),
    "arcpy-gpserver-submit-job": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-SUBMIT-JOB, ArcGIS Pro/arcpy 3.7.1.1904: calling arcpy.Buffer_testservice returned an arcpy.Result (resultID gp-8eda90217661436ebe408df110f8f560)"
    ),
    "arcpy-gpserver-job-status": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-JOB-STATUS, ArcGIS Pro/arcpy 3.7.1.1904: Result.status reached the terminal code 4"
    ),
    "arcpy-gpserver-results": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-JOB-RESULTS, ArcGIS Pro/arcpy 3.7.1.1904: Result.getOutput retrieved 1 output(s): ['<geoprocessing record set object object at 0x00000242B2AE8890>']"
    ),
    "arcpy-gpserver-cancel": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-gp/certification/20260919T184352Z-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-CANCEL-JOB, ArcGIS Pro/arcpy 3.7.1.1904: Result.cancel() was accepted on the submitted job; status after the call was 8 (a fast task may already have reached a terminal state, which is not a "
    ),
    "arcpy-naserver-standalone-probe": (
        "honua-esri-compat/evidence/arcpy-standalone-probes-20260919/naserver-arcpy-nax.md - "
        "ArcGIS Pro/arcpy 3.7.1.1904: arcpy.nax.Route stand-alone dictionary fails at the utility "
        "service: Task 'GetTravelModes' on service 'test_service' was not found; NAServer service "
        "and Route layer resources answer 404 while Route/solve returns a route over the seeded grid"
    ),
    "arcpy-vms-workspace-probe": (
        "honua-esri-compat/evidence/arcpy-standalone-probes-20260919/versionmanagement-arcpy.md - "
        "ArcGIS Pro/arcpy 3.7.1.1904: CreateVersion on the FeatureServer URL fails ERROR 000301 "
        "workspace is of the wrong type after 14 GET admin/services/test_service.MapServer -> 404; "
        "no request reaches the VersionManagementServer, which answers its own resources"
    ),
    "arcpy-geocodeserver-geocodeaddresses": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-geocodeserver.cert.json - "
        "extensions GC-EXT-01, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Locator geocoded a single-line address through arcgis/rest/services/GeocodeServer ->"
    ),
    "arcpy-geocodeserver-reversegeocode": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-geocodeserver.cert.json - "
        "extensions GC-EXT-02, ArcGIS Pro/arcpy 3.7.1.1904: arcpy Locator reverse-geocoded (-122.42, 37.77) through arcgis/rest/services/GeocodeServer"
    ),
    "arcpy-elevation-point-query": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-imageserver.cert.json - "
        "extensions IS-EXT-ELEV-01, ArcGIS Pro/arcpy 3.7.1.1904: arcpy read elevation 180 at (-122.4150,37.7650) through RasterToNumPyArray, matching the service's identify ('180'); serviceDataType=esriImageServiceDataTypeElevation"
    ),
    "pro-matrix": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904"
    ),
    "pro-matrix-ui-feat-edits": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-FEAT-CREATE, UI-FEAT-EDIT, UI-FEAT-DELETE: Create Features digitised objectid 2044 (POST /FeatureServer/applyEdits 200, independent query returned the point at -122.3854312, 37.7664971), the Attributes pane changed value 60->61 and back (each state re-read from the service), and table Delete + Save Edits removed 2044 (independent count back to 12)"
    ),
    "pro-matrix-ui-service-map-identify": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-MAP: Explore pop-up on the MapServer layer returned UI Points (1) name ui-point-07, objectid 10, category A, value 60, matching the service; Pro issued POST MapServer/identify 200"
    ),
    "pro-matrix-ui-service-map-legend": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-MAP: the Map Image Layer's sublayer UI Points resolved the server legend entry 'Default' (POST MapServer/legend 200) alongside the export render"
    ),
    "pro-matrix-ui-service-wms-featureinfo": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-WMS: Explore pop-up on the WMS layer returned the text/plain GetFeatureInfo body 'Layer=UI Points, category=A, name=ui-point-07, objectid=10, value=60', matching the service and the server-side GetFeatureInfo in all three advertised formats"
    ),
    "pro-matrix-ui-service-vts-style": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-ESRI-VECTOR: the Vector Tile Service layer rendered with the service's resources/styles/root.json (Mapbox v8, layer esri-circle over source-layer 'layer'), all 12 fixture points styled, re-rendered after the map was reopened"
    ),
    "pro-matrix-ui-service-wfs-describe": (
        "honua-esri-compat/evidence/native-pro-matrix-20260917-a/results.json - "
        "ArcGIS Pro 3.7.1.1904, UI-SERVICE-WFS: the WFS layer added through Add WFS Layer(s) carries the typed schema DescribeFeatureType declares (GmlID, objectid, name, category, value, Shape Point) over 5 successful WFS requests, and its rows match the REST oracle"
    ),
    "qgis-ltr": (
        "honua-client-compat/evidence/native-qgis-ltr-20260916-a/results.json - "
        "QGIS 3.44.14-Solothurn"
    ),

    # WMS 1.3.0 and WMTS 1.0.0 through the QGIS wms provider,
    # tests/python/pyqgis/test_wm{s,ts}_client_compat.py.
    "pyqgis-wms-caps": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-CONN-01"),
    "pyqgis-wms-getmap": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-01"),
    "pyqgis-wms-featureinfo": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-SCHM-01"),
    "pyqgis-wms-legend": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-wms-styles": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-wms-time": _pyqgis("wms", "1.3.0", 9, cert_id="CERT-QFLT-01"),
    "pyqgis-wmts-caps": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-CONN-01"),
    "pyqgis-wmts-gettile": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-RNDR-01"),
    "pyqgis-wmts-featureinfo": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-SCHM-01"),
    "pyqgis-wmts-restful": _pyqgis("wmts", "1.0.0", 7, cert_id="CERT-DISC-02"),

    # GeoServices REST, STAC and the artifact surfaces, certified 2026-09-17.
    "pyqgis-wfst-insert": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-01", skipped=4),
    "pyqgis-wfst-update": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-02", skipped=4),
    "pyqgis-wfst-delete": _pyqgis("wfs", "2.0.0", 15, cert_id="NB-PQG-WFST-03", skipped=4),
    "pyqgis-oapif-part4": _pyqgis("ogc-features", "1.0", 21, cert_id="NB-PQG-OAPIFT-01/NB-PQG-OAPIFT-02/NB-PQG-OAPIFT-03", skipped=3),
    "pyqgis-fs-applyedits": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-AUTH-01"),
    "pyqgis-fs-domains": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-SCHM-02"),
    "pyqgis-3dtiles": _pyqgis("3d-tiles", "1.1", 2, cert_id="NB-PQG-3DT-01/NB-PQG-3DT-02"),
    "qgis-ui-ui-op-wms-getcapabilities": _qgis_ui("UI-OP-WMS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wms-getmap": _qgis_ui("UI-OP-WMS-GETMAP"),
    "qgis-ui-ui-op-wms-getfeatureinfo": _qgis_ui("UI-OP-WMS-GETFEATUREINFO"),
    "qgis-ui-ui-op-wms-getlegendgraphic": _qgis_ui("UI-OP-WMS-GETLEGENDGRAPHIC"),
    "qgis-ui-ui-op-wms-styles": _qgis_ui("UI-OP-WMS-STYLES"),
    "qgis-ui-ui-op-wms-time-dimension": _qgis_ui("UI-OP-WMS-TIME-DIMENSION"),
    "qgis-ui-ui-op-wmts-getcapabilities": _qgis_ui("UI-OP-WMTS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wmts-gettile": _qgis_ui("UI-OP-WMTS-GETTILE"),
    "qgis-ui-ui-op-wmts-getfeatureinfo": _qgis_ui("UI-OP-WMTS-GETFEATUREINFO"),
    "qgis-ui-ui-op-wmts-restful-tile-path": _qgis_ui("UI-OP-WMTS-RESTFUL-TILE-PATH"),
    "qgis-ui-ui-op-wfs-getcapabilities": _qgis_ui("UI-OP-WFS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wfs-describefeaturetype": _qgis_ui("UI-OP-WFS-DESCRIBEFEATURETYPE"),
    "qgis-ui-ui-op-wfs-getfeature": _qgis_ui("UI-OP-WFS-GETFEATURE"),
    "qgis-ui-ui-op-wfs-transaction-insert": _qgis_ui("UI-OP-WFS-TRANSACTION-INSERT"),
    "qgis-ui-ui-op-wfs-transaction-update": _qgis_ui("UI-OP-WFS-TRANSACTION-UPDATE"),
    "qgis-ui-ui-op-wfs-transaction-delete": _qgis_ui("UI-OP-WFS-TRANSACTION-DELETE"),
    "qgis-ui-ui-op-wcs-getcapabilities": _qgis_ui("UI-OP-WCS-GETCAPABILITIES"),
    "qgis-ui-ui-op-wcs-describecoverage": _qgis_ui("UI-OP-WCS-DESCRIBECOVERAGE"),
    "qgis-ui-ui-op-wcs-getcoverage": _qgis_ui("UI-OP-WCS-GETCOVERAGE"),
    "qgis-ui-ui-op-oapif-landing-page": _qgis_ui("UI-OP-OAPIF-LANDING-PAGE"),
    "qgis-ui-ui-op-oapif-conformance": _qgis_ui("UI-OP-OAPIF-CONFORMANCE"),
    "qgis-ui-ui-op-oapif-collections": _qgis_ui("UI-OP-OAPIF-COLLECTIONS"),
    "qgis-ui-ui-op-oapif-items": _qgis_ui("UI-OP-OAPIF-ITEMS"),
    "qgis-ui-ui-op-oapif-item": _qgis_ui("UI-OP-OAPIF-ITEM"),
    "qgis-ui-ui-op-oapif-bbox-datetime-filter": _qgis_ui("UI-OP-OAPIF-BBOX-DATETIME-FILTER"),
    "qgis-ui-ui-op-oapif-crs-negotiation": _qgis_ui("UI-OP-OAPIF-CRS-NEGOTIATION"),
    "qgis-ui-ui-op-oapif-transactions-part4": _qgis_ui("UI-OP-OAPIF-TRANSACTIONS-PART4"),
    "qgis-ui-ui-op-stac-catalog-landing": _qgis_ui("UI-OP-STAC-CATALOG-LANDING"),
    "qgis-ui-ui-op-stac-collections": _qgis_ui("UI-OP-STAC-COLLECTIONS"),
    "qgis-ui-ui-op-stac-item-search": _qgis_ui("UI-OP-STAC-ITEM-SEARCH"),
    "qgis-ui-ui-op-stac-asset-download": _qgis_ui("UI-OP-STAC-ASSET-DOWNLOAD"),
    "qgis-ui-ui-op-sta-entity-sets": _qgis_ui("UI-OP-STA-ENTITY-SETS"),
    "qgis-ui-ui-op-sta-expand": _qgis_ui("UI-OP-STA-EXPAND"),
    "qgis-ui-ui-op-sta-filter-paging": _qgis_ui("UI-OP-STA-FILTER-PAGING"),
    "qgis-ui-ui-op-fs-applyedits": _qgis_ui("UI-OP-FS-APPLYEDITS"),
    "qgis-ui-ui-op-fs-domains": _qgis_ui("UI-OP-FS-DOMAINS"),
    "qgis-ui-ui-op-mapserver-service-info": _qgis_ui("UI-OP-MAPSERVER-SERVICE-INFO"),
    "qgis-ui-ui-op-mapserver-export": _qgis_ui("UI-OP-MAPSERVER-EXPORT"),
    "qgis-ui-ui-op-mapserver-identify": _qgis_ui("UI-OP-MAPSERVER-IDENTIFY"),
    "qgis-ui-ui-op-mapserver-legend": _qgis_ui("UI-OP-MAPSERVER-LEGEND"),
    "qgis-ui-ui-op-vts-service-info": _qgis_ui("UI-OP-VTS-SERVICE-INFO"),
    "qgis-ui-ui-op-vts-tile": _qgis_ui("UI-OP-VTS-TILE"),
    "qgis-ui-ui-op-vts-style": _qgis_ui("UI-OP-VTS-STYLE"),
    "qgis-ui-ui-op-styles": _qgis_ui("UI-OP-STYLES"),
    "qgis-ui-ui-op-pmtiles-archive-read": _qgis_ui("UI-OP-PMTILES-ARCHIVE-READ"),
    "qgis-ui-ui-op-3dtiles-tileset": _qgis_ui("UI-OP-3DTILES-TILESET"),
    "pyqgis-fs-info": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-DISC-01"),
    "pyqgis-fs-meta": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-SCHM-01"),
    "pyqgis-fs-query": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-QFLT-01"),
    "pyqgis-fs-identify": _pyqgis("featureserver", "10.8", 6, cert_id="CERT-GEOM-01"),
    "pyqgis-ms-info": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-CONN-01"),
    "pyqgis-ms-export": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-RNDR-01"),
    "pyqgis-ms-identify": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-SCHM-01"),
    "pyqgis-ms-legend": _pyqgis("mapserver", "10.8", 4, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-vts-info": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-CONN-02"),
    "pyqgis-vts-tile": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-RNDR-02"),
    "pyqgis-vts-style": _pyqgis("vectortileserver", "10.8", 3, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-stac-landing": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-CONN-01"),
    "pyqgis-stac-collections": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-DISC-01"),
    "pyqgis-stac-search": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-QFLT-01"),
    "pyqgis-stac-asset": _pyqgis("stac", "1.0.0", 4, cert_id="CERT-RNDR-URL-01"),
    "pyqgis-pmtiles": _pyqgis("pmtiles", "3", 6, cert_id="CERT-CONN-01"),
    "pyqgis-cog": _pyqgis("cog", "GeoTIFF", 4, cert_id="CERT-RNDR-01"),
    "arcpy-cog-range-read": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-edit/certification/"
        "20260919T162438Z-desktop-arcgis-cog.cert.json - CERT-CONN-01, CERT-DISC-01, CERT-DISC-02, "
        "CERT-RNDR-01, ArcGIS Pro/arcpy 3.7.1.1904: HEAD 200 image/tiff, arcpy.Raster describes the "
        "published COG as 64x64/1 band over the fixture extent, HEAD length stable and Range 0-3 -> 206 "
        "with the TIFF magic after an unranged warm-up, RasterToNumPyArray at (-122.42, 37.77) = 100 "
        "== ImageServer identify"
    ),
    "arcpy-featureserver-apply-edits": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260919-f-edit/certification/"
        "20260919T162438Z-desktop-arcgis-featureserver.cert.json - FS-OP-APPLY-EDITS (and ADD/UPDATE/"
        "DELETE-FEATURES, FS-OP-APPEND), ArcGIS Pro/arcpy 3.7.1.1904: da.InsertCursor over the "
        "token-bearing layer URL committed one feature (new objectid observed from a fresh ArcPy process), "
        "da.UpdateCursor.deleteRow removed it and the count returned to 10; Append added and cleanup "
        "restored the fixture; run verdict pass"
    ),
    "qgis-ui-ui-op-cog-range-read": (
        "honua-client-compat/evidence/native-qgis-cog-20260919-a/results.json - "
        f"UI-OP-COG-RANGE-READ, QGIS {QGIS_LTR_BUILD} (windows-computer-use receipt: Data "
        "Source Manager > Raster > Protocol HTTP/HTTPS/FTP added /api/v1/rasters/cog/cog/0/1.tif "
        "through the gdal provider, Layer Properties 64x64 Float32 EPSG:4326, Identify Band 1 = 100 "
        "== ImageServer identify; server log shows GDAL/3.13.3 HEAD 200 and Range GET 206), pass"
    ),
    "pyqgis-styles": _pyqgis("ogc-api-styles", "1.0", 3, cert_id="CERT-RNDR-SYM-01"),
    "pyqgis-sta-entities": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-DISC-01"),
    "pyqgis-sta-expand": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-SCHM-01"),
    "pyqgis-sta-paging": _pyqgis("sensorthings", "1.1", 3, cert_id="CERT-PAGE-01"),
}

# --------------------------------------------------------------------------
# The matrix. Each protocol lists its operations and, per lane, a state.
# A bare string is the state; a tuple is (state, citation-or-evidence key).
# --------------------------------------------------------------------------

NS = "not-started"


def _blocked(reason: str) -> tuple[str, str]:
    return ("blocked", reason)



MATRIX: list[dict] = [
    {
        "protocol": "wms", "version": "1.3.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getcapabilities"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wms-caps")},
            "GetMap": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getmap"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getmap"), "pyqgis": ("pass", "pyqgis-wms-getmap")},
            "GetFeatureInfo": {"pro-ui": ("pass", "pro-matrix-ui-service-wms-featureinfo"), "arcpy": ("n/a-no-client", "arcpy-no-wms-identify"), "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wms-featureinfo")},
            "GetLegendGraphic": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wms-getlegendgraphic"),
                                 "pyqgis": ("pass", "pyqgis-wms-legend")},
            "styles": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-wms-styles"), "pyqgis": ("pass", "pyqgis-wms-styles")},
            "time-dimension": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wms-time-dimension"),
                               "pyqgis": ("pass", "pyqgis-wms-time")},
        },
    },
    {
        "protocol": "wmts", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"),
                                "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getcapabilities"),
                                "pyqgis": ("pass", "pyqgis-wmts-caps")},
            "GetTile": {"pro-ui": ("pass", "pro-matrix"),
                        "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-gettile"), "pyqgis": ("pass", "pyqgis-wmts-gettile")},
            "GetFeatureInfo": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-getfeatureinfo"),
                               "pyqgis": ("pass", "pyqgis-wmts-featureinfo")},
            "RESTful-tile-path": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                  "qgis-ui": ("pass", "qgis-ui-ui-op-wmts-restful-tile-path"),
                                  "pyqgis": ("pass", "pyqgis-wmts-restful")},
        },
    },
    {
        "protocol": "wfs", "version": "2.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getcapabilities"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wfs")},
            "DescribeFeatureType": {"pro-ui": ("pass", "pro-matrix-ui-service-wfs-describe"), "arcpy": ("pass", "arcpy-wfs-describefeaturetype"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-describefeaturetype"),
                                    "pyqgis": ("pass", "pyqgis-wfs")},
            "GetFeature": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getfeature"),
                           "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-getfeature"), "pyqgis": ("pass", "pyqgis-wfs")},
            "GetPropertyValue": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
            "Transaction-Insert": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-insert"), "pyqgis": ("pass", "pyqgis-wfst-insert")},
            "Transaction-Update": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-update"), "pyqgis": ("pass", "pyqgis-wfst-update")},
            "Transaction-Delete": {"pro-ui": ("n/a-no-client", "pro-wfs-read-only"), "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("pass", "qgis-ui-ui-op-wfs-transaction-delete"), "pyqgis": ("pass", "pyqgis-wfst-delete")},
            "ListStoredQueries": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": ("n/a-no-client", "qgis-wfs-no-propertyvalue"), "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
        },
    },
    {
        "protocol": "wcs", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcapabilities"), "pyqgis": ("pass", "pyqgis-wcs")},
            "DescribeCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                 "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-describecoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
            "GetCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-wcs-getcoverage"), "pyqgis": ("pass", "pyqgis-wcs")},
        },
    },
    {
        "protocol": "wcs", "version": "2.0.1",
        "operations": {
            "GetCapabilities": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "DescribeCoverage": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                                 "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
            "GetCoverage": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("n/a-superseded", "qgis-wcs-provider"),
                            "pyqgis": ("n/a-superseded", "qgis-wcs-provider")},
        },
    },
    {
        "protocol": "ogc-api-features", "version": "1.0",
        "operations": {
            "landing-page": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-landing-page"), "pyqgis": ("pass", "pyqgis-oapif")},
            "conformance": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-conformance"), "pyqgis": ("pass", "pyqgis-oapif")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-collections"), "pyqgis": ("pass", "pyqgis-oapif")},
            "items": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                      "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-items"), "pyqgis": ("pass", "pyqgis-oapif")},
            "item": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-item"), "pyqgis": ("pass", "pyqgis-oapif")},
            "bbox-datetime-filter": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                     "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-bbox-datetime-filter"), "pyqgis": ("pass", "pyqgis-oapif")},
            "crs-negotiation": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-crs-negotiation"), "pyqgis": ("pass", "pyqgis-oapif")},
            "transactions-part4": {"pro-ui": ("n/a-no-client", "pro-oapif-read-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                                   "qgis-ui": ("pass", "qgis-ui-ui-op-oapif-transactions-part4"), "pyqgis": ("pass", "pyqgis-oapif-part4")},
        },
    },
    {
        "protocol": "ogc-api-tiles", "version": "1.0",
        "operations": {
            "landing-tilesets": {"pro-ui": ("n/a-no-client", "pro-oapi-tiles-map-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                 "pyqgis": ("n/a-no-client", "qgis-registry")},
            "tile": {"pro-ui": ("n/a-no-client", "pro-oapi-tiles-map-only"), "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": ("n/a-no-client", "qgis-registry"),
                     "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "stac", "version": "1.0.0",
        "operations": {
            "catalog-landing": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-catalog-landing"),
                                "pyqgis": ("pass", "pyqgis-stac-landing")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-collections"),
                            "pyqgis": ("pass", "pyqgis-stac-collections")},
            "item-search": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-item-search"),
                            "pyqgis": ("pass", "pyqgis-stac-search")},
            "asset-download": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": ("pass", "qgis-ui-ui-op-stac-asset-download"),
                               "pyqgis": ("pass", "pyqgis-stac-asset")},
        },
    },
    {
        "protocol": "sensorthings", "version": "1.1",
        "operations": {
            "entity-sets": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": ("pass", "qgis-ui-ui-op-sta-entity-sets"),
                            "pyqgis": ("pass", "pyqgis-sta-entities")},
            "expand": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-sta-expand"), "pyqgis": ("pass", "pyqgis-sta-expand")},
            "filter-paging": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                              "arcpy": ("n/a-no-client", "arcpy-modules"),
                              "qgis-ui": ("pass", "qgis-ui-ui-op-sta-filter-paging"),
                              "pyqgis": ("pass", "pyqgis-sta-paging")},
        },
    },
    {
        "protocol": "featureserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ltr"),
                             "pyqgis": ("pass", "pyqgis-fs-info")},
            "layer-metadata": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-layer-metadata"),
                               "qgis-ui": ("pass", "qgis-ltr"),
                               "pyqgis": ("pass", "pyqgis-fs-meta")},
            "query": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-query"),
                      "qgis-ui": ("pass", "qgis-ltr"),
                      "pyqgis": ("pass", "pyqgis-fs-query")},
            "identify": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-featureserver-identify"),
                         "qgis-ui": ("pass", "qgis-ltr"),
                         "pyqgis": ("pass", "pyqgis-fs-identify")},
            # QGIS edits through the per-operation addFeatures / updateFeatures /
            # deleteFeatures endpoints, never combined applyEdits. The earlier
            # "objectid: null" 1006 rejection did not reproduce on 3.44.14: the
            # form's Autogenerate OID is omitted from the payload and the insert
            # and delete commit (UI-OP-FS-APPLYEDITS).
            "applyEdits": {"pro-ui": ("pass", "pro-matrix-ui-feat-edits"), "arcpy": ("pass", "arcpy-featureserver-apply-edits"),
                           "qgis-ui": ("pass", "qgis-ui-ui-op-fs-applyedits"),
                           "pyqgis": ("pass", "pyqgis-fs-applyedits")},
            # Both fails are tracked. #5012 is the per-feature attachments POST
            # rejection that makes Pro report zero attachments; #5021 is the
            # V1-catalog compat synthesis dropping relationships, attachments and
            # VectorTileServer.
            "attachments": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5012"),
                "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "relatedRecords": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5021"),
                "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            # The server answers outStatistics correctly; QGIS never asks. It
            # downloads outFields=* and aggregates locally, so there is no client
            # request to certify.
            "statistics": {"pro-ui": NS, "arcpy": ("pass", "arcpy-featureserver-statistics"), "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                           "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "domains": {"pro-ui": NS, "arcpy": ("pass", "arcpy-featureserver-domains"), "qgis-ui": ("pass", "qgis-ui-ui-op-fs-domains"),
                        "pyqgis": ("pass", "pyqgis-fs-domains")},
            "replica-sync": {
                # The surface IS implemented - createReplica, synchronizeReplica and
                # unregisterReplica, a distributed replica store and a Postgres
                # repository - and is merely switched off here. Only once it is on can
                # the residual format question be tested: Pro's offline download asks
                # for dataFormat=sqlite (an Esri mobile geodatabase, .geodatabase: a
                # single-file SQLite database with Esri's own schema and ST_Geometry,
                # explicitly not OGC GeoPackage), and
                # FeatureServerRequestHandlers.ReplicaDelivery.cs rejects any
                # dataFormat but json. GDAL ships no .geodatabase driver - only
                # OpenFileGDB, for the .gdb directory format - so that format has no
                # writer in our toolchain.
                "pro-ui": NS,  # sync.offline is enabled on the fixture (syncEnabled=true live);
                #  the Pro offline-map flow has not been exercised yet
                "arcpy": ("n/a-no-client", "arcpy-no-replica"),
                "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
        },
    },
    {
        "protocol": "mapserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-service-info"), "pyqgis": ("pass", "pyqgis-ms-info")},
            "export": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-export"),
                       "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-export"), "pyqgis": ("pass", "pyqgis-ms-export")},
            "identify": {"pro-ui": ("pass", "pro-matrix-ui-service-map-identify"), "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-identify"),
                         "pyqgis": ("pass", "pyqgis-ms-identify")},
            "legend": {"pro-ui": ("pass", "pro-matrix-ui-service-map-legend"), "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": ("pass", "qgis-ui-ui-op-mapserver-legend"),
                       "pyqgis": ("pass", "pyqgis-ms-legend")},
        },
    },
    {
        "protocol": "imageserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-service-info"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
            "exportImage": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-exportimage"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "identify": {"pro-ui": NS, "arcpy": ("pass", "arcpy-imageserver-identify"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "vectortileserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-vectortileserver-service-info"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-vts-service-info"), "pyqgis": ("pass", "pyqgis-vts-info")},
            "tile": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-vectortileserver-tile"),
                     "qgis-ui": ("pass", "qgis-ui-ui-op-vts-tile"), "pyqgis": ("pass", "pyqgis-vts-tile")},
            "style": {"pro-ui": ("pass", "pro-matrix-ui-service-vts-style"), "arcpy": ("pass", "arcpy-vectortileserver-style"), "qgis-ui": ("pass", "qgis-ui-ui-op-vts-style"),
                      "pyqgis": ("pass", "pyqgis-vts-style")},
        },
    },
    {
        "protocol": "gpserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-service-info"),
                             "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                             "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "task-info": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-task-info"),
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "submitJob": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-submit-job"),
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "job-status": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-job-status"),
                           "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                           "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "results": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-results"),
                        "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                        "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "cancel": {"pro-ui": NS, "arcpy": ("pass", "arcpy-gpserver-cancel"),
                       "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                       "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
        },
    },
    {
        "protocol": "geocodeserver", "version": "GeoServices REST",
        "operations": {
            "findAddressCandidates": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-candidates"),
                                      "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                                      "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "suggest": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-candidates"),
                        "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                        "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "reverseGeocode": {"pro-ui": NS, "arcpy": ("pass", "arcpy-geocodeserver-reversegeocode"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                               "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "geocodeAddresses": {"pro-ui": NS, "arcpy": ("pass", "arcpy-geocodeserver-geocodeaddresses"),
                                 "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                                 "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
        },
    },
    {
        "protocol": "geometryserver", "version": "GeoServices REST",
        "operations": {
            "project": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                        "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                        "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "buffer": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                       "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                       "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "areasAndLengths": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                                "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                                "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
            "relation": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-geometryserver"),
                         "qgis-ui": ("n/a-no-client", "qgis-local-geometry"),
                         "pyqgis": ("n/a-no-client", "qgis-local-geometry")},
        },
    },
    {
        "protocol": "naserver", "version": "GeoServices REST",
        "operations": {
            # pgRouting is installed in the fixture image and the routing grid seed
            # (tests/seed/client-compat-routing-v1.sql) makes Route/solve and
            # ServiceArea/solveServiceArea return results; the NAServer metadata,
            # NetworkAnalysisUtilities tasks and portal helperServices shipped for
            # honua-server#5035. arcpy still has no path to the NAServer solve: arcpy.nax
            # drives only Esri's asynchronous routing web tools (n/a-no-client, cited).
            # The synchronous layers are the Pro user interface's path (pro-ui).
            "route-solve": {"pro-ui": NS,
                            "arcpy": ("n/a-no-client", "arcpy-nax-web-tools-only"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "service-area": {"pro-ui": NS,
                             "arcpy": ("n/a-no-client", "arcpy-nax-web-tools-only"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "versionmanagementserver", "version": "GeoServices REST",
        "operations": {
            # versioning.branch is enabled in the client-compat fixture and the
            # VersionManagementServer answers its resources; arcpy's branch-versioning
            # tools reject the feature service as a workspace because the Admin API
            # service resource they validate against is not published (honua-server#5036).
            "create-version": {"pro-ui": NS,
                               "arcpy": ("fail", "arcpy-vms-workspace-probe", "honua-server#5036"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
            "reconcile-post": {"pro-ui": NS,
                               "arcpy": ("fail", "arcpy-vms-workspace-probe", "honua-server#5036"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
        },
    },
    {
        "protocol": "geoservices-soap", "version": "GeoServices SOAP",
        "operations": {
            "catalog-discovery": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-soap"),
                                  "qgis-ui": ("n/a-no-client", "qgis-rest-only"),
                                  "pyqgis": ("n/a-no-client", "qgis-rest-only")},
        },
    },
    {
        "protocol": "odata", "version": "v4",
        "operations": {
            "metadata": {"pro-ui": ("n/a-no-client", "pro-ogc-classic"),
                         "arcpy": ("n/a-no-client", "arcpy-modules"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
            "entity-query": {"pro-ui": ("n/a-no-client", "pro-ogc-classic"),
                             "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-maps", "version": "1.0",
        "operations": {
            "map": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                    "arcpy": ("n/a-no-client", "arcpy-modules"),
                    "qgis-ui": ("n/a-no-client", "qgis-registry"),
                    "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-coverages", "version": "1.0",
        "operations": {
            "coverage": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                         "arcpy": ("n/a-no-client", "arcpy-modules"),
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-records", "version": "1.0",
        "operations": {
            "records": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                        "arcpy": ("n/a-no-client", "arcpy-modules"),
                        "qgis-ui": ("n/a-no-client", "qgis-registry"),
                        "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-processes", "version": "1.0",
        "operations": {
            "processes-execute": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                                  "arcpy": ("n/a-no-client", "arcpy-modules"),
                                  "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                  "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "ogc-api-styles", "version": "1.0",
        "operations": {
            "styles": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
                       # The previous cause - "landing reports an empty styles
                       # array" - was simply false: /ogc/styles serves 8 styles with
                       # negotiable SLD 1.0/1.1 and Mapbox representations, and QGIS
                       # applies the SLD verbatim.
                       "qgis-ui": ("pass", "qgis-ui-ui-op-styles"),
                       "pyqgis": ("pass", "pyqgis-styles")},
        },
    },
    {
        "protocol": "ogc-api-edr", "version": "1.0",
        "operations": {
            "edr-query": {"pro-ui": ("n/a-no-client", "pro-ogcapi"),
                          "arcpy": ("n/a-no-client", "arcpy-modules"),
                          "qgis-ui": ("n/a-no-client", "qgis-registry"),
                          "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "pmtiles", "version": "3",
        "operations": {
            # The archive is now published at fixture bring-up. It cannot be
            # written to disk: LocalFileStorage indexes its objects once at
            # construction, so it is published through the running server.
            "archive-read": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                             "qgis-ui": ("pass", "qgis-ui-ui-op-pmtiles-archive-read"),
                             "pyqgis": ("pass", "pyqgis-pmtiles")},
        },
    },
    {
        "protocol": "tilejson", "version": "3.0.0",
        "operations": {
            "descriptor": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                           "qgis-ui": ("n/a-no-client", "qgis-no-tilejson"),
                           "pyqgis": ("n/a-no-client", "qgis-no-tilejson")},
        },
    },
    {
        "protocol": "cog", "version": "GeoTIFF",
        "operations": {
            # The fixture seed publishes test_service layer 0's raster through
            # POST /api/v1/admin/raster-artifacts/cog; the public range proxy
            # /api/v1/rasters/cog/{artifactId} answers HEAD with the real length
            # and 206 for ranges, which is what GDAL /vsicurl needs.
            "range-read": {"pro-ui": NS, "arcpy": ("pass", "arcpy-cog-range-read"),
                           "qgis-ui": ("pass", "qgis-ui-ui-op-cog-range-read"),
                           "pyqgis": ("pass", "pyqgis-cog")},
        },
    },
    {
        "protocol": "i3s-sceneserver", "version": "1.x",
        "operations": {
            # The 404 recorded here as "no scene published" is the capability gate
            # itself: the manifest reports serve.i3s-scene as
            # reasonCode=experimental-disabled. Whether a scene also needs
            # publishing cannot be established until the surface is switched on.
            "scene-layer": {"pro-ui": NS,  # serve.i3s-scene is enabled and the seeded scene
                                    #  serves live; not yet exercised through Pro
                            "arcpy": ("pass", "arcpy-i3s-sceneserver-scene-layer"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "3d-tiles", "version": "1.0",
        "operations": {
            "tileset": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": ("pass", "qgis-ui-ui-op-3dtiles-tileset"), "pyqgis": ("pass", "pyqgis-3dtiles")},
        },
    },
    {
        "protocol": "elevation", "version": "Esri",
        "operations": {
            # The old cause - "no elevation service published; 404" - was wrong:
            # the Elevation protocol was simply absent from the fixture's enabled
            # list. Restored in tests/seed/client-compat-v1.sql, and
            # /elevation/0/value now answers 200. The native surface is reachable
            # and merely unexercised; the Esri elevation identity is a real gap.
            "point-query": {"pro-ui": NS,
                            "arcpy": ("pass", "arcpy-elevation-point-query"),
                            "qgis-ui": ("n/a-no-client", "qgis-no-imageserver-raster"),
                            "pyqgis": ("n/a-no-client", "qgis-no-imageserver-raster")},
        },
    },
]


EXCLUSION_REVIEW_REPORT = "docs/gis/client-exclusion-audit-2026-09-20.md"

# Preserve the original claims in MATRIX/CITE and in each reopened cell. These
# citations cannot close an operation until operation-specific review replaces
# them. A module/provider inventory is not an exhaustive client capability test.
EXCLUSIONS_REQUIRING_REVIEW = {
    "arcpy-modules": "A module-list page does not establish absence of a tool or layer-file path; installed MakeWCSLayer disproves this premise for WCS.",
    "arcpy-mp-web-service-types": "Failure through one addDataFromPath method does not exclude saved layers, connection files or geoprocessing tools.",
    "qgis-registry": "The citation names no missing provider or receipt; shared GDAL/OGR providers must also be checked. Installed GDAL includes OGCAPI.",
    "qgis-wcs-provider": "The dedicated WCS provider's version limit does not exclude the bundled GDAL provider: a fresh QgsRasterLayer/GDAL WCS 2.0.1 diagnostic loads 64x64 and reads a non-NoData pixel.",
    "pro-wcs-versions": "Default negotiation of 2.0.1 does not exclude explicitly selecting WCS 1.0.0.",
    "pro-oapi-tiles-map-only": "A vector-only fixture is not proof that Pro lacks the documented map-tiles client; provision or verify a map tileset.",
    "pro-ogc-classic": "An OGC classic service list cannot establish absence of OData support.",
    "arcpy-no-soap": "The broad REST-only premise conflicts with the existing ArcPy GP SOAP workflow; catalog discovery needs a specific probe.",
}

FOLLOWUP_REVIEW_REPORT = "docs/gis/client-exclusion-followup-2026-09-20.md"
FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW = {
    "arcpy-no-candidates": "The installed and documented Locator class exposes geocode and suggest; fresh native calls against Honua return independently validated candidates and suggestions.",
    "arcpy-rest-only-ops": "The citation is a harness exclusion rule, not capability evidence. ExportAttachments/AddAttachments exist and mapping objects come from factories; attachment/relationship fixtures and native mapping requests need specific review.",
    "arcpy-no-replica": "CreateReplica's input contract does not establish absence of every native offline path. CreateReplicaFromServer targets a different GeoDataServer protocol and cannot settle FeatureServer replica/sync.",
    "arcpy-wfs-read-only": "The WFSToFeatureClass parameter list bounds one conversion tool, not native saved layers, connections or licensed extension paths for these operations.",
    "arcpy-no-wms-identify": "Missing MakeWMSLayer and module-level identify names do not cover factory-returned mapping objects; the exact GetFeatureInfo request path needs review.",
    "arcpy-no-geometryserver": "Module-name matching is not a complete native client inventory. Review concrete native tools and requests; local geometry calculations alone cannot certify remote GeometryServer operations.",
    "pro-no-sta": "An OGC API menu's supported standards do not establish absence of SensorThings through every native layer, representation or extension path.",
    "pro-ogcapi": "The native OGC API connection menu supports Features/Tiles; alternative native representations, saved layers and licensed extensions need operation-specific review before excluding other APIs.",
    "qgis-no-tilejson": "Stock QgsVectorTileUtils.updateUriSources consumes remote TileJSON through a standard Mapbox GL style source.url; direct XYZ descriptor failure does not establish no client.",
    "qgis-no-imageserver-raster": "Stock arcgismapserver explicitly supports ImageServer rendering, and stock GDAL AGS reads numeric TIFF pixels. Dynamic 0x0 dimensions and the narrower identify-parser failure do not exclude elevation sampling.",
    "qgis-gp-algorithms": "No specialized GP processing entry was found, but a provider/algorithm registry scan is not operation-specific proof for all discovery/job paths. The broader native SDK scope needs a retained source/request inventory.",
    "qgis-no-esri-locator": "The old receipt asserts stock QGIS has no Esri locator without an operation-specific native API/source inventory; fresh inventory has not found a specialized path but cannot justify the universal claim.",
    "qgis-local-geometry": "Observed local GEOS/GDAL computation does not by itself exclude every remote native SDK path. A remote-operation source/request inventory is still required.",
    "qgis-no-versioning": "A missing dedicated provider or UI is not an operation-specific review of SDK and connection paths for create/reconcile/post; retain the work until the citation is sufficient.",
    "qgis-rest-only": "REST discovery observed in one provider does not prove absence of every native SOAP catalog path; review exact request builders and shared providers.",
}
EXCLUSIONS_REQUIRING_REVIEW.update(FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW)


# Operation-specific native receipts can resolve an audited exclusion while
# retaining both the original claim and the intervening review state.
RESOLVED_EXCLUSION_EVIDENCE = {
    ("stac", "1.0.0", operation, "arcpy"): (
        "honua-esri-compat/evidence/arcpy-stac-metadata-20260920-d/observations.json "
        "(retained at honua-esri-compat commit 1490b03); "
        f"GetSTACInfo {operation}: native metadata validated against separate HTTP and fixture controls; "
        "installed ArcGIS Pro 3.7.1.1904 executable SHA-256 bound alongside ArcPy 3.7.1 build 1901; "
        "server 6ac9debbccdd716639db89ce1f36821472f5018e / image 737851273827; "
        "Development JIT SDK evidence, no UI credit"
    )
    for operation in ("catalog-landing", "collections", "item-search")
}


RESOLVED_EXCLUSION_EVIDENCE.update({
    ("wcs", "2.0.1", operation, "pyqgis"): (
        "honua-client-compat/evidence/pyqgis-sdk-roundtrip-20260920-i/observations.json (retained at honua-client-compat commit 2452daa); "
        f"{operation}, stock PyQGIS 3.44.14-Solothurn / GDAL 3.13.3: fresh native capabilities and description caches, "
        "four-corner pixel reads before/after QgsProject reload, separate full/subset TIFF grid controls against SQL; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; "
        "Development JIT SDK evidence, no UI credit"
    )
    for operation in ("GetCapabilities", "DescribeCoverage", "GetCoverage")
})


RESOLVED_EXCLUSION_EVIDENCE.update({
    ("geocodeserver", "GeoServices REST", operation, "arcpy"): (
        "honua-esri-compat/evidence/arcpy-exclusion-followup-20260920-d/observations.json (retained at commit b691b03); "
        f"native Locator {operation}: ten geocode candidates or five suggestions validated against separate HTTP controls, "
        "including addresses/scores/WGS84 XY or suggestion texts/magic keys/collection flags; geocode forStorage=False; "
        "ArcGIS Pro 3.7.1.1904 executable version/hash bound alongside ArcPy 3.7.1 build 1901; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; Development JIT SDK evidence, no UI credit"
    )
    for operation in ("findAddressCandidates", "suggest")
})

for protocol, version, operation, detail in (
    ("tilejson", "3.0.0", "descriptor", "stock updateUriSources consumes a remote TileJSON URL from a standard Mapbox GL style; exact template and native decoded feature geometry validated within MVT grid tolerance; direct descriptor-as-XYZ still fails"),
    ("imageserver", "GeoServices REST", "service-info", "stock arcgismapserver constructor CRS and extent match independent ImageServer metadata"),
    ("imageserver", "GeoServices REST", "exportImage", "stock arcgismapserver 64x64 ARGB block corner colors match independent exportImage PNG; dynamic provider 0x0 native dimensions do not prevent rendering"),
    ("elevation", "Esri", "point-query", "stock GDAL AGS TIFF numeric sample 10 at [-122.498828125,37.83890625] matches independent ImageServer identify at the same point; four corners/affine grid match SQL and native project reload repeats samples; does not certify native identify endpoint"),
):
    RESOLVED_EXCLUSION_EVIDENCE[(protocol, version, operation, "pyqgis")] = (
        "honua-client-compat/evidence/pyqgis-deep-exclusions-20260920-d/observations.json (retained at commit 046a74c); "
        f"PyQGIS 3.44.14-Solothurn / GDAL 3.13.3: {detail}; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; Development JIT SDK evidence, no UI credit"
    )

# Only these two operations have fresh inspected native GUI receipts. The later
# Properties hang leaves identify, elevation and TileJSON UI obligations open.
for operation, case_id, detail in (
    ("service-info", "UI-OP-IMAGESERVER-SERVICE-INFO", "native ArcGIS REST connection tree discovery and added layer information: ImageServer URL, EPSG:4326 and fixture extent inspected"),
    ("exportImage", "UI-OP-IMAGESERVER-EXPORT-IMAGE", "native added ImageServer layer canvas inspected against independent PNG control; QGIS/34414 exportImage HTTP200,765x724 PNG2682bytes at2026-09-20T09:37:31Z, trace6f136fe7c94227895d3b74a229d701ed"),
):
    RESOLVED_EXCLUSION_EVIDENCE[("imageserver", "GeoServices REST", operation, "qgis-ui")] = (
        "honua-client-compat/evidence/native-qgis-image-tilejson-20260920-a/results.json "
        "(retained at honua-client-compat commit 2312dd99970f07b7ca0d38657e1c94a9b632edd3); "
        f"{case_id}, QGIS 3.44.14-Solothurn, windows-computer-use inspected checkpoint receipts: {detail}; "
        "plan SHA256 790e0e594128c2ccbb86c674d4735468671b4505d17207f3d2576548e8936f9f; "
        "server 25fa17d9cfa72340c9de4a33a743f19ff0911800 / image 0ad6f6c9d81e; "
        "existing native authentication configuration reference; not an anonymous-native claim; Development JIT UI diagnostic evidence, no shipping NativeAOT claim"
    )


REVIEWED_PASS_GAPS = {
    ("featureserver", "GeoServices REST", "statistics", "arcpy"): (
        "docs/gis/arcpy-local-calculation-verdict-audit-2026-09-20.md: "
        "Historical receipt counts SearchCursor rows locally; it does not prove "
        "a native outStatistics/groupByFieldsForStatistics request. Fresh remote "
        "aggregate evidence and independent result assertions are required."
    ),
}


def build_rows() -> list[dict]:
    rows: list[dict] = []
    for entry in MATRIX:
        for operation, lanes in entry["operations"].items():
            cells = {}
            for lane in LANES:
                raw = lanes[lane]
                if isinstance(raw, str):
                    state, ref, issue = raw, None, None
                elif len(raw) == 3:
                    state, ref, issue = raw
                else:
                    state, ref = raw
                    issue = None
                cell = {"state": state}
                if issue:
                    cell["issue"] = issue
                if ref in CITE:
                    cell["citation"] = CITE[ref]
                elif ref in EV:
                    cell["evidence"] = EV[ref]
                elif ref is not None:
                    cell["cause"] = ref
                if state.startswith("n/a-") and ref in EXCLUSIONS_REQUIRING_REVIEW:
                    cell["previous_exclusion"] = {
                        "state": state,
                        "citation": cell["citation"],
                    }
                    cell["state"] = "blocked"
                    review_report = (FOLLOWUP_REVIEW_REPORT
                                     if ref in FOLLOWUP_EXCLUSIONS_REQUIRING_REVIEW
                                     else EXCLUSION_REVIEW_REPORT)
                    cell["citation"] = (
                        f"{review_report}: exclusion evidence review pending. "
                        + EXCLUSIONS_REQUIRING_REVIEW[ref]
                    )
                resolution = RESOLVED_EXCLUSION_EVIDENCE.get(
                    (entry["protocol"], entry["version"], operation, lane))
                if resolution:
                    if "previous_exclusion" not in cell:
                        raise ValueError("An exclusion resolution must preserve its original claim")
                    cell["previous_review"] = {
                        "state": cell["state"],
                        "citation": cell.pop("citation"),
                    }
                    cell["state"] = "pass"
                    cell["evidence"] = resolution
                pass_gap = REVIEWED_PASS_GAPS.get(
                    (entry["protocol"], entry["version"], operation, lane))
                if pass_gap:
                    if cell["state"] != "pass":
                        raise ValueError("A reviewed pass gap must preserve a previous pass")
                    cell["previous_pass"] = {
                        "state": cell["state"],
                        "evidence": cell.pop("evidence"),
                    }
                    cell["state"] = "blocked"
                    cell["citation"] = pass_gap
                cells[lane] = cell
            rows.append({
                "protocol": entry["protocol"],
                "version": entry["version"],
                "operation": operation,
                "lanes": cells,
            })
    return rows


def validate(rows: list[dict]) -> list[str]:
    problems: list[str] = []
    for row in rows:
        where = f"{row['protocol']} {row['version']} {row['operation']}"
        for lane in LANES:
            cell = row["lanes"].get(lane)
            if cell is None:
                problems.append(f"{where}: lane {lane} has no cell")
                continue
            state = cell.get("state")
            if state not in STATES:
                problems.append(f"{where}/{lane}: unknown state {state!r}")
                continue
            if state.startswith("n/a-") and cell.get("citation") in {
                CITE[ref] for ref in EXCLUSIONS_REQUIRING_REVIEW
            }:
                problems.append(
                    f"{where}/{lane}: disputed exclusion requires operation-specific evidence review")
            if state in NEEDS_CITATION and not (
                cell.get("citation") or cell.get("cause")
            ):
                problems.append(
                    f"{where}/{lane}: state {state} requires a citation or a named cause")
            if state == "fail" and not cell.get("issue"):
                problems.append(
                    f"{where}/{lane}: a fail requires a filed issue reference. A "
                    "client-visible defect with a receipt and no issue is a bug "
                    "nobody is tracking.")
            if state == "fail" and not cell.get("evidence"):
                problems.append(
                    f"{where}/{lane}: a fail requires the receipt that observed it")
            if state == "pass":
                evidence = cell.get("evidence")
                if ((row["protocol"], row["version"], row["operation"], lane)
                        in REVIEWED_PASS_GAPS and evidence == EV["arcpy-featureserver-statistics"]):
                    problems.append(
                        f"{where}/{lane}: reviewed local calculation cannot certify a remote operation")
                if not evidence:
                    problems.append(
                        f"{where}/{lane}: a pass requires an evidence reference")
                elif not any(token in evidence for token in CERTIFIED_BUILD_TOKENS):
                    problems.append(
                        f"{where}/{lane}: a pass must name a build under "
                        f"certification {CERTIFIED_BUILD_TOKENS}, got {evidence!r}")
    return problems


def render_markdown(rows: list[dict], summary: dict) -> str:
    """Render the per-lane totals and the full cell table."""
    lines: list[str] = [
        DOC_BEGIN,
        "",
        "<!-- Generated by scripts/certification/build-client-checklist.py."
        + " Do not edit by hand. -->",
        "",
        "### Totals",
        "",
        "| Lane | Client build | Closed | Open | Breakdown |",
        "|---|---|---|---|---|",
    ]
    for lane in LANES:
        totals = summary["per_lane"][lane]
        closed = sum(count for state, count in totals.items() if state in CLOSED_STATES)
        opened = sum(count for state, count in totals.items() if state not in CLOSED_STATES)
        breakdown = ", ".join(
            f"{state} {totals[state]}" for state in sorted(totals))
        lines.append(
            f"| `{lane}` | {CLIENT_BUILDS[lane]} | {closed}/{closed + opened} | "
            f"{opened} | {breakdown} |"
        )

    overall = summary["overall"]
    lines += [
        "",
        f"**{overall['closed']} of {overall['cells']} cells closed; "
        f"{overall['open']} open.**",
        "",
        "### Cells",
        "",
        "A cell closes as `pass`, `n/a-no-client` or `n/a-superseded`. Every other",
        "value is open work. The full evidence reference or citation for each cell is",
        "in `docs/gis/data/client-certification-checklist.v1.json`.",
        "",
    ]

    ordered: list[tuple[str, str]] = []
    for row in rows:
        key = (row["protocol"], row["version"])
        if key not in ordered:
            ordered.append(key)

    for protocol, version in ordered:
        lines += [
            f"#### {protocol} {version}",
            "",
            "| Operation | " + " | ".join(f"`{lane}`" for lane in LANES) + " |",
            "|---" * (len(LANES) + 1) + "|",
        ]
        for row in rows:
            if (row["protocol"], row["version"]) != (protocol, version):
                continue
            cells = " | ".join(
                row["lanes"][lane]["state"] for lane in LANES)
            lines.append(f"| {row['operation']} | {cells} |")
        lines.append("")

    lines.append(DOC_END)
    return "\n".join(lines) + "\n"


def write_markdown(rows: list[dict], summary: dict) -> None:
    """Replace the generated region of DOC_PATH, appending it if absent."""
    generated = render_markdown(rows, summary)
    existing = DOC_PATH.read_text(encoding="utf-8")
    if DOC_BEGIN in existing and DOC_END in existing:
        head = existing.split(DOC_BEGIN)[0]
        tail = existing.split(DOC_END, 1)[1]
        updated = (
            head.rstrip("\n") + "\n\n" + generated.strip("\n") + "\n" + tail
        )
    else:
        updated = existing.rstrip("\n") + "\n\n" + generated
    # Normalise the ends, or the generated region's own trailing newline is
    # re-added on every run and the file is never byte-identical twice - which
    # would make the CI drift check fire on a no-op regeneration.
    DOC_PATH.write_text(
        updated.rstrip("\n") + "\n", encoding="utf-8", newline="\n")


def summarise(rows: list[dict]) -> dict:
    totals = {lane: {} for lane in LANES}
    for row in rows:
        for lane in LANES:
            state = row["lanes"][lane]["state"]
            totals[lane][state] = totals[lane].get(state, 0) + 1
    overall = {"cells": len(rows) * len(LANES), "closed": 0, "open": 0}
    for lane in LANES:
        for state, count in totals[lane].items():
            if state in CLOSED_STATES:
                overall["closed"] += count
            else:
                overall["open"] += count
    return {"per_lane": totals, "overall": overall}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true",
                        help="validate only; do not write the data file")
    args = parser.parse_args()

    rows = build_rows()
    problems = validate(rows)
    if problems:
        print(f"FAIL {len(problems)} checklist problem(s):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    summary = summarise(rows)
    document = {
        "schema_version": "1.0",
        "description": (
            "Four-lane client certification checklist. A cell closes as pass (with an "
            "evidence reference naming the client build) or n/a-* (with a "
            "vendor-documentation or provider-registry citation). blocked and "
            "not-started are open."
        ),
        "lanes": {lane: CLIENT_BUILDS[lane] for lane in LANES},
        "states": sorted(STATES),
        "closed_states": sorted(CLOSED_STATES),
        "summary": summary,
        "rows": rows,
    }

    if not args.check:
        DATA_PATH.parent.mkdir(parents=True, exist_ok=True)
        DATA_PATH.write_text(
            json.dumps(document, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8", newline="\n")
        print(f"wrote {DATA_PATH.relative_to(REPO_ROOT)}")
        write_markdown(rows, summary)
        print(f"wrote {DOC_PATH.relative_to(REPO_ROOT)}")

    overall = summary["overall"]
    print(f"OK  {len(rows)} operations x {len(LANES)} lanes = {overall['cells']} cells")
    print(f"    closed {overall['closed']}  open {overall['open']}")
    for lane in LANES:
        states = summary["per_lane"][lane]
        closed = sum(v for k, v in states.items() if k in CLOSED_STATES)
        total = sum(states.values())
        print(f"    {lane:9s} {closed:3d}/{total:3d} closed  " +
              "  ".join(f"{k}={v}" for k, v in sorted(states.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
