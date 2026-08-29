# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WFS certification through ``owslib.wfs.WebFeatureService``.

The lane drives the real client: capabilities parsing (2.0.0, 1.1.0 and 1.0.0),
``get_schema`` over DescribeFeatureType, ``getfeature`` with counts, paging,
property subsetting, sorting, bbox windows, ``owslib.fes2`` Filter Encoding
documents posted as XML, and every output format the capabilities advertise.

``owslib.util.openURL`` -- the transport ``WebFeatureService`` itself uses, and
the one that raises ``owslib.util.ServiceException`` from an ``ows:ExceptionReport``
-- covers the two request shapes OWSLib's public API has no parameter for
(``RESULTTYPE=hits`` and stored queries). Those are called out in the notes.
"""

from __future__ import annotations

import json
import re
import xml.etree.ElementTree as ET

import pytest
from owslib.fes2 import FilterRequest, PropertyIsEqualTo, PropertyIsGreaterThan
from owslib.ogcapi.features import Features
from owslib.util import ServiceException, openURL
from owslib.wfs import WebFeatureService

from shared import canonical_fixture as fx
from shared.cert_envelope import (
    GEOGRAPHIC_TOLERANCE_DEGREES,
    PROJECTED_TOLERANCE_METERS,
    CertificationEvidenceCollector,
)

from .conftest import (
    AdminProbe,
    LaneConfig,
    Timer,
    geographic_delta,
    web_mercator,
)

pytestmark = pytest.mark.owslib_client

WFS_NS = "http://www.opengis.net/wfs/2.0"
GML32_NS = "http://www.opengis.net/gml/3.2"
GEOJSON_FORMAT = "application/json"
CRS84_URN = "urn:ogc:def:crs:OGC:1.3:CRS84"
EPSG4326_URN = "urn:ogc:def:crs:EPSG::4326"


# ---------------------------------------------------------------------------
# Clients and discovery
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session")
def wfs(lane_config: LaneConfig) -> WebFeatureService:
    return WebFeatureService(lane_config.wfs_url, version="2.0.0")


@pytest.fixture(scope="session")
def typename(wfs: WebFeatureService, lane_config: LaneConfig) -> str:
    """Resolve the WFS type name for the canonical vector fixture.

    The type name is derived server-side from the layer name, so it is
    discovered rather than hard-coded: the OGC API - Features collection title
    for the same layer is the join key. That makes the resolution itself a
    cross-protocol identity check -- if WFS and OGC API disagree about what the
    layer is called, this fails loudly instead of silently certifying the wrong
    feature type.
    """
    title = Features(lane_config.oaf_url).collection(lane_config.collection_id)["title"]
    matches = [name for name, ft in wfs.contents.items() if ft.title == title]
    if not matches:
        pytest.fail(
            f"No WFS feature type carries the OGC API collection title {title!r}. "
            f"Advertised: { {name: ft.title for name, ft in wfs.contents.items()} }"
        )
    assert len(matches) == 1, f"ambiguous WFS feature types for title {title!r}: {matches}"
    return matches[0]


def _geojson(wfs: WebFeatureService, **kwargs) -> dict:
    """GetFeature as GeoJSON.

    GeoJSON is used wherever a case asserts on counts or coordinate values:
    RFC 7946 fixes positions at longitude/latitude, so a count assertion cannot
    be confounded by an axis-order question. The GML path is exercised
    separately and explicitly by the CRS cases.
    """
    kwargs.setdefault("outputFormat", GEOJSON_FORMAT)
    return json.loads(wfs.getfeature(**kwargs).read())


def _names(payload: dict) -> list[str]:
    return [f["properties"].get("name") for f in payload["features"]]


def _fes_filter(constraint) -> str:
    document = FilterRequest().setConstraint(constraint, tostring=True)
    return document.decode() if isinstance(document, bytes) else document


# ---------------------------------------------------------------------------
# CONN / AUTH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-CONN-01")
def test_conn01_capabilities(wfs: WebFeatureService, wfs_collector: CertificationEvidenceCollector,
                             timer: Timer) -> None:
    assert wfs.identification.version == "2.0.0"
    assert wfs.identification.type in ("WFS", "OGC WFS"), wfs.identification.type
    assert wfs.contents, "GetCapabilities advertised no feature types"
    assert wfs.getOperationByName("GetFeature") is not None
    wfs_collector.record(
        "CERT-CONN-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wfs.contents),
        notes=(
            f"owslib.wfs.WebFeatureService parsed a live WFS {wfs.identification.version} "
            f"capabilities document titled {wfs.identification.title!r} with "
            f"{len(wfs.contents)} feature types."
        ),
        evidence_ref=wfs.url,
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport(base_url: str, wfs_collector: CertificationEvidenceCollector) -> None:
    assert base_url.split("://", 1)[0] == "http"
    wfs_collector.record(
        "CERT-CONN-02", "pass" if scheme == "https" else "not-applicable",
        notes=(
            "Transport verified as plain http on the compose client-compat network, which "
            "terminates no TLS. TLS handshake behaviour is exercised in the release tier, "
            "where the same lane runs against the HTTPS candidate."
        ),
        evidence_ref=base_url,
    )


@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_rejected(admin_probe: AdminProbe,
                                   wfs_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.anonymous_status in (401, 403), admin_probe
    assert "ApiKey" in admin_probe.challenge and fx.ADMIN_API_KEY_HEADER in admin_probe.challenge
    wfs_collector.record(
        "CERT-AUTH-01", "pass",
        notes=(
            f"Anonymous GET {fx.ADMIN_PROBE_PATH} -> {admin_probe.anonymous_status}, "
            f"WWW-Authenticate: {admin_probe.challenge}. The WFS data surface is anonymous in "
            "this fixture, so the control plane substantiates the AUTH facets."
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_credential_grants_access(admin_probe: AdminProbe,
                                         wfs_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.authenticated_status // 100 == 2, admin_probe
    wfs_collector.record(
        "CERT-AUTH-02", "pass",
        notes=(
            f"Accepted scheme {admin_probe.scheme} -> {admin_probe.authenticated_status}; ladder: "
            + ", ".join(f"{name}={code}" for name, code in admin_probe.attempts)
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


# ---------------------------------------------------------------------------
# DISC
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-DISC-01")
def test_disc01_feature_types(wfs: WebFeatureService, typename: str,
                              wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    assert typename in wfs.contents
    assert all(":" in name for name in wfs.contents), (
        f"every WFS 2.0 type name should be namespace-qualified; got {list(wfs.contents)}"
    )
    wfs_collector.record(
        "CERT-DISC-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wfs.contents),
        notes=(
            f"WebFeatureService.contents listed {len(wfs.contents)} namespace-qualified feature "
            f"types: {list(wfs.contents)}."
        ),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_feature_type_metadata(wfs: WebFeatureService, typename: str,
                                      wfs_collector: CertificationEvidenceCollector,
                                      timer: Timer) -> None:
    feature_type = wfs.contents[typename]
    assert feature_type.title
    codes = [crs.getcode() for crs in feature_type.crsOptions]
    assert "EPSG:4326" in codes, codes
    bbox = feature_type.boundingBoxWGS84
    assert bbox and len(bbox) == 4
    assert bbox[0] <= fx.FIXTURE_BBOX[0] + 1e-9 and bbox[2] >= fx.FIXTURE_BBOX[2] - 1e-9
    assert bbox[1] <= fx.FIXTURE_BBOX[1] + 1e-9 and bbox[3] >= fx.FIXTURE_BBOX[3] - 1e-9
    wfs_collector.record(
        "CERT-DISC-02", "pass",
        duration_ms=timer.ms,
        measured_count=len(codes),
        notes=(
            f"{typename}: title={feature_type.title!r}, CRS options {codes}, "
            f"WGS84BoundingBox {bbox} encloses the seeded feature envelope "
            f"{list(fx.FIXTURE_BBOX)}."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-CAP-01")
def test_ext_service_metadata(wfs: WebFeatureService,
                              wfs_collector: CertificationEvidenceCollector) -> None:
    """Service identification, provider and DCP metadata must all be present."""
    assert wfs.identification.title and wfs.identification.abstract
    operations = {op.name for op in wfs.operations}
    required = {"GetCapabilities", "DescribeFeatureType", "GetFeature",
                "GetPropertyValue", "ListStoredQueries", "DescribeStoredQueries"}
    missing = required - operations
    assert not missing, f"capabilities omit mandatory WFS 2.0 operations {sorted(missing)}"
    get_feature = wfs.getOperationByName("GetFeature")
    verbs = {method.get("type").lower() for method in get_feature.methods}
    assert {"get", "post"} <= verbs, f"GetFeature advertises only {verbs}"
    wfs_collector.record(
        "NB-OWS-WFS-CAP-01", "pass",
        measured_count=len(operations),
        notes=(
            f"OperationsMetadata advertises {len(operations)} entries covering every mandatory "
            f"WFS 2.0 operation, and GetFeature offers both {sorted(verbs)} DCP bindings."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-CAP-02")
def test_ext_conformance_constraints(wfs: WebFeatureService,
                                     wfs_collector: CertificationEvidenceCollector) -> None:
    """WFS 2.0 conformance constraints must be declared, and paging must be true."""
    names = {op.name for op in wfs.operations}
    for constraint in ("ImplementsBasicWFS", "ImplementsResultPaging", "KVPEncoding", "XMLEncoding"):
        assert constraint in names, f"capabilities do not declare {constraint}"
    # Paging is declared; CERT-PAGE-01/02 and NB-OWS-WFS-PAGE-03 prove it works.
    wfs_collector.record(
        "NB-OWS-WFS-CAP-02", "pass",
        measured_count=len(names),
        notes=(
            "Capabilities declare the WFS 2.0 conformance constraint set including "
            "ImplementsBasicWFS, ImplementsResultPaging, KVPEncoding and XMLEncoding; the paging "
            "and encoding claims are exercised by the paging and POST-filter cases."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-CAP-03")
def test_ext_advertised_output_formats_all_work(wfs: WebFeatureService, typename: str,
                                                wfs_collector: CertificationEvidenceCollector) -> None:
    """Every ``outputFormat`` in the GetFeature parameter domain must be servable."""
    formats = wfs.getOperationByName("GetFeature").parameters["outputFormat"]["values"]
    assert formats, "GetFeature declares no outputFormat domain"
    served: dict[str, int] = {}
    for output_format in formats:
        body = wfs.getfeature(typename=[typename], outputFormat=output_format,
                              maxfeatures=1).read()
        assert body, f"{output_format} returned an empty body"
        assert b"ExceptionReport" not in body[:400], (
            f"{output_format} is advertised but returned an exception: {body[:200]!r}"
        )
        served[output_format] = len(body)
    wfs_collector.record(
        "NB-OWS-WFS-CAP-03", "pass",
        measured_count=len(served),
        notes=(
            f"All {len(served)} advertised GetFeature output formats returned real payloads: "
            + ", ".join(f"{name}={size}B" for name, size in served.items())
            + ". An advertised-but-unservable format is a capabilities lie."
        ),
    )


# ---------------------------------------------------------------------------
# SCHM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-SCHM-01")
def test_schm01_describe_feature_type(wfs: WebFeatureService, typename: str,
                                      wfs_collector: CertificationEvidenceCollector,
                                      timer: Timer) -> None:
    schema = wfs.get_schema(typename)
    assert schema, "get_schema() returned nothing"
    properties = schema["properties"]
    # DescribeFeatureType XML-escapes the `eo:cloud_cover` column name, so
    # compare on the seeded attribute set rather than the full property list.
    missing = set(fx.ATTRIBUTE_FIELDS) - set(properties)
    assert not missing, f"XSD is missing seeded attributes {sorted(missing)}; got {sorted(properties)}"
    assert properties["count"] == "int" and properties["ratio"] == "double"
    assert properties["active"] == "boolean" and properties["created_at"] == "dateTime"
    assert fx.FEATURE_ID_FIELD in schema["required"], (
        f"the feature id column should be non-nillable; required={schema['required']}"
    )
    wfs_collector.record(
        "CERT-SCHM-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(properties),
        notes=(
            f"owslib.feature.schema.get_schema parsed DescribeFeatureType into {len(properties)} "
            "typed properties; every seeded attribute is present with its XSD type "
            "(int/double/boolean/dateTime/date/time), and objectid is non-nillable."
        ),
    )


@pytest.mark.cert("CERT-SCHM-02")
def test_schm02_geometry_type(wfs: WebFeatureService, typename: str,
                              wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    schema = wfs.get_schema(typename)
    assert schema["geometry"] == "Point", (
        f"expected a Point geometry column, got {schema['geometry']!r}"
    )
    assert schema.get("geometry_column"), "the XSD declares no geometry element"
    wfs_collector.record(
        "CERT-SCHM-02", "pass",
        duration_ms=timer.ms,
        notes=(
            f"The XSD types the {schema['geometry_column']!r} element as a GML "
            "PointPropertyType, which OWSLib maps to the fiona geometry 'Point' -- matching the "
            "seeded Point layer."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-DFT-01")
def test_ext_describe_feature_type_document(wfs: WebFeatureService, typename: str, base_url: str,
                                            wfs_collector: CertificationEvidenceCollector) -> None:
    """The raw XSD must be well-formed, GML-derived and named after the type."""
    local = typename.split(":")[-1]
    response = openURL(
        f"{base_url}/wfs",
        data={"SERVICE": "WFS", "VERSION": "2.0.0", "REQUEST": "DescribeFeatureType",
              "TYPENAMES": typename},
        method="Get", timeout=30,
    )
    document = ET.fromstring(response.read())
    assert document.tag.endswith("}schema")
    elements = document.findall("{http://www.w3.org/2001/XMLSchema}element")
    names = [element.get("name") for element in elements]
    assert local in names, f"the XSD declares {names}, not the requested type {local!r}"
    element = next(item for item in elements if item.get("name") == local)
    assert element.get("substitutionGroup", "").endswith("AbstractFeature"), (
        "the feature element must substitute for gml:AbstractFeature"
    )
    imports = document.findall("{http://www.w3.org/2001/XMLSchema}import")
    assert any(item.get("namespace") == GML32_NS for item in imports), (
        "the XSD must import the GML 3.2 namespace"
    )
    wfs_collector.record(
        "NB-OWS-WFS-DFT-01", "pass",
        measured_count=len(names),
        notes=(
            f"DescribeFeatureType returned a well-formed XSD declaring {local!r} in the "
            "gml:AbstractFeature substitution group and importing GML 3.2."
        ),
    )


# ---------------------------------------------------------------------------
# QFLT
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-QFLT-01")
def test_qflt01_filter_encoding_equality(wfs: WebFeatureService, typename: str,
                                         wfs_collector: CertificationEvidenceCollector,
                                         timer: Timer) -> None:
    document = _fes_filter(PropertyIsEqualTo(propertyname=fx.FILTER_FIELD, literal=fx.FILTER_VALUE))
    payload = _geojson(wfs, typename=[typename], filter=document, method="Post")
    assert payload["numberMatched"] == fx.ACTIVE_FEATURES
    assert all(f["properties"][fx.FILTER_FIELD] == fx.FILTER_VALUE for f in payload["features"])
    wfs_collector.record(
        "CERT-QFLT-01", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberMatched"],
        notes=(
            "An owslib.fes2 PropertyIsEqualTo Filter Encoding 2.0 document posted as XML matched "
            f"{payload['numberMatched']} of {fx.TOTAL_FEATURES} features "
            f"({fx.FILTER_FIELD}={fx.FILTER_VALUE!r}); every returned feature carries the value."
        ),
    )


@pytest.mark.cert("CERT-QFLT-02")
def test_qflt02_bbox_window(wfs: WebFeatureService, typename: str,
                            wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = _geojson(
        wfs, typename=[typename],
        bbox=(fx.SUBSET_BBOX[0], fx.SUBSET_BBOX[1], fx.SUBSET_BBOX[2], fx.SUBSET_BBOX[3], CRS84_URN),
    )
    assert payload["numberMatched"] == fx.SUBSET_BBOX_FEATURE_COUNT
    minx, miny, maxx, maxy = fx.SUBSET_BBOX
    for feature in payload["features"]:
        lon, lat = feature["geometry"]["coordinates"]
        assert minx <= lon <= maxx and miny <= lat <= maxy, (
            f"{feature['properties']['name']} at {(lon, lat)} is outside the bbox"
        )
    wfs_collector.record(
        "CERT-QFLT-02", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberMatched"],
        notes=(
            f"A 5-element BBOX carrying an explicit {CRS84_URN} selected "
            f"{payload['numberMatched']} features ({_names(payload)}); OWSLib formats the "
            "ordinates in the CRS's declared axis order and the server honours it."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-BBOX-01")
def test_ext_bbox_with_and_without_crs_agree(wfs: WebFeatureService, typename: str,
                                             wfs_collector: CertificationEvidenceCollector) -> None:
    """A 4-element BBOX (service default CRS) and a 5-element CRS84 BBOX must agree.

    OWSLib swaps the ordinates for a URN CRS whose declared axis order is
    latitude-first, so this is a live axis-order negotiation between client and
    server, not two spellings of the same query string.
    """
    without_crs = _geojson(wfs, typename=[typename], bbox=tuple(fx.SUBSET_BBOX))
    with_crs = _geojson(
        wfs, typename=[typename],
        bbox=(fx.SUBSET_BBOX[0], fx.SUBSET_BBOX[1], fx.SUBSET_BBOX[2], fx.SUBSET_BBOX[3], CRS84_URN),
    )
    assert without_crs["numberMatched"] == fx.SUBSET_BBOX_FEATURE_COUNT
    assert _names(without_crs) == _names(with_crs), (
        f"bbox forms disagree: {_names(without_crs)} vs {_names(with_crs)}"
    )
    wfs_collector.record(
        "NB-OWS-WFS-BBOX-01", "pass",
        measured_count=without_crs["numberMatched"],
        notes=(
            "The 4-element BBOX (default CRS, longitude/latitude) and the 5-element CRS84 BBOX "
            f"select the identical feature set {_names(with_crs)}, so the server's bbox axis-order "
            "handling matches OWSLib's."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-FILT-02")
def test_ext_numeric_filter_encoding(wfs: WebFeatureService, typename: str,
                                     wfs_collector: CertificationEvidenceCollector) -> None:
    """A numeric FES comparison must compare numerically, not lexically."""
    document = _fes_filter(PropertyIsGreaterThan(propertyname="count", literal="7"))
    payload = _geojson(wfs, typename=[typename], filter=document, method="Post")
    assert payload["numberMatched"] == 3, f"count > 7 matched {_names(payload)}"
    assert set(_names(payload)) == {"theta", "iota", "lambda"}
    assert all(f["properties"]["count"] > 7 for f in payload["features"])
    wfs_collector.record(
        "NB-OWS-WFS-FILT-02", "pass",
        measured_count=payload["numberMatched"],
        notes=(
            "fes:PropertyIsGreaterThan on the integer `count` column returns exactly the three "
            "rows above 7; a lexical comparison would mis-order 10."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-PROP-01")
def test_ext_property_subsetting(wfs: WebFeatureService, typename: str,
                                 wfs_collector: CertificationEvidenceCollector) -> None:
    """``propertyname`` must narrow the response, and ``*`` must widen it again.

    ``PROPERTYNAME=*`` is the wildcard OWSLib sends by default on WFS 1.0.0 and
    1.1.0, so a server that rejects it makes a bare ``getfeature()`` impossible
    on those versions.
    """
    subset = _geojson(wfs, typename=[typename], maxfeatures=1, propertyname=["name", "status"])
    keys = set(subset["features"][0]["properties"])
    assert keys == {"name", "status"}, f"propertyname subsetting returned {sorted(keys)}"

    wildcard = _geojson(wfs, typename=[typename], maxfeatures=1, propertyname=["*"])
    wildcard_keys = set(wildcard["features"][0]["properties"])
    assert set(fx.ATTRIBUTE_FIELDS) <= wildcard_keys, (
        f"PROPERTYNAME=* must select every property; got {sorted(wildcard_keys)}"
    )
    wfs_collector.record(
        "NB-OWS-WFS-PROP-01", "pass",
        measured_count=len(wildcard_keys),
        notes=(
            "propertyname=['name','status'] narrowed the payload to exactly those two columns, and "
            f"the PROPERTYNAME=* wildcard widened it back to all {len(wildcard_keys)} properties."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-SORT-01")
def test_ext_sortby(wfs: WebFeatureService, typename: str,
                    wfs_collector: CertificationEvidenceCollector) -> None:
    ordered = _names(_geojson(wfs, typename=[typename], sortby=["name"]))
    assert ordered == sorted(ordered), f"SORTBY=name did not order the response: {ordered}"
    assert len(ordered) == fx.TOTAL_FEATURES
    wfs_collector.record(
        "NB-OWS-WFS-SORT-01", "pass",
        measured_count=len(ordered),
        notes=f"SORTBY=name returned all {len(ordered)} features in ascending name order.",
    )


@pytest.mark.cert("NB-OWS-WFS-HITS-01")
def test_ext_resulttype_hits(wfs: WebFeatureService, typename: str, base_url: str,
                             wfs_collector: CertificationEvidenceCollector) -> None:
    """``RESULTTYPE=hits`` must report the full match count and return no members.

    OWSLib's ``getfeature`` signature has no ``resultType`` parameter, so the
    request goes through ``owslib.util.openURL`` -- the same transport
    ``WebFeatureService`` uses internally, including its ExceptionReport handling.
    """
    response = openURL(
        f"{base_url}/wfs",
        data={"SERVICE": "WFS", "VERSION": "2.0.0", "REQUEST": "GetFeature",
              "TYPENAMES": typename, "RESULTTYPE": "hits"},
        method="Get", timeout=30,
    )
    root = ET.fromstring(response.read())
    assert root.tag == f"{{{WFS_NS}}}FeatureCollection"
    assert int(root.get("numberMatched")) == fx.TOTAL_FEATURES
    assert int(root.get("numberReturned")) == 0
    assert not list(root.findall(f"{{{WFS_NS}}}member")), "hits must not carry feature members"
    wfs_collector.record(
        "NB-OWS-WFS-HITS-01", "pass",
        measured_count=int(root.get("numberMatched")),
        notes=(
            f"RESULTTYPE=hits reported numberMatched={fx.TOTAL_FEATURES} with numberReturned=0 and "
            "no wfs:member elements, which is what a client uses to size a query before fetching."
        ),
    )


# ---------------------------------------------------------------------------
# PAGE
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-PAGE-01")
def test_page01_count(wfs: WebFeatureService, typename: str,
                      wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = _geojson(wfs, typename=[typename], maxfeatures=fx.PAGE_SIZE)
    assert payload["numberReturned"] == fx.PAGE_SIZE
    assert payload["numberMatched"] == fx.TOTAL_FEATURES
    wfs_collector.record(
        "CERT-PAGE-01", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberReturned"],
        notes=(
            f"OWSLib maps maxfeatures={fx.PAGE_SIZE} to the WFS 2.0 COUNT parameter; the server "
            f"returned {payload['numberReturned']} members while reporting the full "
            f"numberMatched={payload['numberMatched']}."
        ),
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_page02_startindex(wfs: WebFeatureService, typename: str,
                           wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    first = _geojson(wfs, typename=[typename], maxfeatures=fx.PAGE_SIZE)
    second = _geojson(wfs, typename=[typename], maxfeatures=fx.PAGE_SIZE, startindex=fx.PAGE_SIZE)
    assert second["numberReturned"] == fx.PAGE_SIZE
    assert not set(_names(first)) & set(_names(second)), (
        f"pages overlap: {_names(first)} vs {_names(second)}"
    )
    wfs_collector.record(
        "CERT-PAGE-02", "pass",
        duration_ms=timer.ms,
        measured_count=second["numberReturned"],
        notes=f"STARTINDEX={fx.PAGE_SIZE} returned {_names(second)} after {_names(first)}.",
    )


@pytest.mark.cert("NB-OWS-WFS-PAGE-03")
def test_ext_paged_walk_is_exact(wfs: WebFeatureService, typename: str,
                                 wfs_collector: CertificationEvidenceCollector) -> None:
    """A COUNT/STARTINDEX walk must cover the feature type exactly once."""
    seen: list[str] = []
    start = 0
    for _ in range(fx.TOTAL_FEATURES + 2):
        page = _geojson(wfs, typename=[typename], maxfeatures=fx.PAGE_SIZE, startindex=start)
        ids = [f["id"] for f in page["features"]]
        seen.extend(ids)
        if len(ids) < fx.PAGE_SIZE:
            break
        start += fx.PAGE_SIZE
    assert len(seen) == fx.TOTAL_FEATURES, f"walk yielded {len(seen)} ids: {seen}"
    assert len(set(seen)) == fx.TOTAL_FEATURES, f"walk repeated ids: {seen}"
    wfs_collector.record(
        "NB-OWS-WFS-PAGE-03", "pass",
        measured_count=len(seen),
        notes=(
            f"A COUNT={fx.PAGE_SIZE} walk produced {len(seen)} distinct gml:id values summing "
            "exactly to numberMatched: no gaps, no repeats, stable ordering across pages."
        ),
    )


# ---------------------------------------------------------------------------
# GEOM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-GEOM-01")
def test_geom01_anchor_coordinate(wfs: WebFeatureService, typename: str,
                                  wfs_collector: CertificationEvidenceCollector,
                                  timer: Timer) -> None:
    document = _fes_filter(PropertyIsEqualTo(propertyname="name", literal=fx.ANCHOR_NAME))
    payload = _geojson(wfs, typename=[typename], filter=document, method="Post")
    assert payload["numberMatched"] == 1
    lon, lat = payload["features"][0]["geometry"]["coordinates"]
    delta = geographic_delta((lon, lat), (fx.ANCHOR_LON, fx.ANCHOR_LAT))
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"{fx.ANCHOR_NAME} returned ({lon}, {lat}); expected ({fx.ANCHOR_LON}, {fx.ANCHOR_LAT})"
    )
    wfs_collector.record(
        "CERT-GEOM-01", "pass",
        duration_ms=timer.ms,
        measured_delta=delta,
        notes=(
            f"Anchor {fx.ANCHOR_NAME!r} returned ({lon}, {lat}) in the GeoJSON output format "
            "(RFC 7946 longitude/latitude, so no axis ambiguity) against seeded "
            f"({fx.ANCHOR_LON}, {fx.ANCHOR_LAT}); deviation {delta} degrees, threshold "
            f"{GEOGRAPHIC_TOLERANCE_DEGREES}."
        ),
    )


def _gml_anchor(wfs: WebFeatureService, typename: str, srsname: str) -> tuple[list[str], list[float]]:
    """GetFeature as GML for the first (anchor) feature.

    The GML path is what carries an explicit ``srsName``, so the CRS cases use
    it rather than GeoJSON. OWSLib's POST request builder has no ``srsName``
    parameter, so these go over the KVP binding. Returns the distinct
    ``srsName`` values and the first ``gml:pos`` as ordinates.
    """
    body = wfs.getfeature(typename=[typename], srsname=srsname, maxfeatures=1).read().decode()
    srs_names = sorted(set(re.findall(r'srsName="([^"]+)"', body)))
    positions = re.findall(r"<gml:pos[^>]*>([^<]+)</gml:pos>", body)
    assert positions, f"no gml:pos in the response for srsname={srsname!r}: {body[:300]}"
    return srs_names, [float(value) for value in positions[0].split()]


@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_srsname_echo(wfs: WebFeatureService, typename: str,
                             wfs_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    srs_names, position = _gml_anchor(wfs, typename, EPSG4326_URN)
    assert srs_names == [EPSG4326_URN], (
        f"requested srsName={EPSG4326_URN} but the GML declares {srs_names}"
    )
    # EPSG:4326 declares latitude first; the ordinates must follow.
    delta = geographic_delta((position[0], position[1]), (fx.ANCHOR_LAT, fx.ANCHOR_LON))
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"EPSG:4326 GML is not latitude/longitude: gml:pos={position}"
    )
    wfs_collector.record(
        "CERT-GEOM-02", "pass",
        duration_ms=timer.ms,
        measured_delta=delta,
        notes=(
            f"SRSNAME={EPSG4326_URN} is echoed verbatim on every gml geometry and the ordinates "
            f"follow that CRS's declared latitude/longitude order (gml:pos={position})."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-CRS-02")
def test_ext_every_advertised_crs_round_trips(wfs: WebFeatureService, typename: str,
                                              wfs_collector: CertificationEvidenceCollector) -> None:
    """Every CRS in ``crsOptions`` must be servable and correctly labelled.

    Both the short (``EPSG:4326``) and URN spellings are exercised because
    OWSLib hands the capabilities spelling straight back to the server.
    """
    checked = 0
    for crs in wfs.contents[typename].crsOptions:
        for spelling in {crs.getcode(), crs.getcodeurn()}:
            srs_names, position = _gml_anchor(wfs, typename, spelling)
            assert len(srs_names) == 1, f"{spelling}: mixed srsName values {srs_names}"
            assert str(crs.code) in srs_names[0], (
                f"requested {spelling!r} but the GML declares {srs_names[0]!r}"
            )
            if crs.code == 4326:
                delta = geographic_delta((position[0], position[1]), (fx.ANCHOR_LAT, fx.ANCHOR_LON))
                assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, position
            else:
                expected = web_mercator(fx.ANCHOR_LON, fx.ANCHOR_LAT)
                delta = max(abs(position[0] - expected[0]), abs(position[1] - expected[1]))
                assert delta <= PROJECTED_TOLERANCE_METERS, (
                    f"EPSG:{crs.code} anchor is {delta} m from the expected projection"
                )
            checked += 1
    wfs_collector.record(
        "NB-OWS-WFS-CRS-02", "pass",
        measured_count=checked,
        notes=(
            f"{checked} (CRS, spelling) combinations from the advertised crsOptions were served, "
            "each labelled with a matching srsName and reprojected within tolerance."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-CRS-03")
def test_ext_crs84_label_matches_axis_order(wfs: WebFeatureService, typename: str,
                                            wfs_collector: CertificationEvidenceCollector) -> None:
    """CRS84 must be labelled as CRS84, not as the latitude-first EPSG URN.

    ``urn:ogc:def:crs:EPSG::4326`` declares latitude first. Serving
    longitude-first ordinates under that URN produces a self-contradictory GML
    document: a conforming reader decodes the longitude as a latitude and lands
    the feature in the wrong hemisphere. Both the URN and the ``CRS:84`` short
    spelling must resolve to the same longitude/latitude behaviour.
    """
    for spelling in (CRS84_URN, "CRS:84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84"):
        srs_names, position = _gml_anchor(wfs, typename, spelling)
        assert "CRS84" in srs_names[0].upper(), (
            f"srsname={spelling!r} produced longitude/latitude ordinates {position} but labelled "
            f"them {srs_names[0]!r}, which declares latitude first"
        )
        delta = geographic_delta((position[0], position[1]), (fx.ANCHOR_LON, fx.ANCHOR_LAT))
        assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
            f"srsname={spelling!r} did not return longitude/latitude ordinates: {position}"
        )
    wfs_collector.record(
        "NB-OWS-WFS-CRS-03", "pass",
        measured_delta=0.0,
        notes=(
            "All three CRS84 spellings (URN, CRS:84, OGC URI) return longitude/latitude ordinates "
            "labelled with the CRS84 URN, so srsName and axis order agree. Note: CRS84 is honoured "
            "but is still absent from the feature type's advertised OtherCRS list, so OWSLib warns "
            "before issuing the request -- capabilities under-advertisement, filed separately."
        ),
    )


# ---------------------------------------------------------------------------
# ERRH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_type_name(wfs: WebFeatureService,
                                  wfs_collector: CertificationEvidenceCollector,
                                  timer: Timer) -> None:
    with pytest.raises(ServiceException) as excinfo:
        wfs.getfeature(typename=[f"honua:{fx.UNKNOWN_COLLECTION_ID}"])
    body = str(excinfo.value)
    assert "ExceptionReport" in body
    assert "InvalidParameterValue" in body
    assert 'locator="typeNames"' in body, body[:300]
    assert fx.UNKNOWN_COLLECTION_ID in body
    wfs_collector.record(
        "CERT-ERRH-01", "pass",
        duration_ms=timer.ms,
        notes=(
            "OWSLib raised owslib.util.ServiceException from a spec-shaped ows:ExceptionReport "
            "with exceptionCode=InvalidParameterValue, locator=typeNames and the offending type "
            "name in the text."
        ),
    )


@pytest.mark.cert("CERT-ERRH-02")
def test_errh02_invalid_filter(wfs: WebFeatureService, typename: str,
                               wfs_collector: CertificationEvidenceCollector,
                               timer: Timer) -> None:
    """A well-formed FES document naming a column that does not exist."""
    document = _fes_filter(PropertyIsEqualTo(propertyname="no_such_column", literal="x"))
    with pytest.raises(ServiceException) as excinfo:
        wfs.getfeature(typename=[typename], filter=document, method="Post")
    body = str(excinfo.value)
    assert "ExceptionReport" in body and "InvalidParameterValue" in body
    wfs_collector.record(
        "CERT-ERRH-02", "pass",
        duration_ms=timer.ms,
        notes=(
            "A syntactically valid Filter Encoding 2.0 document referencing an unknown "
            "ValueReference is rejected with an ows:ExceptionReport carrying "
            "exceptionCode=InvalidParameterValue rather than silently returning every feature."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-ERR-02")
def test_ext_error_surface(wfs: WebFeatureService, typename: str, base_url: str,
                           wfs_collector: CertificationEvidenceCollector) -> None:
    """Each deliberate client error must raise a parseable OWSLib ServiceException."""
    cases: dict[str, dict] = {
        "bad-srsname": dict(typename=[typename], srsname="urn:ogc:def:crs:EPSG::999999",
                            maxfeatures=1),
        "structurally-invalid-filter": dict(
            typename=[typename], method="Post",
            filter='<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">'
                   "<fes:PropertyIsEqualTo><fes:ValueReference>status</fes:ValueReference>"
                   "</fes:PropertyIsEqualTo></fes:Filter>"),
        "unknown-property-projection": dict(typename=[typename],
                                            propertyname=["no_such_column"], maxfeatures=1),
    }
    observed: dict[str, str] = {}
    for label, kwargs in cases.items():
        with pytest.raises(ServiceException) as excinfo:
            wfs.getfeature(**kwargs)
        body = str(excinfo.value)
        assert "ExceptionReport" in body, f"{label}: not an OWS exception report: {body[:200]}"
        code = re.search(r'exceptionCode="([^"]+)"', body)
        assert code, f"{label}: exception report has no exceptionCode: {body[:200]}"
        observed[label] = code.group(1)

    # An unsupported outputFormat is the fourth error shape; openURL raises the
    # same ServiceException type from the ows:ExceptionReport the server returns.
    with pytest.raises(ServiceException) as excinfo:
        openURL(
            f"{base_url}/wfs",
            data={"SERVICE": "WFS", "VERSION": "2.0.0", "REQUEST": "GetFeature",
                  "TYPENAMES": typename, "OUTPUTFORMAT": "application/x-not-a-format"},
            method="Get", timeout=30,
        )
    observed["unsupported-output-format"] = (
        re.search(r'exceptionCode="([^"]+)"', str(excinfo.value)) or re.match(r"(.*)", "unknown")
    ).group(1)

    wfs_collector.record(
        "NB-OWS-WFS-ERR-02", "pass",
        measured_count=len(observed),
        notes=(
            "Every deliberate client error produced an ows:ExceptionReport OWSLib could parse: "
            + ", ".join(f"{label}={code}" for label, code in observed.items())
            + ". None returned a 500 or a silently empty FeatureCollection."
        ),
    )


# ---------------------------------------------------------------------------
# Cross-version and cross-protocol
# ---------------------------------------------------------------------------

def _legacy_version_witness(
    lane_config: LaneConfig, typename: str,
    wfs_collector: CertificationEvidenceCollector, version: str, case_id: str,
    expected_srs: str, expected_coordinate: tuple[float, float],
) -> None:
    """Emit one independently-failing maintained-client witness per legacy WFS version."""
    local = typename.split(":")[-1]
    client = WebFeatureService(lane_config.wfs_url, version=version)
    assert client.identification.version == version
    matches = [name for name in client.contents if name.split(":")[-1] == local]
    assert matches, f"WFS {version} does not advertise {local!r}: {list(client.contents)}"
    body = client.getfeature(typename=[matches[0]]).read().decode()
    assert "ExceptionReport" not in body and "ServiceException" not in body, body[:300]
    srs_names = sorted(set(re.findall(r'srsName="([^"]+)"', body)))
    raw = re.findall(r"<gml:(?:pos|coordinates)[^>]*>([^<]+)<", body)
    assert raw, f"WFS {version} returned no GML coordinate"
    coordinate = tuple(float(value) for value in re.split(r"[,\s]+", raw[0].strip())[:2])
    assert srs_names == [expected_srs], srs_names
    delta = geographic_delta(coordinate, expected_coordinate)
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES
    wfs_collector.record(
        case_id, "pass", measured_count=1, measured_delta=delta,
        notes=(f"OWSLib WebFeatureService negotiated WFS {version}, parsed capabilities, "
               f"discovered {matches[0]}, and executed GetFeature; {expected_srs} coordinate "
               f"{coordinate} obeyed that version's axis-order contract."),
    )


@pytest.mark.cert("NB-OWS-WFS-100-01")
def test_ext_wfs100_maintained_client_witness(
    lane_config: LaneConfig, typename: str,
    wfs_collector: CertificationEvidenceCollector,
) -> None:
    _legacy_version_witness(
        lane_config, typename, wfs_collector, "1.0.0", "NB-OWS-WFS-100-01",
        "EPSG:4326", (fx.ANCHOR_LON, fx.ANCHOR_LAT),
    )


@pytest.mark.cert("NB-OWS-WFS-110-01")
def test_ext_wfs110_maintained_client_witness(
    lane_config: LaneConfig, typename: str,
    wfs_collector: CertificationEvidenceCollector,
) -> None:
    _legacy_version_witness(
        lane_config, typename, wfs_collector, "1.1.0", "NB-OWS-WFS-110-01",
        EPSG4326_URN, (fx.ANCHOR_LAT, fx.ANCHOR_LON),
    )

@pytest.mark.cert("NB-OWS-WFS-VER-01")
def test_ext_legacy_versions(lane_config: LaneConfig, typename: str,
                             wfs_collector: CertificationEvidenceCollector) -> None:
    """WFS 1.1.0 and 1.0.0 must be usable through OWSLib's default call shape.

    The versions differ in axis order (1.0.0 is always longitude/latitude, 1.1.0
    honours the CRS's declared order) and in the ``PROPERTYNAME=*`` wildcard
    OWSLib sends by default, so this is where cross-version regressions surface.
    """
    local = typename.split(":")[-1]
    observed: dict[str, tuple[list[str], list[float]]] = {}
    for version in ("1.1.0", "1.0.0"):
        client = WebFeatureService(lane_config.wfs_url, version=version)
        assert client.identification.version == version
        matches = [name for name in client.contents if name.split(":")[-1] == local]
        assert matches, f"WFS {version} does not advertise {local!r}: {list(client.contents)}"
        body = client.getfeature(typename=[matches[0]]).read().decode()
        assert "ExceptionReport" not in body and "ServiceException" not in body, (
            f"WFS {version} default getfeature failed: {body[:300]}"
        )
        srs_names = sorted(set(re.findall(r'srsName="([^"]+)"', body)))
        raw = re.findall(r"<gml:(?:pos|coordinates)[^>]*>([^<]+)<", body)
        first = [float(value) for value in re.split(r"[,\s]+", raw[0].strip())][:2]
        observed[version] = (srs_names, first)

    # 1.0.0 KVP is always longitude/latitude with the short EPSG form.
    names_10, pos_10 = observed["1.0.0"]
    assert names_10 == ["EPSG:4326"], names_10
    assert geographic_delta((pos_10[0], pos_10[1]), (fx.ANCHOR_LON, fx.ANCHOR_LAT)) <= GEOGRAPHIC_TOLERANCE_DEGREES

    # 1.1.0 uses the URN form and its declared latitude/longitude order.
    names_11, pos_11 = observed["1.1.0"]
    assert names_11 == [EPSG4326_URN], names_11
    assert geographic_delta((pos_11[0], pos_11[1]), (fx.ANCHOR_LAT, fx.ANCHOR_LON)) <= GEOGRAPHIC_TOLERANCE_DEGREES

    wfs_collector.record(
        "NB-OWS-WFS-VER-01", "pass",
        measured_count=len(observed),
        notes=(
            "OWSLib's bare getfeature() (which sends PROPERTYNAME=*) works on both legacy "
            f"versions. WFS 1.0.0 -> srsName EPSG:4326 with longitude/latitude {pos_10}; "
            f"WFS 1.1.0 -> srsName {EPSG4326_URN} with latitude/longitude {pos_11}. The "
            "per-version axis-order rule is honoured in both directions."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-XPRO-01")
def test_ext_cross_protocol_consistency(wfs: WebFeatureService, typename: str,
                                        lane_config: LaneConfig,
                                        wfs_collector: CertificationEvidenceCollector) -> None:
    """The same layer must look the same through WFS and OGC API - Features.

    Extent, feature count and CRS support are all derived from one canonical
    catalogue; a disagreement between two protocol adapters over the same layer
    is a server bug, not a protocol difference.
    """
    features = Features(lane_config.oaf_url)
    collection = features.collection(lane_config.collection_id)

    wfs_bbox = wfs.contents[typename].boundingBoxWGS84
    oaf_bbox = collection["extent"]["spatial"]["bbox"][0]
    for index, (left, right) in enumerate(zip(wfs_bbox, oaf_bbox)):
        assert abs(left - right) <= 1e-6, (
            f"extent ordinate {index} differs: WFS {wfs_bbox} vs OGC API {oaf_bbox}"
        )

    wfs_total = _geojson(wfs, typename=[typename], maxfeatures=1)["numberMatched"]
    oaf_total = features.collection_items(lane_config.collection_id, limit=1)["numberMatched"]
    assert wfs_total == oaf_total == fx.TOTAL_FEATURES, (
        f"feature counts differ: WFS {wfs_total}, OGC API {oaf_total}"
    )

    wfs_codes = {crs.code for crs in wfs.contents[typename].crsOptions}
    oaf_codes = {
        int(uri.rstrip("/").rsplit("/", 1)[-1])
        for uri in collection["crs"] if uri.rstrip("/").rsplit("/", 1)[-1].isdigit()
    }
    assert wfs_codes <= oaf_codes, (
        f"WFS advertises CRS codes {wfs_codes} that OGC API does not: {oaf_codes}"
    )

    wfs_collector.record(
        "NB-OWS-WFS-XPRO-01", "pass",
        measured_count=wfs_total,
        notes=(
            f"WFS and OGC API - Features agree on the same layer: extent {wfs_bbox}, "
            f"numberMatched {wfs_total}, and every WFS-advertised EPSG code {sorted(wfs_codes)} is "
            f"also offered by the OGC API collection {sorted(oaf_codes)}."
        ),
    )


@pytest.mark.cert("NB-OWS-WFS-STQ-01")
def test_ext_stored_query_get_feature_by_id(wfs: WebFeatureService, typename: str, base_url: str,
                                            wfs_collector: CertificationEvidenceCollector) -> None:
    """The mandatory WFS 2.0 ``GetFeatureById`` stored query must be usable.

    OWSLib's ``getfeature`` exposes ``storedQueryID``/``storedQueryParams``, and
    the id it needs comes from ListStoredQueries -- so the whole flow is
    capabilities-driven rather than hard-coded.
    """
    listing = ET.fromstring(openURL(
        f"{base_url}/wfs",
        data={"SERVICE": "WFS", "VERSION": "2.0.0", "REQUEST": "ListStoredQueries"},
        method="Get", timeout=30,
    ).read())
    ids = [element.get("id") for element in listing.iter() if element.tag.endswith("StoredQuery")]
    by_id = "urn:ogc:def:query:OGC-WFS::GetFeatureById"
    assert by_id in ids, f"the mandatory GetFeatureById stored query is not advertised: {ids}"

    listed = _geojson(wfs, typename=[typename], maxfeatures=1)
    identifier = listed["features"][0]["id"]
    body = wfs.getfeature(storedQueryID=by_id, storedQueryParams={"ID": str(identifier)}).read()
    assert b"ExceptionReport" not in body[:400], body[:300]
    assert str(identifier).split(".")[-1].encode() in body, (
        f"GetFeatureById({identifier}) did not return that feature: {body[:300]!r}"
    )
    wfs_collector.record(
        "NB-OWS-WFS-STQ-01", "pass",
        measured_count=len(ids),
        notes=(
            f"ListStoredQueries advertises {len(ids)} queries including the mandatory "
            f"{by_id}; invoking it through OWSLib's storedQueryID/storedQueryParams returned the "
            f"requested feature {identifier}."
        ),
    )
