# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GeoPandas / pyogrio certification of the OGC API Features surface.

Common core (``CERT-*``) is the floor. The ``NB-GPD-*`` extension cases push
the broadest practical slice of the GeoPandas client API at the server - CRS
negotiation, attribute typing, null handling, temporal/CQL2 filtering, paging
edges, sorting, format round-trips and the error surface - because the point of
certifying a client is to find places where the *server* is wrong.
"""

from __future__ import annotations

import tempfile
import urllib.error
import urllib.parse
from pathlib import Path

import geopandas
import httpx
import pyogrio
import pyogrio.errors
import pytest
from pyproj import CRS, Transformer
from shapely import wkb, wkt

from shared import canonical_fixture as fixture
from shared.cert_envelope import (
    GEOGRAPHIC_TOLERANCE_DEGREES,
    PROJECTED_TOLERANCE_METERS,
    CertificationEvidenceCollector,
)

from .conftest import CaseTimer, record_fail, record_pass, row_identities

pytestmark = pytest.mark.geopandas_client

EPSG_4326_URI = "http://www.opengis.net/def/crs/EPSG/0/4326"
EPSG_3857_URI = "http://www.opengis.net/def/crs/EPSG/0/3857"

#: A declared, queryable field whose name carries a namespace prefix.
NAMESPACED_FIELD = "eo:cloud_cover"


# ---------------------------------------------------------------------------
# Read helpers - every one of these is a real GeoPandas/pyogrio call
# ---------------------------------------------------------------------------

def read_items(items_url: str, **params: object) -> geopandas.GeoDataFrame:
    """Read ``/items`` with OGC query parameters through ``geopandas.read_file``.

    GDAL's OAPIF driver only ever emits ``limit``/``bbox``/``bbox-crs``/``crs``,
    so the parameters the OGC spec defines beyond that (``datetime``,
    ``filter``, ``sortby``, ``offset``, ``properties``) are exercised by handing
    the fully-formed collection URL to ``geopandas.read_file``, which fetches it
    and parses the GeoJSON with pyogrio. It is the same client, one layer down.
    """
    query = urllib.parse.urlencode(
        {key.replace("__", "-"): value for key, value in params.items()},
        quote_via=urllib.parse.quote,
    )
    url = f"{items_url}?{query}" if query else items_url
    return geopandas.read_file(url)


def unknown_collection_items_url(items_url: str) -> str:
    """Return the ``/items`` URL for a collection id the server does not have."""
    collections_root = items_url.rsplit("/collections/", 1)[0]
    return (
        f"{collections_root}/collections/"
        f"{fixture.UNKNOWN_COLLECTION_ID}/items"
    )


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
def test_conn01_pyogrio_opens_oapif_dataset(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """pyogrio opens the OAPIF dataset and materializes the seeded rows."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    assert isinstance(frame, geopandas.GeoDataFrame)
    assert len(frame) == fixture.TOTAL_FEATURES

    record_pass(
        ogc_features_evidence,
        "CERT-CONN-01",
        timer,
        measured_count=len(frame),
        notes=(
            f"pyogrio.read_dataframe('{oapif_dsn}', layer="
            f"'{geopandas_collection_id}') returned {len(frame)} rows via "
            "GDAL's OAPIF driver."
        ),
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport_scheme(
    base_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The lane records the transport posture of the certified endpoint."""
    timer = CaseTimer()
    scheme = urllib.parse.urlparse(base_url).scheme

    assert scheme in {"http", "https"}, f"unexpected transport scheme {scheme!r}"

    ogc_features_evidence.record(
        "CERT-CONN-02",
        "pass" if scheme == "https" else "not-applicable",
        duration_ms=timer.elapsed_ms,
        client_identity="py-geopandas",
        notes=(
            "GeoPandas exercised an HTTPS endpoint and therefore a TLS transport."
            if scheme == "https"
            else "GeoPandas exercised plain HTTP; TLS was not exercised in this run."
        ),
    )


@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_admin_probe_is_rejected(
    base_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
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
        ogc_features_evidence,
        "CERT-AUTH-01",
        timer,
        notes=(
            f"GET {fixture.ADMIN_PROBE_PATH} without credentials returned "
            f"{response.status_code} with WWW-Authenticate={challenge!r}. "
            "httpx is used here because the control plane has no GeoPandas "
            "client surface."
        ),
        client_identity="httpx",
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_api_key_is_accepted(
    base_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
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
        ogc_features_evidence,
        "CERT-AUTH-02",
        timer,
        notes=(
            f"GET {fixture.ADMIN_PROBE_PATH} authenticated with the "
            f"{accepted} scheme (observed: "
            + ", ".join(f"{scheme}->{status}" for scheme, status in attempts)
            + "). httpx is used because the control plane has no GeoPandas "
            "client surface."
        ),
        client_identity="httpx",
    )


@pytest.mark.cert("CERT-DISC-01")
def test_disc01_collections_are_listed(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """``pyogrio.list_layers`` enumerates the server's collections."""
    timer = CaseTimer()
    layers = pyogrio.list_layers(oapif_dsn)
    names = [str(entry[0]) for entry in layers]

    assert geopandas_collection_id in names, (
        f"collection {geopandas_collection_id!r} not advertised; saw {names}"
    )

    record_pass(
        ogc_features_evidence,
        "CERT-DISC-01",
        timer,
        measured_count=len(names),
        notes=f"pyogrio.list_layers discovered {len(names)} collections: {names}.",
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_single_collection_metadata(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """``pyogrio.read_info`` returns per-collection metadata."""
    timer = CaseTimer()
    info = pyogrio.read_info(oapif_dsn, layer=geopandas_collection_id)

    assert info["driver"] == "OAPIF"
    assert info["layer_name"] == geopandas_collection_id
    assert info["features"] == fixture.TOTAL_FEATURES
    assert CRS.from_user_input(info["crs"]).to_epsg() == fixture.STORAGE_CRS_EPSG

    record_pass(
        ogc_features_evidence,
        "CERT-DISC-02",
        timer,
        measured_count=int(info["features"]),
        notes=(
            f"read_info reported driver={info['driver']}, crs={info['crs']}, "
            f"features={info['features']}, geometry_type={info['geometry_type']}, "
            f"metadata title={(info.get('layer_metadata') or {}).get('TITLE')!r}."
        ),
    )


@pytest.mark.cert("CERT-SCHM-01")
def test_schm01_attribute_schema(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Every canonical attribute field reaches the client."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn, layer=geopandas_collection_id, use_arrow=True
    )
    columns = set(frame.columns)
    missing = [name for name in fixture.ATTRIBUTE_FIELDS if name not in columns]

    assert not missing, (
        f"OGC API Features response is missing declared fields {missing}; "
        f"client saw {sorted(columns)}"
    )

    record_pass(
        ogc_features_evidence,
        "CERT-SCHM-01",
        timer,
        measured_count=len(fixture.ATTRIBUTE_FIELDS),
        notes=(
            "The OAPIF driver exposed all "
            f"{len(fixture.ATTRIBUTE_FIELDS)} canonical attribute fields "
            f"({sorted(columns - {'geometry'})}). The read uses pyogrio's "
            "Arrow path (use_arrow=True) because the legacy OGR field path "
            "cannot represent OFTTime or OFTList, and silently drops "
            "event_time/tags/numbers - a pyogrio limitation, not a server "
            "defect; NB-GPD-TYP-04 certifies those three values."
        ),
    )


@pytest.mark.cert("CERT-SCHM-02")
def test_schm02_geometry_type_is_point(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The collection reports a Point geometry type to the client."""
    timer = CaseTimer()
    info = pyogrio.read_info(oapif_dsn, layer=geopandas_collection_id)
    geometry_type = str(info["geometry_type"])

    assert "Point" in geometry_type, f"expected a Point layer, got {geometry_type!r}"

    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    observed = set(frame.geometry.dropna().geom_type.unique())
    assert observed <= {"Point"}, f"non-point geometries returned: {observed}"

    record_pass(
        ogc_features_evidence,
        "CERT-SCHM-02",
        timer,
        measured_count=len(frame.geometry.dropna()),
        notes=(
            f"read_info geometry_type={geometry_type!r}; materialized geometry "
            f"types={sorted(observed)}."
        ),
    )


@pytest.mark.cert("CERT-QFLT-01")
def test_qflt01_attribute_filter(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """An attribute filter selects exactly the active features."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn,
        layer=geopandas_collection_id,
        where=f"{fixture.FILTER_FIELD} = '{fixture.FILTER_VALUE}'",
    )

    assert len(frame) == fixture.ACTIVE_FEATURES
    assert set(frame[fixture.FILTER_FIELD].unique()) == {fixture.FILTER_VALUE}

    record_pass(
        ogc_features_evidence,
        "CERT-QFLT-01",
        timer,
        measured_count=len(frame),
        notes=(
            "pyogrio where=\"status = 'active'\" returned "
            f"{len(frame)} rows. GDAL did NOT push this predicate to the "
            "server: a CPL_DEBUG trace of the same call shows only "
            "'/items?limit=1000&crs=...EPSG/0/4326' being fetched, so the "
            "OAPIF driver evaluated the attribute filter client-side. "
            "Server-side predicate evaluation is certified separately by "
            "NB-GPD-FLT-02 (CQL2-text) which the server does execute."
        ),
    )


@pytest.mark.cert("CERT-QFLT-02")
def test_qflt02_bbox_filter(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A bbox filter selects the canonical subset."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn, layer=geopandas_collection_id, bbox=fixture.SUBSET_BBOX
    )

    assert len(frame) == fixture.SUBSET_BBOX_FEATURE_COUNT

    record_pass(
        ogc_features_evidence,
        "CERT-QFLT-02",
        timer,
        measured_count=len(frame),
        notes=(
            f"bbox={fixture.SUBSET_BBOX} returned {len(frame)} rows. GDAL DID "
            "push this down: the trace shows "
            "'/items?limit=1000&bbox=37.705,-122.495,37.735,-122.455"
            "&bbox-crs=...EPSG/0/4326', i.e. the driver emitted the envelope "
            "in EPSG:4326 lat/lon axis order and the server honoured that "
            "axis convention."
        ),
    )


@pytest.mark.cert("CERT-PAGE-01")
def test_page01_first_page(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A bounded read returns exactly the requested page size."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn,
        layer=geopandas_collection_id,
        max_features=fixture.PAGE_SIZE,
        fid_as_index=True,
    )

    assert len(frame) == fixture.PAGE_SIZE

    record_pass(
        ogc_features_evidence,
        "CERT-PAGE-01",
        timer,
        measured_count=len(frame),
        notes=f"max_features={fixture.PAGE_SIZE} returned {len(frame)} rows.",
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_page02_second_page_is_disjoint(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Skipping the first page yields a disjoint set of features."""
    timer = CaseTimer()
    first = pyogrio.read_dataframe(
        oapif_dsn,
        layer=geopandas_collection_id,
        max_features=fixture.PAGE_SIZE,
        fid_as_index=True,
    )
    second = pyogrio.read_dataframe(
        oapif_dsn,
        layer=geopandas_collection_id,
        skip_features=fixture.PAGE_SIZE,
        max_features=fixture.PAGE_SIZE,
        fid_as_index=True,
    )

    first_ids = row_identities(first)
    second_ids = row_identities(second)
    assert len(second) == fixture.PAGE_SIZE
    assert not (first_ids & second_ids), (
        f"pages overlap: {sorted(first_ids & second_ids)}"
    )

    record_pass(
        ogc_features_evidence,
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
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The anchor feature's coordinates survive the round trip."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    point = _anchor_row(frame).geometry

    delta = max(
        abs(point.x - fixture.ANCHOR_LON), abs(point.y - fixture.ANCHOR_LAT)
    )
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"anchor drifted by {delta} deg (limit {GEOGRAPHIC_TOLERANCE_DEGREES})"
    )

    record_pass(
        ogc_features_evidence,
        "CERT-GEOM-01",
        timer,
        measured_delta=delta,
        notes=(
            f"anchor '{fixture.ANCHOR_NAME}' materialized at "
            f"({point.x}, {point.y}); expected "
            f"({fixture.ANCHOR_LON}, {fixture.ANCHOR_LAT}); max abs deviation "
            f"{delta} deg against a {GEOGRAPHIC_TOLERANCE_DEGREES} deg limit."
        ),
    )


@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_crs_is_wgs84(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The client receives an EPSG:4326 GeoDataFrame."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    assert frame.crs is not None
    assert frame.crs.to_epsg() == fixture.STORAGE_CRS_EPSG

    record_pass(
        ogc_features_evidence,
        "CERT-GEOM-02",
        timer,
        measured_count=fixture.STORAGE_CRS_EPSG,
        notes=f"gdf.crs={frame.crs.to_string()} (EPSG:{frame.crs.to_epsg()}).",
    )


@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_collection(
    oapif_dsn: str,
    items_url: str,
    base_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """An unknown collection raises a structured client error and 404s."""
    timer = CaseTimer()
    with pytest.raises(pyogrio.errors.DataLayerError) as raised:
        pyogrio.read_dataframe(oapif_dsn, layer=fixture.UNKNOWN_COLLECTION_ID)

    message = str(raised.value)
    assert fixture.UNKNOWN_COLLECTION_ID in message, message

    transport = httpx.get(unknown_collection_items_url(items_url), timeout=30.0)

    assert transport.status_code == 404, (
        f"unknown collection returned {transport.status_code}; expected 404"
    )
    assert "problem+json" in transport.headers.get("content-type", ""), (
        "404 was not an RFC 7807 problem document: "
        f"{transport.headers.get('content-type')!r}"
    )

    record_pass(
        ogc_features_evidence,
        "CERT-ERRH-01",
        timer,
        notes=(
            f"pyogrio raised DataLayerError({message!r}); the underlying "
            f"transport shape was verified with httpx: {transport.status_code} "
            f"{transport.headers.get('content-type')}."
        ),
    )


@pytest.mark.cert("CERT-ERRH-02")
def test_errh02_malformed_filter(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A malformed attribute filter is rejected with a structured error."""
    timer = CaseTimer()
    with pytest.raises(ValueError) as raised:
        pyogrio.read_dataframe(
            oapif_dsn,
            layer=geopandas_collection_id,
            where=fixture.MALFORMED_CQL2_FILTER,
        )

    message = str(raised.value)
    assert "Invalid SQL query" in message, message
    assert fixture.MALFORMED_CQL2_FILTER in message, message

    record_pass(
        ogc_features_evidence,
        "CERT-ERRH-02",
        timer,
        notes=f"pyogrio rejected where={fixture.MALFORMED_CQL2_FILTER!r}: {message!r}.",
    )


# ===========================================================================
# Lane extensions - broadened server surface
# ===========================================================================

@pytest.mark.cert("NB-GPD-CRS-01")
def test_nb_crs01_default_axis_order(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Default GeoJSON output is CRS84 lon/lat, not swapped to lat/lon."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    minx, miny, maxx, maxy = frame.total_bounds

    assert -180.0 <= minx <= 180.0 and -90.0 <= miny <= 90.0
    assert minx == pytest.approx(fixture.FIXTURE_BBOX[0], abs=1e-9)
    assert miny == pytest.approx(fixture.FIXTURE_BBOX[1], abs=1e-9)
    assert maxx == pytest.approx(fixture.FIXTURE_BBOX[2], abs=1e-9)
    assert maxy == pytest.approx(fixture.FIXTURE_BBOX[3], abs=1e-9)

    record_pass(
        ogc_features_evidence,
        "NB-GPD-CRS-01",
        timer,
        measured_delta=float(abs(minx - fixture.FIXTURE_BBOX[0])),
        notes=(
            f"total_bounds={tuple(frame.total_bounds)} matches the canonical "
            "lon/lat extent, proving the default GeoJSON output uses CRS84 "
            "axis order rather than EPSG:4326 lat/lon."
        ),
    )


@pytest.mark.cert("NB-GPD-CRS-02")
def test_nb_crs02_server_side_web_mercator(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The OGC API Features Part 2 ``crs`` negotiation returns EPSG:3857."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn, layer=geopandas_collection_id, CRS="EPSG:3857"
    )

    assert frame.crs is not None
    assert frame.crs.to_epsg() == fixture.PROJECTED_CRS_EPSG
    point = _anchor_row(frame).geometry
    transformer = Transformer.from_crs(
        f"EPSG:{fixture.STORAGE_CRS_EPSG}",
        f"EPSG:{fixture.PROJECTED_CRS_EPSG}",
        always_xy=True,
    )
    expected_x, expected_y = transformer.transform(
        fixture.ANCHOR_LON, fixture.ANCHOR_LAT
    )
    delta = max(abs(point.x - expected_x), abs(point.y - expected_y))

    assert delta <= PROJECTED_TOLERANCE_METERS, (
        f"server-side EPSG:3857 reprojection drifted {delta} m"
    )

    record_pass(
        ogc_features_evidence,
        "NB-GPD-CRS-02",
        timer,
        measured_delta=delta,
        notes=(
            "Opened with the OAPIF driver's CRS=EPSG:3857 open option; the "
            f"server returned EPSG:{frame.crs.to_epsg()} and the anchor landed "
            f"{delta} m from the pyproj reference (limit "
            f"{PROJECTED_TOLERANCE_METERS} m)."
        ),
    )


@pytest.mark.cert("NB-GPD-CRS-03")
def test_nb_crs03_server_reprojection_matches_client(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Server-side reprojection agrees with ``GeoDataFrame.to_crs``."""
    timer = CaseTimer()
    native = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    projected = pyogrio.read_dataframe(
        oapif_dsn, layer=geopandas_collection_id, CRS="EPSG:3857"
    )
    client_side = native.to_crs(fixture.PROJECTED_CRS_EPSG)

    native_by_name = client_side.set_index("name").geometry
    server_by_name = projected.set_index("name").geometry
    deltas = []
    for name, expected in native_by_name.items():
        actual = server_by_name.get(name)
        if expected is None or actual is None or expected.is_empty:
            continue
        deltas.append(max(abs(actual.x - expected.x), abs(actual.y - expected.y)))

    assert deltas, "no comparable geometries between server- and client-side CRS"
    worst = max(deltas)
    assert worst <= PROJECTED_TOLERANCE_METERS, (
        f"server reprojection differs from pyproj by {worst} m"
    )

    record_pass(
        ogc_features_evidence,
        "NB-GPD-CRS-03",
        timer,
        measured_count=len(deltas),
        measured_delta=worst,
        notes=(
            f"Compared {len(deltas)} features; worst server-vs-pyproj "
            f"EPSG:3857 deviation {worst} m."
        ),
    )


@pytest.mark.cert("NB-GPD-CRS-04")
def test_nb_crs04_bbox_crs_equivalence(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """``bbox-crs`` in EPSG:3857 selects the same subset as the 4326 bbox."""
    timer = CaseTimer()
    transformer = Transformer.from_crs(
        f"EPSG:{fixture.STORAGE_CRS_EPSG}",
        f"EPSG:{fixture.PROJECTED_CRS_EPSG}",
        always_xy=True,
    )
    minx, miny = transformer.transform(
        fixture.SUBSET_BBOX[0], fixture.SUBSET_BBOX[1]
    )
    maxx, maxy = transformer.transform(
        fixture.SUBSET_BBOX[2], fixture.SUBSET_BBOX[3]
    )
    frame = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        bbox=f"{minx},{miny},{maxx},{maxy}",
        bbox__crs=EPSG_3857_URI,
    )

    assert len(frame) == fixture.SUBSET_BBOX_FEATURE_COUNT, (
        f"EPSG:3857 bbox-crs selected {len(frame)} rows; expected "
        f"{fixture.SUBSET_BBOX_FEATURE_COUNT}"
    )

    record_pass(
        ogc_features_evidence,
        "NB-GPD-CRS-04",
        timer,
        measured_count=len(frame),
        notes=(
            f"bbox={minx},{miny},{maxx},{maxy} with bbox-crs={EPSG_3857_URI} "
            f"returned {len(frame)} rows, matching the EPSG:4326 bbox result."
        ),
    )


@pytest.mark.cert("NB-GPD-TYP-01")
def test_nb_typ01_numeric_typing(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Integer and double fields materialize as numeric dtypes with exact values."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    assert str(frame["count"].dtype).startswith("int"), frame["count"].dtype
    assert str(frame["ratio"].dtype).startswith("float"), frame["ratio"].dtype
    assert sorted(frame["count"].tolist()) == list(
        range(1, fixture.TOTAL_FEATURES + 1)
    )
    anchor = _anchor_row(frame)
    assert anchor["count"] == 1
    assert anchor["ratio"] == pytest.approx(1.25, abs=1e-12)

    record_pass(
        ogc_features_evidence,
        "NB-GPD-TYP-01",
        timer,
        measured_count=len(frame),
        notes=(
            f"count dtype={frame['count'].dtype}, ratio dtype="
            f"{frame['ratio'].dtype}; anchor count=1, ratio=1.25 round-tripped "
            "exactly."
        ),
    )


@pytest.mark.cert("NB-GPD-TYP-02")
def test_nb_typ02_boolean_typing(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The boolean field arrives as a real bool, not 0/1 or "true"/"false"."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    assert str(frame["active"].dtype) == "bool", frame["active"].dtype
    true_count = int(frame["active"].sum())
    assert true_count == fixture.ACTIVE_FEATURES
    assert (frame["active"] == (frame["status"] == fixture.FILTER_VALUE)).all()

    record_pass(
        ogc_features_evidence,
        "NB-GPD-TYP-02",
        timer,
        measured_count=true_count,
        notes=(
            f"active dtype={frame['active'].dtype} with {true_count} true rows, "
            "consistent with status='active' on every row."
        ),
    )


@pytest.mark.cert("NB-GPD-TYP-03")
def test_nb_typ03_temporal_typing(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Timestamp and date fields arrive as datetimes with the right values."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    assert "datetime64" in str(frame["created_at"].dtype), frame["created_at"].dtype
    assert "datetime64" in str(frame["event_date"].dtype), frame["event_date"].dtype

    anchor = _anchor_row(frame)
    assert str(anchor["created_at"])[:19] == "2024-01-01 12:00:00"
    assert str(anchor["event_date"])[:10] == "2024-02-01"
    assert frame["created_at"].is_monotonic_increasing

    record_pass(
        ogc_features_evidence,
        "NB-GPD-TYP-03",
        timer,
        measured_count=len(frame),
        notes=(
            f"created_at dtype={frame['created_at'].dtype} (anchor "
            f"{anchor['created_at']}), event_date dtype="
            f"{frame['event_date'].dtype} (anchor {anchor['event_date']}); "
            "the RFC 3339 timestamps the server emits are parsed as instants, "
            "not strings."
        ),
    )


@pytest.mark.cert("NB-GPD-TYP-04")
def test_nb_typ04_time_and_array_payloads(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Time and JSON-array properties survive to the client verbatim."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(
        oapif_dsn, layer=geopandas_collection_id, use_arrow=True
    )
    anchor = _anchor_row(frame)

    assert str(anchor["event_time"]).startswith("12:34:56"), anchor["event_time"]
    assert list(anchor["tags"]) == ["red", "blue"], anchor["tags"]
    assert list(anchor["numbers"]) == [0, 1, 2], anchor["numbers"]
    assert str(anchor["uid"]) == "00000000-0000-0000-0000-000000000001"
    assert [len(list(value)) for value in frame["tags"]] == [2, 1] * 5

    record_pass(
        ogc_features_evidence,
        "NB-GPD-TYP-04",
        timer,
        measured_count=len(frame),
        notes=(
            f"Through pyogrio's Arrow path: event_time={anchor['event_time']!r} "
            f"(a real time value, not a string), tags={list(anchor['tags'])}, "
            f"numbers={list(anchor['numbers'])}, uid={anchor['uid']!r}. The "
            "same three columns are silently dropped on the legacy OGR field "
            "path because pyogrio cannot represent OFTTime/OFTStringList/"
            "OFTIntegerList, so this case pins the server's payload against "
            "the one client path that can see them."
        ),
    )


@pytest.mark.cert("NB-GPD-NUL-01")
def test_nb_nul01_nullable_column(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A nullable text column preserves nulls rather than coercing to "".*"""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    null_rows = frame["description"].isna()

    assert null_rows.any(), "no nulls survived on the nullable description column"
    assert not null_rows.all(), "every description was null; the column is empty"
    assert bool(null_rows[frame["name"] == fixture.ANCHOR_NAME].iloc[0]), (
        "the anchor feature's null description was materialized as a value"
    )
    assert (frame.loc[~null_rows, "description"] != "").all()

    record_pass(
        ogc_features_evidence,
        "NB-GPD-NUL-01",
        timer,
        measured_count=int(null_rows.sum()),
        notes=(
            f"{int(null_rows.sum())} of {len(frame)} rows carried a null "
            "description; the remainder are non-empty strings, so JSON null "
            "was not coerced to an empty string."
        ),
    )


@pytest.mark.cert("NB-GPD-NUL-02")
def test_nb_nul02_null_geometry_row_survives(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The null-geometry feature is delivered, not dropped and not fabricated."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    missing = frame.geometry.isna() | frame.geometry.is_empty

    assert len(frame) == fixture.TOTAL_FEATURES
    assert int(missing.sum()) == fixture.TOTAL_FEATURES - fixture.FEATURES_WITH_GEOMETRY
    assert frame.loc[missing, "name"].tolist() == ["lambda"]

    record_pass(
        ogc_features_evidence,
        "NB-GPD-NUL-02",
        timer,
        measured_count=int(missing.sum()),
        notes=(
            f"{len(frame)} rows returned with "
            f"{int(missing.sum())} null geometry "
            f"({frame.loc[missing, 'name'].tolist()}); the geometry-less "
            "feature is neither dropped nor given a placeholder geometry."
        ),
    )


@pytest.mark.cert("NB-GPD-GEO-01")
def test_nb_geo01_wkb_wkt_round_trip(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Every returned geometry survives a shapely WKB and WKT round trip."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    geometries = [geom for geom in frame.geometry if geom is not None]

    worst = 0.0
    for geometry in geometries:
        from_wkb = wkb.loads(wkb.dumps(geometry))
        from_wkt = wkt.loads(wkt.dumps(geometry, rounding_precision=-1))
        worst = max(
            worst,
            abs(from_wkb.x - geometry.x),
            abs(from_wkb.y - geometry.y),
            abs(from_wkt.x - geometry.x),
            abs(from_wkt.y - geometry.y),
        )

    assert len(geometries) == fixture.FEATURES_WITH_GEOMETRY
    assert worst <= GEOGRAPHIC_TOLERANCE_DEGREES

    record_pass(
        ogc_features_evidence,
        "NB-GPD-GEO-01",
        timer,
        measured_count=len(geometries),
        measured_delta=worst,
        notes=(
            f"{len(geometries)} geometries round-tripped through shapely WKB "
            f"and WKT with a worst-case deviation of {worst} deg."
        ),
    )


@pytest.mark.cert("NB-GPD-GEO-02")
def test_nb_geo02_bounds_within_declared_extent(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Materialized bounds match the fixture and sit inside the declared extent."""
    timer = CaseTimer()
    info = pyogrio.read_info(oapif_dsn, layer=geopandas_collection_id)
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    data_bounds = tuple(float(value) for value in frame.total_bounds)
    declared = tuple(float(value) for value in info["total_bounds"])

    assert data_bounds == pytest.approx(fixture.FIXTURE_BBOX, abs=1e-9)
    assert declared[0] <= data_bounds[0] and declared[1] <= data_bounds[1]
    assert declared[2] >= data_bounds[2] and declared[3] >= data_bounds[3]

    record_pass(
        ogc_features_evidence,
        "NB-GPD-GEO-02",
        timer,
        measured_count=len(frame),
        notes=(
            f"declared collection extent {declared} contains the materialized "
            f"data bounds {data_bounds}, which equal the canonical fixture "
            "extent."
        ),
    )


@pytest.mark.cert("NB-GPD-FLT-01")
def test_nb_flt01_temporal_filter(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The OGC ``datetime`` interval parameter is honoured server-side."""
    timer = CaseTimer()
    frame = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        datetime="2024-01-03T00:00:00Z/2024-01-05T23:59:59Z",
    )
    names = sorted(frame["name"].tolist())

    assert names == ["delta", "epsilon", "gamma"], names

    record_pass(
        ogc_features_evidence,
        "NB-GPD-FLT-01",
        timer,
        measured_count=len(frame),
        notes=(
            "datetime=2024-01-03T00:00:00Z/2024-01-05T23:59:59Z selected "
            f"{names}, i.e. exactly the three features whose created_at falls "
            "inside the interval."
        ),
    )


@pytest.mark.cert("NB-GPD-FLT-02")
def test_nb_flt02_cql2_text_filter(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A CQL2-text predicate is evaluated by the server, not the client."""
    timer = CaseTimer()
    frame = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        filter="count > 5",
        filter__lang="cql2-text",
    )

    assert len(frame) == 5, frame["count"].tolist()
    assert min(frame["count"].tolist()) > 5

    combined = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        filter=f"count > 5 AND status = '{fixture.FILTER_VALUE}'",
        filter__lang="cql2-text",
    )
    assert set(combined["status"].unique()) == {fixture.FILTER_VALUE}

    record_pass(
        ogc_features_evidence,
        "NB-GPD-FLT-02",
        timer,
        measured_count=len(frame),
        notes=(
            f"filter='count > 5' returned {len(frame)} rows "
            f"({sorted(frame['count'].tolist())}); the conjunction with "
            f"status='{fixture.FILTER_VALUE}' returned {len(combined)} rows, "
            "so the server evaluates CQL2-text predicates including AND."
        ),
    )


@pytest.mark.cert("NB-GPD-FLT-03")
def test_nb_flt03_zero_match_is_an_empty_frame(
    oapif_dsn: str,
    geopandas_collection_id: str,
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A filter matching nothing yields an empty GeoDataFrame, not an error."""
    timer = CaseTimer()
    driver_side = pyogrio.read_dataframe(
        oapif_dsn,
        layer=geopandas_collection_id,
        where=f"{fixture.FILTER_FIELD} = 'no-such-status'",
    )
    server_side = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        filter="count > 10000",
        filter__lang="cql2-text",
    )

    assert isinstance(driver_side, geopandas.GeoDataFrame)
    assert len(driver_side) == 0
    assert fixture.FILTER_FIELD in driver_side.columns
    assert len(server_side) == 0

    record_pass(
        ogc_features_evidence,
        "NB-GPD-FLT-03",
        timer,
        measured_count=0,
        notes=(
            "Both an unmatched OGR attribute filter and an unmatched CQL2 "
            "predicate produced empty GeoDataFrames with the schema intact "
            "rather than an HTTP error or a malformed payload."
        ),
    )


@pytest.mark.cert("NB-GPD-FLT-04")
def test_nb_flt04_out_of_range_bbox(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A valid bbox far from the data returns nothing rather than failing."""
    timer = CaseTimer()
    frame = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        bbox="-60.0,-179.0,-50.0,-170.0",
        bbox__crs=EPSG_4326_URI,
    )

    assert len(frame) == 0

    record_pass(
        ogc_features_evidence,
        "NB-GPD-FLT-04",
        timer,
        measured_count=0,
        notes=(
            "An Antarctic bbox in EPSG:4326 (lat/lon axis order) returned an "
            "empty FeatureCollection, confirming the server treats a "
            "disjoint-but-valid envelope as a zero-result query."
        ),
    )


@pytest.mark.cert("NB-GPD-PAG-01")
def test_nb_pag01_full_pagination(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Walking every page with limit/offset reproduces the collection exactly."""
    timer = CaseTimer()
    seen: set[str] = set()
    pages = 0
    offset = 0
    while offset < fixture.TOTAL_FEATURES + fixture.PAGE_SIZE:
        page = read_items(items_url, limit=fixture.PAGE_SIZE, offset=offset)
        if len(page) == 0:
            break
        pages += 1
        page_names = set(page["name"].tolist())
        assert not (seen & page_names), (
            f"offset={offset} repeated features {sorted(seen & page_names)}"
        )
        seen |= page_names
        offset += fixture.PAGE_SIZE

    assert len(seen) == fixture.TOTAL_FEATURES, sorted(seen)

    record_pass(
        ogc_features_evidence,
        "NB-GPD-PAG-01",
        timer,
        measured_count=len(seen),
        notes=(
            f"{pages} pages of limit={fixture.PAGE_SIZE} produced "
            f"{len(seen)} distinct features with no repeats and no gaps, "
            "so limit/offset paging is stable and complete."
        ),
    )


@pytest.mark.cert("NB-GPD-PAG-02")
def test_nb_pag02_paging_edges(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A limit above the server maximum clamps; an offset past the end empties."""
    timer = CaseTimer()
    oversized = read_items(items_url, limit=1_000_000)
    past_end = read_items(items_url, limit=fixture.PAGE_SIZE, offset=10_000)

    assert len(oversized) == fixture.TOTAL_FEATURES, (
        "an oversized limit must be clamped to the server maximum, not "
        f"rejected; got {len(oversized)} rows"
    )
    assert len(past_end) == 0

    record_pass(
        ogc_features_evidence,
        "NB-GPD-PAG-02",
        timer,
        measured_count=len(oversized),
        notes=(
            f"limit=1000000 was clamped and returned {len(oversized)} rows; "
            "offset=10000 returned an empty FeatureCollection rather than an "
            "error."
        ),
    )


@pytest.mark.cert("NB-GPD-SRT-01")
def test_nb_srt01_sortby_ordering(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """``sortby`` orders results and stays stable across page boundaries."""
    timer = CaseTimer()
    descending = read_items(
        items_url, limit=fixture.TOTAL_FEATURES, sortby="-count"
    )
    first_page = read_items(items_url, limit=fixture.PAGE_SIZE, sortby="-count")
    second_page = read_items(
        items_url,
        limit=fixture.PAGE_SIZE,
        offset=fixture.PAGE_SIZE,
        sortby="-count",
    )

    counts = descending["count"].tolist()
    assert counts == sorted(counts, reverse=True), counts
    assert first_page["count"].tolist() == counts[: fixture.PAGE_SIZE]
    assert second_page["count"].tolist() == counts[
        fixture.PAGE_SIZE : 2 * fixture.PAGE_SIZE
    ]

    record_pass(
        ogc_features_evidence,
        "NB-GPD-SRT-01",
        timer,
        measured_count=len(counts),
        notes=(
            f"sortby=-count produced {counts}; the first two pages match the "
            "corresponding slices of the full ordering, so the sort is applied "
            "before paging."
        ),
    )


@pytest.mark.cert("NB-GPD-IO-01")
def test_nb_io01_ogr_format_round_trip(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Server output survives a GPKG and FlatGeobuf write/read round trip."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)
    results: dict[str, int] = {}
    worst = 0.0

    with tempfile.TemporaryDirectory() as workdir:
        # FlatGeobuf refuses NULL geometries when it builds a packed Hilbert
        # R-tree, and the canonical fixture deliberately contains one, so the
        # index is disabled for that driver only.
        for driver, suffix, options in (
            ("GPKG", ".gpkg", {}),
            ("FlatGeobuf", ".fgb", {"SPATIAL_INDEX": "NO"}),
        ):
            path = Path(workdir) / f"round-trip{suffix}"
            pyogrio.write_dataframe(frame, path, driver=driver, **options)
            reloaded = pyogrio.read_dataframe(path)
            results[driver] = len(reloaded)
            assert len(reloaded) == len(frame), driver
            original = frame.set_index("name").geometry
            restored = reloaded.set_index("name").geometry
            for name, geometry in original.items():
                other = restored.get(name)
                if geometry is None or other is None:
                    assert geometry is None and other is None, name
                    continue
                worst = max(
                    worst, abs(other.x - geometry.x), abs(other.y - geometry.y)
                )

    assert worst <= GEOGRAPHIC_TOLERANCE_DEGREES

    record_pass(
        ogc_features_evidence,
        "NB-GPD-IO-01",
        timer,
        measured_count=len(frame),
        measured_delta=worst,
        notes=(
            f"Round-tripped the server response through {results}; worst "
            f"coordinate deviation {worst} deg and the null-geometry row "
            "stayed null in both formats."
        ),
    )


@pytest.mark.cert("NB-GPD-IO-02")
def test_nb_io02_geoparquet_round_trip(
    oapif_dsn: str,
    geopandas_collection_id: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Server output survives a GeoParquet round trip with CRS intact."""
    timer = CaseTimer()
    frame = pyogrio.read_dataframe(oapif_dsn, layer=geopandas_collection_id)

    with tempfile.TemporaryDirectory() as workdir:
        path = Path(workdir) / "round-trip.parquet"
        frame.to_parquet(path)
        reloaded = geopandas.read_parquet(path)

    assert len(reloaded) == len(frame)
    assert reloaded.crs is not None
    assert reloaded.crs.to_epsg() == fixture.STORAGE_CRS_EPSG
    assert reloaded["count"].tolist() == frame["count"].tolist()
    assert str(reloaded["created_at"].dtype) == str(frame["created_at"].dtype)

    record_pass(
        ogc_features_evidence,
        "NB-GPD-IO-02",
        timer,
        measured_count=len(reloaded),
        notes=(
            f"GeoParquet round trip preserved {len(reloaded)} rows, "
            f"EPSG:{reloaded.crs.to_epsg()} and the "
            f"{frame['created_at'].dtype} timestamp dtype."
        ),
    )


@pytest.mark.cert("NB-GPD-ENG-01")
def test_nb_eng01_fiona_and_pyogrio_agree(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The fiona and pyogrio engines read the same server response identically."""
    timer = CaseTimer()
    url = f"{items_url}?limit={fixture.TOTAL_FEATURES}"
    via_pyogrio = geopandas.read_file(url, engine="pyogrio")
    via_fiona = geopandas.read_file(url, engine="fiona")

    assert len(via_fiona) == len(via_pyogrio) == fixture.TOTAL_FEATURES
    assert via_fiona["name"].tolist() == via_pyogrio["name"].tolist()
    assert via_fiona.crs == via_pyogrio.crs
    worst = 0.0
    for left, right in zip(via_fiona.geometry, via_pyogrio.geometry):
        if left is None or right is None:
            assert left is None and right is None
            continue
        worst = max(worst, abs(left.x - right.x), abs(left.y - right.y))
    assert worst <= GEOGRAPHIC_TOLERANCE_DEGREES

    record_pass(
        ogc_features_evidence,
        "NB-GPD-ENG-01",
        timer,
        measured_count=len(via_fiona),
        measured_delta=worst,
        notes=(
            "geopandas.read_file with engine='fiona' and engine='pyogrio' "
            "(two independently vendored GDAL builds) agreed on row count, "
            f"ordering, CRS and geometry to within {worst} deg."
        ),
    )


@pytest.mark.cert("NB-GPD-ERR-01")
def test_nb_err01_error_status_distinction(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """The server distinguishes "not found" from "bad request" for the client."""
    timer = CaseTimer()
    observed: dict[str, int | None] = {}

    with pytest.raises(urllib.error.HTTPError) as not_found:
        geopandas.read_file(unknown_collection_items_url(items_url))
    observed["unknown-collection"] = not_found.value.code

    with pytest.raises(urllib.error.HTTPError) as bad_crs:
        geopandas.read_file(f"{items_url}?limit=1&crs=not-a-crs")
    observed["malformed-crs"] = bad_crs.value.code

    with pytest.raises(urllib.error.HTTPError) as bad_filter:
        geopandas.read_file(
            f"{items_url}?limit=1&filter="
            + urllib.parse.quote(fixture.MALFORMED_CQL2_FILTER)
            + "&filter-lang=cql2-text"
        )
    observed["malformed-cql2"] = bad_filter.value.code

    assert observed["unknown-collection"] == 404, observed
    assert observed["malformed-crs"] == 400, observed
    assert observed["malformed-cql2"] == 400, observed

    record_pass(
        ogc_features_evidence,
        "NB-GPD-ERR-01",
        timer,
        measured_count=len(observed),
        notes=(
            f"Status codes observed by the client: {observed}. A missing "
            "resource is 404 while malformed CRS and CQL2 inputs are 400, so "
            "a GeoPandas caller can tell retryable input errors from a wrong "
            "URL."
        ),
    )


@pytest.mark.cert("NB-GPD-AUTH-01")
def test_nb_auth01_wrong_api_key_is_unauthorized(
    base_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """A wrong API key is 401, never 403 and never 500."""
    timer = CaseTimer()
    response = httpx.get(
        f"{base_url}{fixture.ADMIN_PROBE_PATH}",
        headers={fixture.ADMIN_API_KEY_HEADER: "definitely-not-the-admin-key"},
        timeout=30.0,
    )

    assert response.status_code == 401, (
        f"a wrong API key returned {response.status_code}; expected 401"
    )

    record_pass(
        ogc_features_evidence,
        "NB-GPD-AUTH-01",
        timer,
        measured_count=response.status_code,
        notes=(
            "An invalid X-API-Key produced 401 (not 403, which would imply an "
            "authenticated-but-forbidden principal, and not 500)."
        ),
        client_identity="httpx",
    )


@pytest.mark.cert("NB-GPD-SCH-01")
def test_nb_sch01_declared_queryable_reaches_the_client(
    items_url: str,
    ogc_features_evidence: CertificationEvidenceCollector,
) -> None:
    """Every field the collection advertises as queryable must be readable.

    ``eo:cloud_cover`` is declared in ``/queryables``, is filterable through
    CQL2 and is returned in full by WFS GetFeature, but never appears in the
    OGC API Features feature ``properties`` and cannot be requested through the
    ``properties`` parameter. This case is deliberately strict: a queryable a
    client can filter on but never read back is a server defect.
    """
    timer = CaseTimer()
    frame = read_items(items_url, limit=fixture.TOTAL_FEATURES)
    filtered = read_items(
        items_url,
        limit=fixture.TOTAL_FEATURES,
        filter=f'"{NAMESPACED_FIELD}" > 50',
        filter__lang="cql2-text",
    )
    try:
        selected = read_items(
            items_url, limit=1, properties=f"name,{NAMESPACED_FIELD}"
        )
        properties_outcome = f"HTTP 200 with columns {sorted(selected.columns)}"
    except urllib.error.HTTPError as error:
        properties_outcome = f"HTTP {error.code}"

    if NAMESPACED_FIELD in frame.columns:
        record_pass(
            ogc_features_evidence,
            "NB-GPD-SCH-01",
            timer,
            measured_count=len(frame.columns),
            notes=(
                f"The declared queryable {NAMESPACED_FIELD!r} is present in "
                f"the feature properties and filterable ({len(filtered)} rows "
                f"matched > 50); ?properties= answered {properties_outcome}."
            ),
        )
        return

    record_fail(
        ogc_features_evidence,
        "NB-GPD-SCH-01",
        timer,
        measured_count=len(filtered),
        notes=(
            "SERVER DEFECT (honua-server, OGC API Features read projection). "
            "The field 'eo:cloud_cover' is declared in tests/seed/"
            "client-compat-v1.sql, stored in every feature's attributes jsonb, "
            "advertised by /collections/0/queryables, listed in the f=csv "
            "header and in the FeatureServer 'fields' array, and accepted by "
            "CQL2 (filter='\"eo:cloud_cover\" > 50' matched "
            f"{len(filtered)} features, the correct answer). It is nonetheless "
            "MISSING from every OGC API Features feature 'properties' object, "
            "emitted as an empty f=csv cell, and missing from FeatureServer "
            "'attributes' even with outFields=eo:cloud_cover - while WFS 2.0 "
            "GetFeature returns it with correct values, so the data reaches "
            "at least one protocol adapter intact. Selecting it explicitly "
            f"with '?properties=name,{NAMESPACED_FIELD}' answered "
            f"{properties_outcome}. The syntactic gate on that parameter is "
            "OgcFeaturesQueryParameterAdapter.IsSimpleFieldName "
            "(src/Honua.Protocols.OgcApi/Features/Services/"
            "OgcFeaturesQueryParameterAdapter.cs); a matching identifier "
            "restriction lives at "
            "FeatureQueryBuilder.Validation.ValidFieldNameRegex "
            "(^[a-zA-Z_][a-zA-Z0-9_]*$), which THROWS on such a name, so a "
            "correct fix has to span the protocol adapter and the provider "
            "projection together. Client saw columns: "
            f"{sorted(frame.columns)}."
        ),
    )
    pytest.fail(
        "Declared queryable 'eo:cloud_cover' never reaches the client through "
        "OGC API Features; see the recorded NB-GPD-SCH-01 note."
    )
