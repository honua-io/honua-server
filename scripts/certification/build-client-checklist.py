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
        "spatial filtering demonstrably works."
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
        "ogc-features", "1.0", 18,
        skipped=3),
    "pyqgis-wfs": _pyqgis(
        "wfs", "2.0.0", 12,
        skipped=4),
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
    "pyqgis-fs-info": _pyqgis("featureserver", "10.8", 4, cert_id="CERT-DISC-01"),
    "pyqgis-fs-meta": _pyqgis("featureserver", "10.8", 4, cert_id="CERT-SCHM-01"),
    "pyqgis-fs-query": _pyqgis("featureserver", "10.8", 4, cert_id="CERT-QFLT-01"),
    "pyqgis-fs-identify": _pyqgis("featureserver", "10.8", 4, cert_id="CERT-GEOM-01"),
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
STA_GATE = (
    "the SensorThings surface is not switched on in the client-compat fixture, so "
    "every path 404s (/sensorthings, /sensorthings/v1.1, .../Things, "
    ".../Observations). Unblocked by setting "
    "Capabilities:Experimental:serve.sensorthings:Enabled=true. Previously recorded "
    "as server bug #4202, which misattributed a configuration gap to a defect."
)

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

ESRI_ELEVATION_IDENTITY = (
    "the native /elevation surface answers 200 once the Elevation protocol is "
    "enabled, but no client treats this as an elevation service: getSamples is "
    "implemented and returns values, while serviceDataType is the hard-coded "
    "literal esriImageServiceDataTypeGeneric in ImageServerMetadataHandler and "
    "ImageServerSoapEndpoints. esriImageServiceDataTypeElevation and "
    "elevationInfo appear nowhere in src/, so making it data-driven is a server "
    "change, not a fixture one."
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
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                                "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-wms-caps")},
            "GetMap": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
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
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                                "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-wmts-caps")},
            "GetTile": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
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
            "GetCapabilities": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                                "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfs")},
            "DescribeFeatureType": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                                    "pyqgis": ("pass", "pyqgis-wfs")},
            "GetFeature": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                           "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-wfs")},
            "GetPropertyValue": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": NS},
            "Transaction-Insert": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": NS},
            "Transaction-Update": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": NS},
            "Transaction-Delete": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": NS},
            "ListStoredQueries": {"pro-ui": NS, "arcpy": ("n/a-no-client", "arcpy-wfs-read-only"), "qgis-ui": NS, "pyqgis": NS},
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
                                   "qgis-ui": NS, "pyqgis": NS},
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
            "catalog-landing": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                                "pyqgis": ("pass", "pyqgis-stac-landing")},
            "collections": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                            "pyqgis": ("pass", "pyqgis-stac-collections")},
            "item-search": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                            "pyqgis": ("pass", "pyqgis-stac-search")},
            "asset-download": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                               "pyqgis": ("pass", "pyqgis-stac-asset")},
        },
    },
    {
        "protocol": "sensorthings", "version": "1.1",
        "operations": {
            "entity-sets": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                            "arcpy": ("n/a-no-client", "arcpy-modules"),
                            "qgis-ui": _blocked(STA_GATE),
                            "pyqgis": NS},
            "expand": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                       "arcpy": ("n/a-no-client", "arcpy-modules"),
                       "qgis-ui": _blocked(STA_GATE), "pyqgis": NS},
            "filter-paging": {"pro-ui": ("n/a-no-client", "pro-no-sta"),
                              "arcpy": ("n/a-no-client", "arcpy-modules"),
                              "qgis-ui": _blocked(STA_GATE + " A paging/count defect "
                                                  "(#4200) was recorded against this "
                                                  "operation, but it cannot be "
                                                  "confirmed or retired until the "
                                                  "surface is switched on."),
                              "pyqgis": NS},
        },
    },
    {
        "protocol": "featureserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                             "qgis-ui": ("pass", "qgis-ltr"),
                             "pyqgis": ("pass", "pyqgis-fs-info")},
            "layer-metadata": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                               "qgis-ui": ("pass", "qgis-ltr"),
                               "pyqgis": ("pass", "pyqgis-fs-meta")},
            "query": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                      "qgis-ui": ("pass", "qgis-ltr"),
                      "pyqgis": ("pass", "pyqgis-fs-query")},
            "identify": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
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
                           "pyqgis": _blocked(
                               "addFeatures rejects the null system-maintained "
                               "objectid QGIS sends on insert (error 1006), so no "
                               "stock digitizing session can edit; the same request "
                               "without the member succeeds")},
            # Both fails are tracked. #5012 is the per-feature attachments POST
            # rejection that makes Pro report zero attachments; #5021 is the
            # V1-catalog compat synthesis dropping relationships, attachments and
            # VectorTileServer.
            "attachments": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5012"),
                "arcpy": NS, "qgis-ui": NS,
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "relatedRecords": {
                "pro-ui": ("fail", "pro-matrix", "honua-server#5021"),
                "arcpy": NS, "qgis-ui": NS,
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            # The server answers outStatistics correctly; QGIS never asks. It
            # downloads outFields=* and aggregates locally, so there is no client
            # request to certify.
            "statistics": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                           "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
            "domains": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                        "pyqgis": _blocked(
                            "queryDomains returns an empty domains array for all "
                            "three fixture services and no field on any "
                            "FeatureServer layer carries a domain member. The "
                            "provider does implement coded-value domains, so "
                            "seeding one coded-value domain on a string field "
                            "makes this cell certifiable.")},
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
                "qgis-ui": NS,
                "pyqgis": ("n/a-no-client", "qgis-rest-no-advanced")},
        },
    },
    {
        "protocol": "mapserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                             "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-ms-info")},
            "export": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                       "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-ms-export")},
            "identify": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                         "pyqgis": ("pass", "pyqgis-ms-identify")},
            "legend": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                       "pyqgis": ("pass", "pyqgis-ms-legend")},
        },
    },
    {
        "protocol": "imageserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": NS,
                             "qgis-ui": ("n/a-no-client", "qgis-registry"),
                             "pyqgis": ("n/a-no-client", "qgis-registry")},
            "exportImage": {"pro-ui": NS, "arcpy": NS,
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
            "identify": {"pro-ui": NS, "arcpy": NS,
                         "qgis-ui": ("n/a-no-client", "qgis-registry"),
                         "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "vectortileserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                             "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-vts-info")},
            "tile": {"pro-ui": ("pass", "pro-matrix"), "arcpy": NS,
                     "qgis-ui": NS, "pyqgis": ("pass", "pyqgis-vts-tile")},
            "style": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS,
                      "pyqgis": ("pass", "pyqgis-vts-style")},
        },
    },
    {
        "protocol": "gpserver", "version": "GeoServices REST",
        "operations": {
            "service-info": {"pro-ui": NS, "arcpy": NS,
                             "qgis-ui": ("n/a-no-client", "qgis-gp-algorithms"),
                             "pyqgis": ("n/a-no-client", "qgis-gp-algorithms")},
            "task-info": {"pro-ui": NS, "arcpy": NS,
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
            "reverseGeocode": {"pro-ui": NS, "arcpy": NS,
                               "qgis-ui": ("n/a-no-client", "qgis-no-esri-locator"),
                               "pyqgis": ("n/a-no-client", "qgis-no-esri-locator")},
            "geocodeAddresses": {"pro-ui": NS, "arcpy": NS,
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
                           "qgis-ui": NS, "pyqgis": NS},
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
                            "arcpy": _blocked(_experimental_gate("serve.i3s-scene")),
                            "qgis-ui": ("n/a-no-client", "qgis-registry"),
                            "pyqgis": ("n/a-no-client", "qgis-registry")},
        },
    },
    {
        "protocol": "3d-tiles", "version": "1.0",
        "operations": {
            "tileset": {"pro-ui": NS, "arcpy": NS, "qgis-ui": NS, "pyqgis": NS},
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
            "point-query": {"pro-ui": _blocked(ESRI_ELEVATION_IDENTITY),
                            "arcpy": _blocked(ESRI_ELEVATION_IDENTITY),
                            "qgis-ui": NS,
                            "pyqgis": NS},
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
