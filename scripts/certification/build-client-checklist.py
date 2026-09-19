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
* ``n/a`` is not a shrug. ArcGIS Pro genuinely has no SensorThings client and QGIS
  genuinely has no GPServer client; without a closable state for those the
  denominator could never reach zero, which invites fudging the numerator.

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
        "are therefore unreachable from this client however the server behaves."
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
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-GP-SERVICE-INFO, ArcGIS Pro/arcpy 3.7.1.1904: arcpy.ImportToolbox resolved https://host.docker.internal:18443/services;test_service and "
    ),
    "arcpy-gpserver-task-info": (
        "honua-esri-compat/evidence/arcpy-client-compat-20260918-c/certification/arcpy-client-compat-20260918-c-read-desktop-arcgis-gp-gpserver.cert.json - "
        "operations GP-OP-TASK-METADATA, ArcGIS Pro/arcpy 3.7.1.1904: task 'Buffer' publishes signature 'Buffer_testservice(wkb, srid, distance, {geodesic})' an"
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


# Two causes that were previously written as though they were external walls.
# Both are switches inside our own fixture, so they are stated as such: a cause
# that overstates the obstacle is how ready work stays parked.

def _experimental_gate(capability: str, extra: str = "") -> str:
    """Cause for a capability the fixture has switched off.

    GET /api/v1/capabilities/manifest reports these as
    reasonCode=experimental-disabled, and the 404 body names the config key
    itself. Recording them as missing artifacts or server defects - which is what
    four of these cells did - parks work that is one setting away from running.
    Fetch that manifest authenticated: anonymously 60 of 77 capabilities read as
    unavailable purely for lack of a token.
    """
    return (
        f"the {capability} capability is switched off in the client-compat "
        f"fixture (capability manifest reports "
        f"reasonCode=experimental-disabled). Unblocked by setting "
        f"Capabilities:Experimental:{capability}:Enabled=true." + extra
    )


COG_GAP = (
    "no COG-serving surface in this configuration. The cloud-raster catalog "
    "refuses the fixture's storage outright (Cog/Models/CogModels.cs: 'Local "
    "storage is not supported for COG serving', and the container runs "
    "FileStorage__Provider=Local); ImageServerExportHandler rejects "
    "RasterFormat.COG; and WCS / OGC-API-Coverages GetCoverage emit plain GTiff "
    "stamped Accept-Ranges: none. The only range-capable public route is the "
    "scene asset endpoint, so closing this needs a COG fixture served from there "
    "- not a client change, since QGIS opens a COG natively over /vsicurl/."
)

PGROUTING_GATE = (
    "pgRouting is not installed in the fixture's database image. It is available to "
    "it: postgis/postgis:16-3.4 is Debian bullseye with the PGDG repo already "
    "configured, and postgresql-16-pgrouting resolves to 3.8.0-1.pgdg110+1. "
    "Unblocked by a derived image that installs the package and creates the "
    "extension, not by waiting on anything upstream."
)


MATRIX: list[dict] = [
    {
        "protocol": "wms", "version": "1.3.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getcapabilities"),
                                "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-wms-caps")},
            "GetMap": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wms-getmap"),
                       "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wms-getmap")},
            "GetFeatureInfo": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-no-wms-identify"), "qgis-ui": NS,
                               "pyqgis": ("pass", "pyqgis-wms-featureinfo")},
            "GetLegendGraphic": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": NS,
                                 "pyqgis": ("pass", "pyqgis-wms-legend")},
            "styles": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wms-styles")},
            "time-dimension": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": NS,
                               "pyqgis": ("pass", "pyqgis-wms-time")},
        },
    },
    {
        "protocol": "wmts", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"),
                                "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                                "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-wmts-caps")},
            "GetTile": {"pro-ui": ("pass", "pro-matrix"),
                        "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wmts-gettile")},
            "GetFeatureInfo": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                               "qgis-ui": NS,
                               "pyqgis": ("pass", "pyqgis-wmts-featureinfo")},
            "RESTful-tile-path": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                  "qgis-ui": NS,
                                  "pyqgis": ("pass", "pyqgis-wmts-restful")},
        },
    },
    {
        "protocol": "wfs", "version": "2.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getcapabilities"),
                                "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfs")},
            "DescribeFeatureType": {"pro-ui": NS, "arcpy": ("pass", "arcpy-wfs-describefeaturetype"), "qgis-ui": NS,
                                    "pyqgis": ("pass", "pyqgis-wfs")},
            "GetFeature": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-wfs-getfeature"),
                           "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfs")},
            "GetPropertyValue": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
            "Transaction-Insert": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfst-insert")},
            "Transaction-Update": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfst-update")},
            "Transaction-Delete": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfst-delete")},
            "ListStoredQueries": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": ("n/a-no-client", "qgis-wfs-no-propertyvalue")},
        },
    },
    {
        "protocol": "wcs", "version": "1.0.0",
        "operations": {
            "GetCapabilities": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wcs")},
            "DescribeCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                                 "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wcs")},
            "GetCoverage": {"pro-ui": ("n/a-superseded", "pro-wcs-versions"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wcs")},
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
                             "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "conformance": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "items": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                      "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "item": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "bbox-datetime-filter": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                     "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "crs-negotiation": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif")},
            "transactions-part4": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                   "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-oapif-part4")},
        },
    },
    {
        "protocol": "ogc-api-tiles", "version": "1.0",
        "operations": {
            "landing-tilesets": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                                 "qgis-ui": ("n/a-no-client", "qgis-registry"),
                                 "pyqgis": ("n/a-no-client", "qgis-registry")},
            "tile": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"),
                     "qgis-ui": ("n/a-no-client", "qgis-registry"),
                     "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "stac", "version": "1.0.0",
        "operations": {
            "catalog-landing": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-stac-landing")},
            "collections": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": NS,
                            "pyqgis": ("pass", "pyqgis-stac-collections")},
            "item-search": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": NS,
                            "pyqgis": ("pass", "pyqgis-stac-search")},
            "asset-download": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-modules"), "qgis-ui": NS,
                               "pyqgis": ("pass", "pyqgis-stac-asset")},
        },
    },
    {
        "protocol": "sensorthings", "version": "1.1",
        "operations": {
            "entity-sets": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": NS,
                            "pyqgis": ("pass", "pyqgis-sta-entities")},
            "expand": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-sta-expand")},
            "filter-paging": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                              "arcpy": ("n/a-no-client", "arcpy-modules"),
                              "qgis-ui": NS,
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
            # QGIS serialises the unset system-maintained OID as
            # "objectid": null and the server rejects the whole edit with 1006
            # "Field 'objectid' cannot be null", while the identical request with
            # the member omitted succeeds. No stock QGIS digitizing session can
            # edit this FeatureServer. QGIS uses the per-operation addFeatures /
            # updateFeatures / deleteFeatures endpoints, never combined applyEdits.
            "applyEdits": {"pro-ui": NS, "arcpy": NS,
                           "qgis-ui": NS,
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
            "domains": {"pro-ui": NS, "arcpy": ("pass", "arcpy-featureserver-domains"), "qgis-ui": NS,
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
                "pro-ui": _blocked(_experimental_gate(
                    "sync.offline",
                    " The surface is implemented, not missing. Once enabled, the "
                    "residual limit to test is that replica delivery is Esri JSON "
                    "only, while Pro offline requests dataFormat=sqlite.")),
                "arcpy": ("n/a-no-client", "arcpy-no-replica"),
                "qgis-ui": ("n/a-no-client", "qgis-rest-no-advanced"),
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
        },
    },
    {
        "protocol": "mapserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-service-info"),
                             "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-ms-info")},
            "export": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-mapserver-export"),
                       "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-ms-export")},
            "identify": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": NS,
                         "pyqgis": ("pass", "pyqgis-ms-identify")},
            "legend": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-rest-only-ops"), "qgis-ui": NS,
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
                             "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-vts-info")},
            "tile": {"pro-ui": ("pass", "pro-matrix"), "arcpy": ("pass", "arcpy-vectortileserver-tile"),
                     "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-vts-tile")},
            "style": {"pro-ui": NS, "arcpy": ("pass", "arcpy-vectortileserver-style"), "qgis-ui": NS,
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
            "submitJob": {"pro-ui": NS, "arcpy": NS,
                          "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                          "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "job-status": {"pro-ui": NS, "arcpy": NS,
                           "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                           "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "results": {"pro-ui": NS, "arcpy": NS,
                        "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                        "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "cancel": {"pro-ui": NS, "arcpy": NS,
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
            "route-solve": {"pro-ui": _blocked(PGROUTING_GATE),
                            "arcpy": _blocked(PGROUTING_GATE),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "service-area": {"pro-ui": _blocked(PGROUTING_GATE),
                             "arcpy": _blocked(PGROUTING_GATE),
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "versionmanagementserver", "version": "GeoServices REST",
        "operations": {
            "create-version": {"pro-ui": _blocked("versioning.branch capability is experimental"),
                               "arcpy": _blocked("versioning.branch capability is experimental"),
                               "qgis-ui": ("n/a-no-client", "qgis-no-versioning"),
                               "pyqgis": ("n/a-no-client", "qgis-no-versioning")},
            "reconcile-post": {"pro-ui": _blocked("versioning.branch capability is experimental"),
                               "arcpy": _blocked("versioning.branch capability is experimental"),
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
                       "qgis-ui": NS,
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
                             "qgis-ui": NS,
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
            # QGIS opens a COG natively over HTTP (gdal provider, /vsicurl/), so
            # this is entirely a server-side gap - and not a 404 from a route that
            # exists: nothing in honua serves COG bytes with ranges in this
            # configuration.
            "range-read": {"pro-ui": NS, "arcpy": NS,
                           "qgis-ui": _blocked(COG_GAP),
                           "pyqgis": _blocked(COG_GAP)},
        },
    },
    {
        "protocol": "i3s-sceneserver", "version": "1.x",
        "operations": {
            # The 404 recorded here as "no scene published" is the capability gate
            # itself: the manifest reports serve.i3s-scene as
            # reasonCode=experimental-disabled. Whether a scene also needs
            # publishing cannot be established until the surface is switched on.
            "scene-layer": {"pro-ui": _blocked(_experimental_gate(
                                "serve.i3s-scene",
                                " Previously recorded as 'no scene published; "
                                "SceneServer returns code 404'; that 404 is the gate. "
                                "Whether a scene artifact is also required is "
                                "untestable until it is on.")),
                            "arcpy": ("pass", "arcpy-i3s-sceneserver-scene-layer"),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "3d-tiles", "version": "1.0",
        "operations": {
            "tileset": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-mp-web-service-types"),
                        "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-3dtiles")},
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
        " Do not edit by hand. -->",
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
