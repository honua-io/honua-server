# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GeoPandas / pyogrio certification of the WFS 2.0.0 surface.

The lane drives GDAL's WFS driver through pyogrio for the common core, and
hands fully-formed ``GetFeature`` URLs to ``geopandas.read_file`` for the WFS
request parameters the driver never emits on its own (``COUNT``,
``STARTINDEX``, ``PROPERTYNAME``, ``SRSNAME``, ``BBOX``, ``FILTER``). Both are
real client reads; the second simply reaches parts of the protocol the driver
does not.
"""

from __future__ import annotations

import tempfile
import urllib.error
import urllib.parse
from pathlib import Path

import geopandas
import httpx
import pandas
import pyogrio
import pyogrio.errors
import pytest
from pyproj import CRS, Transformer

from shared import canonical_fixture as fixture
from shared.cert_envelope import (
    GEOGRAPHIC_TOLERANCE_DEGREES,
    PROJECTED_TOLERANCE_METERS,
    CertificationEvidenceCollector,
)

from .conftest import (
    CaseTimer,
    record_fail,
    record_pass,
    row_identities,
    wfs_capabilities_url,
)

pytestmark = pytest.mark.geopandas_client

EPSG_4326_URN = "urn:ogc:def:crs:EPSG::4326"
EPSG_3857_URN = "urn:ogc:def:crs:EPSG::3857"

#: A fes 2.0 equality predicate over the canonical filter field.
FES_STATUS_FILTER = (
    '<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">'
    "<fes:PropertyIsEqualTo>"
    f"<fes:ValueReference>{fixture.FILTER_FIELD}</fes:ValueReference>"
    f"<fes:Literal>{fixture.FILTER_VALUE}</fes:Literal>"
    "</fes:PropertyIsEqualTo>"
    "</fes:Filter>"
)


def read_get_feature(get_feature_url: str, **params: object) -> geopandas.GeoDataFrame:
    """Read a WFS ``GetFeature`` response through ``geopandas.read_file``."""
    query = urllib.parse.urlencode(params, quote_via=urllib.parse.quote)
    url = f"{get_feature_url}&{query}" if query else get_feature_url
    return geopandas.read_file(url)


def read_get_feature_page(
    get_feature_url: str, **params: object
) -> geopandas.GeoDataFrame:
    """Read a ``GetFeature`` page, tolerating a legitimately empty response.

    A conformant WFS 2.0 server answers an exhausted page with a
    ``wfs:FeatureCollection`` carrying ``numberReturned="0"`` and no members.
    GDAL's GML driver cannot synthesise a layer from such a document when the
    schema lives behind a remote ``xsi:schemaLocation``, so pyogrio raises
    ``IndexError`` from ``get_default_layer``. That is a client-side artefact
    of an empty result, not a server error, and it is normalised here to an
    empty frame so the paging assertions stay about the server.
    """
    try:
        return read_get_feature(get_feature_url, **params)
    except IndexError:
        return geopandas.GeoDataFrame({"name": []}, geometry=[], crs="EPSG:4326")


def _anchor_row(frame: geopandas.GeoDataFrame):
    matches = frame[frame["name"] == fixture.ANCHOR_NAME]
    assert not matches.empty, (
        f"anchor feature {fixture.ANCHOR_NAME!r} missing from the response"
    )
    return matches.iloc[0]


# ===========================================================================
# Common core
# ===========================================================================

@pytest.mark.cert("CERT-CONN-01")
def test_conn01_pyogrio_opens_wfs_dataset(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """pyogrio opens the WFS dataset and materializes the seeded rows."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)

    assert isinstance(frame, geopandas.GeoDataFrame)
    assert len(frame) == fixture.TOTAL_FEATURES

    record_pass(
        wfs_evidence,
        "CERT-CONN-01",
        timer,
        measured_count=len(frame),
        notes=(
            f"pyogrio.read_dataframe over the WFS driver returned {len(frame)} "
            f"rows for typename '{wfs_typename}'."
        ),
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport_scheme(
    base_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The lane records the transport posture of the certified endpoint."""
    timer = CaseTimer()
    scheme = urllib.parse.urlparse(base_url).scheme

    assert scheme in {"http", "https"}, f"unexpected transport scheme {scheme!r}"

    record_pass(
        wfs_evidence,
        "CERT-CONN-02",
        timer,
        notes=(
            f"Certified endpoint uses scheme '{scheme}'. The client-compat "
            "compose network is plain HTTP by design; TLS posture is exercised "
            "in the release tier against the HTTPS candidate build."
        ),
    )


@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_admin_probe_is_rejected(
    base_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """An anonymous control-plane request is rejected with a challenge."""
    timer = CaseTimer()
    response = httpx.get(f"{base_url}{fixture.ADMIN_PROBE_PATH}", timeout=30.0)

    assert response.status_code in {401, 403}, (
        f"anonymous {fixture.ADMIN_PROBE_PATH} returned "
        f"{response.status_code}; expected 401/403"
    )
    challenge = response.headers.get("WWW-Authenticate", "")
    if response.status_code == 401:
        assert fixture.ADMIN_API_KEY_HEADER.lower() in challenge.lower(), (
            "401 response did not advertise the API-key header in "
            f"WWW-Authenticate: {challenge!r}"
        )

    record_pass(
        wfs_evidence,
        "CERT-AUTH-01",
        timer,
        notes=(
            f"GET {fixture.ADMIN_PROBE_PATH} without credentials returned "
            f"{response.status_code} with WWW-Authenticate={challenge!r}. "
            "httpx is used here because the control plane has no GeoPandas "
            "client surface."
        ),
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_api_key_is_accepted(
    base_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The documented API-key scheme authenticates the control-plane probe."""
    timer = CaseTimer()
    url = f"{base_url}{fixture.ADMIN_PROBE_PATH}"
    attempts: list[tuple[str, int]] = []

    api_key = httpx.get(
        url,
        headers={fixture.ADMIN_API_KEY_HEADER: fixture.ADMIN_API_KEY},
        timeout=30.0,
    )
    attempts.append((fixture.ADMIN_API_KEY_HEADER, api_key.status_code))
    accepted = fixture.ADMIN_API_KEY_HEADER if api_key.is_success else None

    if accepted is None:
        basic = httpx.get(
            url,
            auth=(fixture.ADMIN_USERNAME, fixture.ADMIN_PASSWORD),
            timeout=30.0,
        )
        attempts.append(("Basic", basic.status_code))
        if basic.is_success:
            accepted = "Basic"

    assert accepted is not None, (
        "no admin authentication scheme was accepted; observed "
        + ", ".join(f"{scheme}->{status}" for scheme, status in attempts)
    )

    record_pass(
        wfs_evidence,
        "CERT-AUTH-02",
        timer,
        notes=(
            f"GET {fixture.ADMIN_PROBE_PATH} authenticated with the "
            f"{accepted} scheme (observed: "
            + ", ".join(f"{scheme}->{status}" for scheme, status in attempts)
            + "). httpx is used because the control plane has no GeoPandas "
            "client surface."
        ),
    )


@pytest.mark.cert("CERT-DISC-01")
def test_disc01_feature_types_are_listed(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``pyogrio.list_layers`` enumerates the advertised WFS feature types."""
    timer = CaseTimer()
    layers = pyogrio.list_layers(wfs_dsn)
    names = [str(entry[0]) for entry in layers]

    assert wfs_typename in names, f"{wfs_typename!r} not advertised; saw {names}"

    record_pass(
        wfs_evidence,
        "CERT-DISC-01",
        timer,
        measured_count=len(names),
        notes=(
            f"GetCapabilities advertised {len(names)} feature types: {names}."
        ),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_feature_type_metadata(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``pyogrio.read_info`` returns per-feature-type metadata."""
    timer = CaseTimer()
    info = pyogrio.read_info(wfs_dsn, layer=wfs_typename)

    assert info["driver"] == "WFS"
    assert info["layer_name"] == wfs_typename
    assert info["features"] == fixture.TOTAL_FEATURES
    assert CRS.from_user_input(info["crs"]).to_epsg() == fixture.STORAGE_CRS_EPSG
    assert (info.get("layer_metadata") or {}).get("TITLE")

    record_pass(
        wfs_evidence,
        "CERT-DISC-02",
        timer,
        measured_count=int(info["features"]),
        notes=(
            f"read_info reported driver={info['driver']}, crs={info['crs']}, "
            f"features={info['features']}, geometry_name="
            f"{info['geometry_name']!r}, title="
            f"{(info.get('layer_metadata') or {}).get('TITLE')!r}."
        ),
    )


@pytest.mark.cert("CERT-SCHM-01")
def test_schm01_attribute_schema(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Every canonical attribute field reaches the client over WFS."""
    timer = CaseTimer()
    info = pyogrio.read_info(wfs_dsn, layer=wfs_typename)
    fields = {str(name) for name in info["fields"]}
    missing = [name for name in fixture.ATTRIBUTE_FIELDS if name not in fields]

    assert not missing, (
        f"WFS schema is missing declared fields {missing}; client saw "
        f"{sorted(fields)}"
    )
    assert fixture.FEATURE_ID_FIELD in fields, (
        "WFS should surface the feature id field as an attribute"
    )

    record_pass(
        wfs_evidence,
        "CERT-SCHM-01",
        timer,
        measured_count=len(fields),
        notes=(
            f"WFS exposed {len(fields)} attributes ({sorted(fields)}), "
            "covering every canonical attribute field plus the "
            f"'{fixture.FEATURE_ID_FIELD}' identifier and gml_id."
        ),
    )


@pytest.mark.cert("CERT-SCHM-02")
def test_schm02_geometry_type_is_point(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The feature type reports a Point geometry to the client."""
    timer = CaseTimer()
    info = pyogrio.read_info(wfs_dsn, layer=wfs_typename)
    geometry_type = str(info["geometry_type"])

    assert "Point" in geometry_type, f"expected a Point layer, got {geometry_type!r}"

    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    observed = set(frame.geometry.dropna().geom_type.unique())
    assert observed <= {"Point"}, f"non-point geometries returned: {observed}"

    record_pass(
        wfs_evidence,
        "CERT-SCHM-02",
        timer,
        measured_count=len(frame.geometry.dropna()),
        notes=(
            f"read_info geometry_type={geometry_type!r} for geometry element "
            f"{info['geometry_name']!r}; materialized types={sorted(observed)}."
        ),
    )


@pytest.mark.cert("CERT-QFLT-01")
def test_qflt01_attribute_filter(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """An attribute filter selects exactly the active features."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        wfs_dsn,
        layer=wfs_typename,
        where=f"{fixture.FILTER_FIELD} = '{fixture.FILTER_VALUE}'",
    )

    assert len(frame) == fixture.ACTIVE_FEATURES
    assert set(frame[fixture.FILTER_FIELD].unique()) == {fixture.FILTER_VALUE}

    record_pass(
        wfs_evidence,
        "CERT-QFLT-01",
        timer,
        measured_count=len(frame),
        notes=(
            "pyogrio where=\"status = 'active'\" returned "
            f"{len(frame)} rows. A CPL_DEBUG trace shows GDAL's WFS driver "
            "fetching GetFeature without a FILTER parameter, so the predicate "
            "was evaluated client-side over the full response; server-side "
            "fes 2.0 predicate evaluation is certified by NB-GPD-WFS-FLT-01."
        ),
    )


@pytest.mark.cert("CERT-QFLT-02")
def test_qflt02_bbox_filter(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """A bbox filter selects the canonical subset."""
    timer = CaseTimer()
    # WFS 2.0 KVP BBOX in EPSG:4326 is lat/lon ordered.
    server_side = read_get_feature(
        wfs_getfeature_url,
        BBOX=(
            f"{fixture.SUBSET_BBOX[1]},{fixture.SUBSET_BBOX[0]},"
            f"{fixture.SUBSET_BBOX[3]},{fixture.SUBSET_BBOX[2]},"
            f"{EPSG_4326_URN}"
        ),
    )
    driver_side = pyogrio.read_dataframe(
        wfs_dsn, layer=wfs_typename, bbox=fixture.SUBSET_BBOX
    )
    driver_side_located = driver_side[driver_side.geometry.notna()]

    assert len(server_side) == fixture.SUBSET_BBOX_FEATURE_COUNT
    assert len(driver_side_located) == fixture.SUBSET_BBOX_FEATURE_COUNT
    assert set(server_side["name"]) == set(driver_side_located["name"])

    record_pass(
        wfs_evidence,
        "CERT-QFLT-02",
        timer,
        measured_count=len(server_side),
        notes=(
            f"Server-side KVP BBOX ({EPSG_4326_URN}, lat/lon ordered) returned "
            f"{len(server_side)} features, matching the canonical subset. The "
            "same envelope passed to pyogrio's bbox= returned "
            f"{len(driver_side)} rows because GDAL's WFS driver evaluates the "
            "spatial filter client-side and retains the null-geometry feature; "
            f"the {len(driver_side_located)} located rows agree with the "
            "server-side result."
        ),
    )


@pytest.mark.cert("CERT-PAGE-01")
def test_page01_first_page(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """A bounded read returns exactly the requested page size."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        wfs_dsn, layer=wfs_typename, max_features=fixture.PAGE_SIZE
    )

    assert len(frame) == fixture.PAGE_SIZE

    record_pass(
        wfs_evidence,
        "CERT-PAGE-01",
        timer,
        measured_count=len(frame),
        notes=f"max_features={fixture.PAGE_SIZE} returned {len(frame)} rows.",
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_page02_second_page_is_disjoint(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Skipping the first page yields a disjoint set of features."""
    timer = CaseTimer()
    first = pyogrio.read_dataframe(
        wfs_dsn, layer=wfs_typename, max_features=fixture.PAGE_SIZE
    )
    second = pyogrio.read_dataframe(
        wfs_dsn,
        layer=wfs_typename,
        skip_features=fixture.PAGE_SIZE,
        max_features=fixture.PAGE_SIZE,
    )

    first_ids = row_identities(first)
    second_ids = row_identities(second)
    assert len(second) == fixture.PAGE_SIZE
    assert not (first_ids & second_ids), (
        f"pages overlap: {sorted(first_ids & second_ids)}"
    )

    record_pass(
        wfs_evidence,
        "CERT-PAGE-02",
        timer,
        measured_count=len(second),
        notes=(
            f"page 1 ids={sorted(first_ids)}, page 2 ids={sorted(second_ids)}; "
            "the two pages are disjoint."
        ),
    )


@pytest.mark.cert("CERT-GEOM-01")
def test_geom01_anchor_coordinate_fidelity(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The anchor feature's coordinates survive the GML round trip."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    point = _anchor_row(frame).geometry

    delta = max(
        abs(point.x - fixture.ANCHOR_LON), abs(point.y - fixture.ANCHOR_LAT)
    )
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"anchor drifted by {delta} deg (limit {GEOGRAPHIC_TOLERANCE_DEGREES})"
    )

    record_pass(
        wfs_evidence,
        "CERT-GEOM-01",
        timer,
        measured_delta=delta,
        notes=(
            f"anchor '{fixture.ANCHOR_NAME}' materialized at "
            f"({point.x}, {point.y}); expected "
            f"({fixture.ANCHOR_LON}, {fixture.ANCHOR_LAT}); max abs deviation "
            f"{delta} deg against a {GEOGRAPHIC_TOLERANCE_DEGREES} deg limit. "
            "GDAL applied the EPSG:4326 lat/lon-to-lon/lat axis swap the WFS "
            "response declares."
        ),
    )


@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_crs_is_wgs84(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The client receives an EPSG:4326 GeoDataFrame."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)

    assert frame.crs is not None
    assert frame.crs.to_epsg() == fixture.STORAGE_CRS_EPSG

    record_pass(
        wfs_evidence,
        "CERT-GEOM-02",
        timer,
        measured_count=fixture.STORAGE_CRS_EPSG,
        notes=f"gdf.crs={frame.crs.to_string()} (EPSG:{frame.crs.to_epsg()}).",
    )


@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_typename(
    wfs_dsn: str,
    base_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """An unknown type name raises a structured client error."""
    timer = CaseTimer()
    with pytest.raises(pyogrio.errors.DataLayerError) as raised:
        pyogrio.read_dataframe(wfs_dsn, layer=fixture.UNKNOWN_COLLECTION_ID)

    message = str(raised.value)
    assert fixture.UNKNOWN_COLLECTION_ID in message, message

    transport = httpx.get(
        f"{base_url}/wfs",
        params={
            "SERVICE": "WFS",
            "VERSION": "2.0.0",
            "REQUEST": "GetFeature",
            "TYPENAMES": fixture.UNKNOWN_COLLECTION_ID,
        },
        timeout=30.0,
    )

    assert transport.status_code == 400, (
        f"unknown typename returned {transport.status_code}; expected 400"
    )
    assert "ExceptionReport" in transport.text, transport.text[:200]

    record_pass(
        wfs_evidence,
        "CERT-ERRH-01",
        timer,
        notes=(
            f"pyogrio raised DataLayerError({message!r}); the underlying "
            f"transport shape was verified with httpx: {transport.status_code} "
            "carrying an ows:ExceptionReport."
        ),
    )


@pytest.mark.cert("CERT-ERRH-02")
def test_errh02_malformed_filter(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """A malformed attribute filter is rejected with a structured error."""
    timer = CaseTimer()
    with pytest.raises(ValueError) as raised:
        pyogrio.read_dataframe(
            wfs_dsn, layer=wfs_typename, where=fixture.MALFORMED_CQL2_FILTER
        )

    message = str(raised.value)
    assert "Invalid SQL query" in message, message
    assert fixture.MALFORMED_CQL2_FILTER in message, message

    record_pass(
        wfs_evidence,
        "CERT-ERRH-02",
        timer,
        notes=f"pyogrio rejected where={fixture.MALFORMED_CQL2_FILTER!r}: {message!r}.",
    )


# ===========================================================================
# Lane extensions - broadened server surface
# ===========================================================================

@pytest.mark.cert("NB-GPD-WFS-CRS-01")
def test_nb_wfs_crs01_axis_order(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The GML response declares EPSG:4326 and is decoded to lon/lat correctly."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    minx, miny, maxx, maxy = frame.total_bounds

    assert minx == pytest.approx(fixture.FIXTURE_BBOX[0], abs=1e-9)
    assert miny == pytest.approx(fixture.FIXTURE_BBOX[1], abs=1e-9)
    assert maxx == pytest.approx(fixture.FIXTURE_BBOX[2], abs=1e-9)
    assert maxy == pytest.approx(fixture.FIXTURE_BBOX[3], abs=1e-9)

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-CRS-01",
        timer,
        measured_delta=float(abs(minx - fixture.FIXTURE_BBOX[0])),
        notes=(
            f"total_bounds={tuple(frame.total_bounds)} after GDAL applied the "
            f"axis swap implied by srsName={EPSG_4326_URN}; the server's GML "
            "coordinate order is therefore consistent with the CRS it "
            "declares."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-CRS-02")
def test_nb_wfs_crs02_srsname_reprojection(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``SRSNAME`` reprojection matches the client's own pyproj transform."""
    timer = CaseTimer()
    frame = read_get_feature(wfs_getfeature_url, SRSNAME=EPSG_3857_URN)

    assert frame.crs is not None
    assert frame.crs.to_epsg() == fixture.PROJECTED_CRS_EPSG
    transformer = Transformer.from_crs(
        f"EPSG:{fixture.STORAGE_CRS_EPSG}",
        f"EPSG:{fixture.PROJECTED_CRS_EPSG}",
        always_xy=True,
    )
    expected_x, expected_y = transformer.transform(
        fixture.ANCHOR_LON, fixture.ANCHOR_LAT
    )
    point = _anchor_row(frame).geometry
    delta = max(abs(point.x - expected_x), abs(point.y - expected_y))

    assert delta <= PROJECTED_TOLERANCE_METERS

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-CRS-02",
        timer,
        measured_delta=delta,
        notes=(
            f"SRSNAME={EPSG_3857_URN} returned EPSG:{frame.crs.to_epsg()}; the "
            f"anchor landed {delta} m from the pyproj reference (limit "
            f"{PROJECTED_TOLERANCE_METERS} m)."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-SCH-01")
def test_nb_wfs_sch01_namespaced_field_is_readable(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The namespaced ``eo:cloud_cover`` field is readable over WFS.

    This is the control for the OGC API Features defect recorded as
    ``NB-GPD-SCH-01``: the same declared field is fully materialized here, so
    the data is present and only the OAPIF/FeatureServer read projection drops
    it.
    """
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    candidates = [
        column for column in frame.columns if "cloud_cover" in column.lower()
    ]

    assert candidates, (
        "the declared eo:cloud_cover field is absent from the WFS schema; "
        f"columns={sorted(frame.columns)}"
    )
    column = candidates[0]
    populated = int(frame[column].notna().sum())
    assert populated >= fixture.TOTAL_FEATURES - 1, (
        f"{column} carried only {populated} values"
    )

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-SCH-01",
        timer,
        measured_count=populated,
        notes=(
            f"WFS exposed the namespaced field as {column!r} (GML escapes ':' "
            f"as _x003A_) with {populated} populated values, e.g. "
            f"{frame[column].dropna().head(3).tolist()}. The identical field "
            "is missing from every OGC API Features / FeatureServer payload - "
            "see NB-GPD-SCH-01 on the ogc-features envelope."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-TYP-01")
def test_nb_wfs_typ01_scalar_typing(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Numeric and boolean XSD types materialize as numeric/bool dtypes."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)

    assert str(frame[fixture.FEATURE_ID_FIELD].dtype).startswith("int")
    assert str(frame["count"].dtype).startswith("int")
    assert str(frame["ratio"].dtype).startswith("float")
    assert str(frame["active"].dtype) == "bool"
    assert sorted(frame[fixture.FEATURE_ID_FIELD].tolist()) == list(
        range(1, fixture.TOTAL_FEATURES + 1)
    )
    assert int(frame["active"].sum()) == fixture.ACTIVE_FEATURES

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-TYP-01",
        timer,
        measured_count=len(frame),
        notes=(
            f"objectid dtype={frame[fixture.FEATURE_ID_FIELD].dtype}, count="
            f"{frame['count'].dtype}, ratio={frame['ratio'].dtype}, active="
            f"{frame['active'].dtype}; xsd:int/xsd:double/xsd:boolean from "
            "DescribeFeatureType survived into pandas dtypes."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-TYP-02")
def test_nb_wfs_typ02_temporal_values(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Temporal fields carry parseable, correct values."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    anchor = _anchor_row(frame)

    created = pandas.to_datetime(frame["created_at"], utc=True, format="mixed")
    dates = pandas.to_datetime(frame["event_date"], format="mixed")

    assert str(created.iloc[0])[:19] == "2024-01-01 12:00:00"
    assert str(dates.iloc[0])[:10] == "2024-02-01"
    assert created.is_monotonic_increasing
    assert str(anchor["event_time"]).startswith("12:34:56")

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-TYP-02",
        timer,
        measured_count=len(frame),
        notes=(
            f"created_at={anchor['created_at']!r}, event_date="
            f"{anchor['event_date']!r}, event_time={anchor['event_time']!r} - "
            "all parse to the seeded instants. GDAL delivers them as strings "
            "rather than OFTDateTime because it falls back to data-driven GML "
            "typing when the DescribeFeatureType schema contains an "
            "xsd:anyType element (the tags/numbers JSON columns), which is a "
            "GDAL schema-parsing behaviour, not a value defect."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-NUL-01")
def test_nb_wfs_nul01_null_geometry_row_survives(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The geometry-less feature is delivered with a nil geometry."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)
    missing = frame.geometry.isna() | frame.geometry.is_empty

    assert len(frame) == fixture.TOTAL_FEATURES
    assert int(missing.sum()) == fixture.TOTAL_FEATURES - fixture.FEATURES_WITH_GEOMETRY
    assert frame.loc[missing, "name"].tolist() == ["lambda"]

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-NUL-01",
        timer,
        measured_count=int(missing.sum()),
        notes=(
            f"{len(frame)} features returned with {int(missing.sum())} nil "
            "geometry; the server emits the nillable geometry property rather "
            "than dropping the feature or writing an empty gml:Point."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-PAG-01")
def test_nb_wfs_pag01_count_startindex_pagination(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``COUNT``/``STARTINDEX`` paging is complete and non-overlapping."""
    timer = CaseTimer()
    seen: set[str] = set()
    pages = 0
    start = 0
    while start < fixture.TOTAL_FEATURES + fixture.PAGE_SIZE:
        page = read_get_feature_page(
            wfs_getfeature_url, COUNT=fixture.PAGE_SIZE, STARTINDEX=start
        )
        if len(page) == 0:
            break
        pages += 1
        names = set(page["name"].tolist())
        assert not (seen & names), (
            f"STARTINDEX={start} repeated {sorted(seen & names)}"
        )
        seen |= names
        start += fixture.PAGE_SIZE

    assert len(seen) == fixture.TOTAL_FEATURES, sorted(seen)

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-PAG-01",
        timer,
        measured_count=len(seen),
        notes=(
            f"{pages} pages of COUNT={fixture.PAGE_SIZE} produced "
            f"{len(seen)} distinct features with no repeats and no gaps."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-PAG-02")
def test_nb_wfs_pag02_paging_edges(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """An oversized ``COUNT`` returns everything; ``STARTINDEX`` past the end empties."""
    timer = CaseTimer()
    oversized = read_get_feature(wfs_getfeature_url, COUNT=1_000_000)
    past_end = read_get_feature_page(
        wfs_getfeature_url, COUNT=fixture.PAGE_SIZE, STARTINDEX=10_000
    )

    assert len(oversized) == fixture.TOTAL_FEATURES
    assert len(past_end) == 0

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-PAG-02",
        timer,
        measured_count=len(oversized),
        notes=(
            f"COUNT=1000000 returned {len(oversized)} features (clamped, not "
            "rejected); STARTINDEX=10000 returned an empty FeatureCollection "
            "rather than a service exception."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-HIT-01")
def test_nb_wfs_hit01_result_type_hits(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``RESULTTYPE=hits`` reports the true match count to the client."""
    timer = CaseTimer()
    info = pyogrio.read_info(wfs_dsn, layer=wfs_typename)
    hits = int(info["features"])

    assert hits == fixture.TOTAL_FEATURES, (
        f"hits reported {hits}; expected {fixture.TOTAL_FEATURES}"
    )
    assert info["capabilities"]["fast_feature_count"] is True

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-HIT-01",
        timer,
        measured_count=hits,
        notes=(
            "GDAL satisfies GetFeatureCount with GetFeature&RESULTTYPE=hits "
            f"(fast_feature_count=True) and the server reported numberMatched="
            f"{hits}, matching the seeded row count without transferring "
            "features."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-PRP-01")
def test_nb_wfs_prp01_propertyname_subsetting(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``PROPERTYNAME`` restricts the returned properties."""
    timer = CaseTimer()
    frame = read_get_feature(
        wfs_getfeature_url, COUNT=fixture.PAGE_SIZE, PROPERTYNAME="name,status"
    )
    attributes = {
        column
        for column in frame.columns
        if column not in {"geometry", "gml_id", "fid"}
    }

    assert "name" in attributes and "status" in attributes
    assert not (attributes - {"name", "status"}), (
        f"PROPERTYNAME leaked extra properties: {sorted(attributes)}"
    )
    assert len(frame) == fixture.PAGE_SIZE

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-PRP-01",
        timer,
        measured_count=len(attributes),
        notes=(
            "PROPERTYNAME=name,status produced exactly "
            f"{sorted(attributes)} on {len(frame)} features; no unrequested "
            "property was serialized."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-BBX-01")
def test_nb_wfs_bbx01_bbox_axis_order(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """KVP ``BBOX`` honours the axis order the declared CRS implies."""
    timer = CaseTimer()
    lat_lon = read_get_feature(
        wfs_getfeature_url,
        BBOX=(
            f"{fixture.SUBSET_BBOX[1]},{fixture.SUBSET_BBOX[0]},"
            f"{fixture.SUBSET_BBOX[3]},{fixture.SUBSET_BBOX[2]},"
            f"{EPSG_4326_URN}"
        ),
    )

    swapped_count: int | str
    try:
        swapped = read_get_feature_page(
            wfs_getfeature_url,
            BBOX=(
                f"{fixture.SUBSET_BBOX[0]},{fixture.SUBSET_BBOX[1]},"
                f"{fixture.SUBSET_BBOX[2]},{fixture.SUBSET_BBOX[3]},"
                f"{EPSG_4326_URN}"
            ),
        )
        swapped_count = len(swapped)
    except urllib.error.HTTPError as error:
        swapped_count = f"HTTP {error.code}"

    assert len(lat_lon) == fixture.SUBSET_BBOX_FEATURE_COUNT
    assert swapped_count != fixture.SUBSET_BBOX_FEATURE_COUNT, (
        "a lon/lat-ordered BBOX matched the same features as the correct "
        "lat/lon ordering, so the server ignores the declared axis order"
    )

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-BBX-01",
        timer,
        measured_count=len(lat_lon),
        notes=(
            f"BBOX in {EPSG_4326_URN} lat/lon order matched "
            f"{len(lat_lon)} features; the same numbers supplied in lon/lat "
            f"order yielded {swapped_count}, proving the server applies the "
            "CRS-declared axis order instead of guessing."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-FLT-01")
def test_nb_wfs_flt01_fes_filter(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """A fes 2.0 ``PropertyIsEqualTo`` filter is evaluated by the server."""
    timer = CaseTimer()
    frame = read_get_feature(wfs_getfeature_url, FILTER=FES_STATUS_FILTER)

    assert len(frame) == fixture.ACTIVE_FEATURES, frame["status"].tolist()
    assert set(frame["status"].unique()) == {fixture.FILTER_VALUE}

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-FLT-01",
        timer,
        measured_count=len(frame),
        notes=(
            "fes:PropertyIsEqualTo(status, 'active') returned "
            f"{len(frame)} features, all with status='{fixture.FILTER_VALUE}', "
            "so the server evaluates OGC Filter Encoding 2.0 predicates."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-ERR-01")
def test_nb_wfs_err01_exception_report(
    wfs_getfeature_url: str,
    base_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Bad WFS requests produce a 400 with an OWS ExceptionReport."""
    timer = CaseTimer()
    observed: dict[str, str] = {}

    with pytest.raises(urllib.error.HTTPError) as unknown_type:
        geopandas.read_file(
            f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"
            f"&TYPENAMES={fixture.UNKNOWN_COLLECTION_ID}"
        )
    body = unknown_type.value.read().decode("utf-8", "replace")
    observed["unknown-typename"] = str(unknown_type.value.code)

    with pytest.raises(urllib.error.HTTPError) as bad_srs:
        read_get_feature(wfs_getfeature_url, COUNT=1, SRSNAME="urn:not:a:crs")
    observed["malformed-srsname"] = str(bad_srs.value.code)

    assert observed["unknown-typename"] == "400", observed
    assert "ExceptionReport" in body, body[:300]
    assert observed["malformed-srsname"] == "400", observed

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-ERR-01",
        timer,
        measured_count=len(observed),
        notes=(
            f"Client-observed statuses: {observed}. The unknown-typename "
            "response body was a well-formed ows:ExceptionReport, so a "
            "GeoPandas caller receives a diagnosable failure instead of an "
            "empty document."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-IDN-01")
def test_nb_wfs_idn01_stable_gml_identity(
    wfs_getfeature_url: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """``gml:id`` values are unique, stable and derived from the feature id."""
    timer = CaseTimer()
    first = read_get_feature(wfs_getfeature_url, COUNT=fixture.TOTAL_FEATURES)
    again = read_get_feature(wfs_getfeature_url, COUNT=fixture.TOTAL_FEATURES)

    assert "gml_id" in first.columns, sorted(first.columns)
    identifiers = first["gml_id"].tolist()
    assert len(set(identifiers)) == len(identifiers)
    assert identifiers == again["gml_id"].tolist()
    for gml_id, object_id in zip(identifiers, first[fixture.FEATURE_ID_FIELD]):
        assert str(gml_id).endswith(f".{object_id}"), (gml_id, object_id)

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-IDN-01",
        timer,
        measured_count=len(set(identifiers)),
        notes=(
            f"{len(set(identifiers))} unique gml:id values "
            f"(e.g. {identifiers[:3]}), identical across two requests and each "
            "suffixed with the feature's objectid, so a client can key on "
            "them across pages."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-IO-01")
def test_nb_wfs_io01_gpkg_round_trip(
    wfs_dsn: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """The WFS response survives a GeoPackage write/read round trip."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(wfs_dsn, layer=wfs_typename)

    with tempfile.TemporaryDirectory() as workdir:
        path = Path(workdir) / "wfs-round-trip.gpkg"
        pyogrio.write_dataframe(frame, path, driver="GPKG")
        reloaded = pyogrio.read_dataframe(path)

    assert len(reloaded) == len(frame)
    assert reloaded.crs is not None
    assert reloaded.crs.to_epsg() == fixture.STORAGE_CRS_EPSG
    assert (
        reloaded[fixture.FEATURE_ID_FIELD].tolist()
        == frame[fixture.FEATURE_ID_FIELD].tolist()
    )
    worst = 0.0
    for left, right in zip(frame.geometry, reloaded.geometry):
        if left is None or right is None:
            assert left is None and right is None
            continue
        worst = max(worst, abs(left.x - right.x), abs(left.y - right.y))
    assert worst <= GEOGRAPHIC_TOLERANCE_DEGREES

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-IO-01",
        timer,
        measured_count=len(reloaded),
        measured_delta=worst,
        notes=(
            f"GeoPackage round trip preserved {len(reloaded)} features, "
            f"EPSG:{reloaded.crs.to_epsg()}, the objectid ordering and the "
            f"null geometry, with a worst deviation of {worst} deg."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-CAP-01")
def test_nb_wfs_cap01_capabilities_advertise_certified_type(
    base_url: str,
    wfs_typename: str,
    wfs_dsn: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Everything GetCapabilities advertises is actually openable."""
    timer = CaseTimer()
    layers = [str(entry[0]) for entry in pyogrio.list_layers(wfs_dsn)]
    openable: list[str] = []
    broken: dict[str, str] = {}

    for name in layers:
        try:
            info = pyogrio.read_info(wfs_dsn, layer=name)
        except Exception as error:  # noqa: BLE001 - the point is to classify it
            broken[name] = f"{type(error).__name__}: {error}"
            continue
        if info.get("geometry_type"):
            openable.append(name)
        else:
            broken[name] = "no geometry type advertised"

    if broken:
        record_fail(
            wfs_evidence,
            "NB-GPD-WFS-CAP-01",
            timer,
            measured_count=len(openable),
            notes=(
                f"GetCapabilities at {wfs_capabilities_url(base_url)} "
                f"advertised {len(layers)} feature types but "
                f"{len(broken)} could not be described: {broken}."
            ),
        )
        pytest.fail(f"advertised but unusable WFS feature types: {broken}")

    assert wfs_typename in openable

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-CAP-01",
        timer,
        measured_count=len(openable),
        notes=(
            f"All {len(openable)} feature types advertised by GetCapabilities "
            "resolved through DescribeFeatureType and reported a geometry "
            f"type: {openable}."
        ),
    )


@pytest.mark.cert("NB-GPD-WFS-NS-01")
def test_nb_wfs_ns01_prefixed_and_unprefixed_typename(
    base_url: str,
    wfs_typename: str,
    wfs_evidence: CertificationEvidenceCollector,
) -> None:
    """Prefixed and unprefixed type names resolve to the same feature type.

    The server's own paging links and ``xsi:schemaLocation`` reference the
    *unprefixed* name (``TYPENAMES=test_layer``) while GetCapabilities
    advertises the prefixed one (``honua:test_layer``). If the unprefixed form
    did not resolve, every ``next``/``previous`` link the server emits would be
    broken for a client that follows them.
    """
    timer = CaseTimer()
    local_name = wfs_typename.split(":", 1)[-1]
    base = f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"

    prefixed = geopandas.read_file(f"{base}&TYPENAMES={wfs_typename}")
    unprefixed = geopandas.read_file(f"{base}&TYPENAMES={local_name}")

    assert len(prefixed) == len(unprefixed) == fixture.TOTAL_FEATURES
    assert prefixed["gml_id"].tolist() == unprefixed["gml_id"].tolist()

    record_pass(
        wfs_evidence,
        "NB-GPD-WFS-NS-01",
        timer,
        measured_count=len(unprefixed),
        notes=(
            f"TYPENAMES={wfs_typename!r} and TYPENAMES={local_name!r} returned "
            f"the same {len(prefixed)} features in the same order, so the "
            "unprefixed name used by the server's own paging links and "
            "xsi:schemaLocation is resolvable."
        ),
    )
