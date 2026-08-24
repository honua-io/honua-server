# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Certification cases for the registered ``py-pystac`` STAC lane (honua-server#3392).

Two vocabularies live here:

``CERT-*``
    The 16 applicable common-core facets declared by
    :mod:`stac_client.cert_lane`. These are the cross-client comparable rows
    the certification matrix and the baseline gate read.

``NB-STAC-<AREA>-<NN>``
    Lane-specific extension cases. The common core is the *floor*: certifying a
    client is a claim about the SERVER, so these push pystac / pystac-client
    across the broadest practical slice of the STAC API — every declared
    conformance class, the whole Item Search parameter surface, GET/POST
    parity, pagination to exhaustion, item/asset content, CRS axis order, and
    the error surface. The shared collector routes any non-common-core ID into
    the envelope's ``extensions[]`` array automatically.

Everything is driven through the real client library except where pystac-client
exposes no observable surface — the control-plane ``CERT-AUTH-*`` probe, the
transport-shape assertions, and the error-surface cases whose evidence is the
RFC 7807 body. Those say so in their ``notes``.
"""

from __future__ import annotations

import importlib.util
import json
import math
import subprocess
import sys
import time
from collections.abc import Iterable, Sequence
from typing import Any

import pystac
import pytest
from pystac_client import Client
from pystac_client.conformance import ConformanceClasses
from pystac_client.exceptions import APIError
from pystac_client.stac_api_io import StacApiIO

from shared import canonical_fixture, cert_envelope
from shared.cert_envelope import CertificationEvidenceCollector

from . import cert_lane


pytestmark = [pytest.mark.integration, pytest.mark.stac]


# ---------------------------------------------------------------------------
# Fixture-derived expectations
# ---------------------------------------------------------------------------

COLLECTION_ID = canonical_fixture.COLLECTION_ID

#: Attribute names the STAC surface projects into ``properties`` for every
#: seeded item. ``description`` is excluded because it is NULL for several
#: seeded rows and a null attribute is legitimately omitted from properties.
REQUIRED_ITEM_PROPERTIES: tuple[str, ...] = (
    "name", "status", "count", "ratio", "active",
    "created_at", "event_date", "event_time", "uid", "tags", "numbers",
)

#: The STAC EO cloud-cover queryable the fixture seeds on every geometry-bearing
#: row. Declared in the collection's queryables document and filterable, so it
#: must also be projected into item properties.
CLOUD_COVER_PROPERTY = "eo:cloud_cover"
ANCHOR_CLOUD_COVER = 5.0

#: Item id / name pairs from ``tests/seed/client-compat-v1.sql``.
ANCHOR_ITEM_ID = "1"
NULL_GEOMETRY_ITEM_NAME = "lambda"

#: The first three seeded points, which ``SUBSET_BBOX`` selects.
SUBSET_ITEM_IDS = frozenset({"1", "2", "3"})


# ---------------------------------------------------------------------------
# Session fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session")
def landing_page(base_url: str) -> dict[str, Any]:
    """Raw STAC landing page, used for conformance-class gap checks."""
    return cert_lane.read_landing_page(base_url)


@pytest.fixture(scope="session")
def api_client(stac_api_url: str) -> Client:
    """A single ``pystac_client.Client`` shared by the certification cases."""
    return Client.open(stac_api_url)


@pytest.fixture(scope="session")
def all_items(api_client: Client) -> list[pystac.Item]:
    """Every seeded item in the canonical collection, fetched through the client."""
    search = api_client.search(collections=[COLLECTION_ID], limit=100)
    return list(search.items())


@pytest.fixture(scope="session")
def anchor_item(all_items: Sequence[pystac.Item]) -> pystac.Item:
    """The ``alpha`` item — the geometry-fidelity and schema anchor."""
    for item in all_items:
        if item.properties.get("name") == canonical_fixture.ANCHOR_NAME:
            return item
    raise AssertionError(
        f"seeded anchor item {canonical_fixture.ANCHOR_NAME!r} was not returned by item search"
    )


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _elapsed_ms(started: float) -> int:
    return int((time.perf_counter() - started) * 1000)


def _require_stac_api_classes(
    collector: CertificationEvidenceCollector,
    case_id: str,
    landing: dict[str, Any],
    suffixes: Iterable[str],
) -> None:
    """Fail loudly, naming the class, when a facet's conformance class is absent.

    A declared-but-absent conformance class is a server gap, not a client
    limitation, so it is recorded ``fail`` (never ``not-applicable``) with the
    missing class named in the note.
    """
    missing = cert_lane.missing_stac_api_classes(landing, suffixes)
    if not missing:
        return
    note = (
        f"Server does not declare STAC API conformance class(es) {', '.join(missing)}; "
        f"{case_id} cannot be substantiated against this deployment."
    )
    collector.record(case_id, "fail", notes=note)
    pytest.fail(note, pytrace=False)


def _item_ids(items: Iterable[pystac.Item]) -> list[str]:
    return [item.id for item in items]


def _coords(item: pystac.Item) -> tuple[float, float]:
    geometry = item.geometry
    assert isinstance(geometry, dict), f"item {item.id} carries no GeoJSON geometry"
    assert geometry.get("type") == "Point", f"item {item.id} geometry is {geometry.get('type')!r}"
    lon, lat = geometry["coordinates"][0], geometry["coordinates"][1]
    return float(lon), float(lat)


def _validate_with_pystac(obj: pystac.STACObject) -> tuple[bool, str]:
    """Validate a STAC object against the published schemas.

    Returns ``(validated, note)``. A genuine schema violation propagates as
    ``STACValidationError``; anything else (no network route to
    ``schemas.stacspec.org`` from the lane container, for instance) degrades to
    ``validated=False`` with an explanatory note instead of a false failure.
    """
    try:
        obj.validate()
        return True, "validated against the published STAC JSON Schemas"
    except pystac.errors.STACValidationError:
        raise
    except Exception as error:  # noqa: BLE001 - schema fetch failures are environmental
        return False, (
            "pystac schema validation skipped - the published STAC schemas were "
            f"unreachable from the lane ({type(error).__name__}: {str(error)[:160]})"
        )


# ===========================================================================
# CERT-CONN — connection
# ===========================================================================

@pytest.mark.cert("CERT-CONN-01")
def test_cert_conn_01_client_open_returns_stac_catalog(
    stac_api_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``Client.open`` succeeds and the landing page is a valid STAC Catalog."""
    started = time.perf_counter()
    client = Client.open(stac_api_url)

    assert isinstance(client, Client)
    assert client.STAC_OBJECT_TYPE == pystac.STACObjectType.CATALOG
    assert client.id, "STAC landing page must carry a non-empty catalog id"
    assert client.conforms_to(ConformanceClasses.CORE), (
        "landing page does not advertise the STAC API core conformance class"
    )

    document = client.to_dict(include_self_link=True, transform_hrefs=False)
    assert document.get("type") == "Catalog"
    assert document.get("stac_version"), "landing page omits stac_version"

    # Rehydrating through plain pystac proves the document is a well-formed
    # Catalog independently of pystac-client's API-aware subclass.
    rehydrated = pystac.Catalog.from_dict(dict(document))
    assert rehydrated.id == client.id

    cert_collector.record(
        "CERT-CONN-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(cert_lane.declared_conformance(document) or ()),
        notes=(
            f"pystac_client.Client.open({stac_api_url}) returned STAC "
            f"{document.get('stac_version')} Catalog id={client.id!r}; "
            "pystac.Catalog.from_dict rehydrated the same document."
        ),
        evidence_ref=stac_api_url,
    )


@pytest.mark.cert("CERT-CONN-02")
def test_cert_conn_02_transport_scheme(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The lane's transport is the plain-HTTP compose network."""
    started = time.perf_counter()
    scheme = cert_lane.transport_scheme(base_url)
    assert scheme in {"http", "https"}, f"unsupported transport scheme {scheme!r}"

    # Transport-shape check: pystac-client exposes no connection-level surface,
    # so the scheme is asserted on the response URL rather than the requested one
    # (an unnoticed redirect to another scheme would show up here).
    response = cert_lane.get_json(cert_lane.stac_root_url(base_url))
    assert response.status_code == 200
    assert response.url.scheme == scheme, (
        f"transport upgraded from {scheme} to {response.url.scheme} en route"
    )

    if scheme == "https":
        note = "Target is served over TLS; pystac-client completed the handshake and read /stac."
    else:
        note = (
            "docker/client-compat runs an HTTP-only bridge network, so this lane "
            "certifies cleartext transport; TLS is exercised in the release tier "
            "against the HTTPS candidate."
        )

    cert_collector.record(
        "CERT-CONN-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=note,
        evidence_ref=base_url,
    )


# ===========================================================================
# CERT-AUTH — control plane
# ===========================================================================

@pytest.mark.cert("CERT-AUTH-01")
def test_cert_auth_01_admin_probe_rejects_anonymous(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """An anonymous control-plane request is rejected."""
    started = time.perf_counter()
    url = cert_lane.admin_probe_url(base_url)

    # Raw httpx: the admin surface is not a STAC endpoint, and the WWW-Authenticate
    # challenge - the substantive evidence here - is not reachable through any
    # pystac-client API.
    response = cert_lane.get_json(url)
    assert response.status_code in (401, 403), (
        f"anonymous {canonical_fixture.ADMIN_PROBE_PATH} returned "
        f"{response.status_code}; the control plane is not enforcing auth"
    )
    challenge = response.headers.get("WWW-Authenticate", "")
    assert "ApiKey" in challenge, (
        f"401 carried no ApiKey challenge (WWW-Authenticate={challenge!r})"
    )
    assert canonical_fixture.ADMIN_API_KEY_HEADER in challenge, (
        f"challenge does not name the {canonical_fixture.ADMIN_API_KEY_HEADER} header: {challenge!r}"
    )

    # The same rejection through pystac-client's own transport, so the lane
    # proves the client surfaces it as a structured APIError rather than a hang.
    with pytest.raises(APIError) as raised:
        StacApiIO(max_retries=0).request(url)
    assert getattr(raised.value, "status_code", None) == response.status_code

    cert_collector.record(
        "CERT-AUTH-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"Anonymous GET {canonical_fixture.ADMIN_PROBE_PATH} -> "
            f"{response.status_code} with WWW-Authenticate={challenge!r}; "
            "pystac-client's StacApiIO surfaced the same status as APIError. "
            "Raw httpx used because the control plane is not a STAC endpoint."
        ),
        evidence_ref=url,
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_cert_auth_02_admin_probe_accepts_api_key(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The control plane admits the canonical admin credential."""
    started = time.perf_counter()
    url = cert_lane.admin_probe_url(base_url)
    headers = cert_lane.admin_auth_headers()

    # Driven through pystac-client's StacApiIO (which accepts per-session
    # headers exactly the way Client.open(url, headers=...) does), so the
    # authenticated path is exercised by the real client transport.
    body = StacApiIO(headers=dict(headers), max_retries=0).request(url)
    assert body, "authenticated admin probe returned an empty body"
    payload = json.loads(body)
    assert isinstance(payload, (dict, list))

    # Raw httpx confirms the status code itself (StacApiIO only yields the body).
    response = cert_lane.get_json(url, headers=headers)
    assert 200 <= response.status_code < 300, (
        f"authenticated {canonical_fixture.ADMIN_PROBE_PATH} returned "
        f"{response.status_code}: {response.text[:200]}"
    )

    cert_collector.record(
        "CERT-AUTH-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"GET {canonical_fixture.ADMIN_PROBE_PATH} with "
            f"{canonical_fixture.ADMIN_API_KEY_HEADER} -> {response.status_code}. "
            "Honua's control plane is API-key authenticated; HTTP Basic is an "
            "opt-in compatibility mode that also refuses non-HTTPS transport, and "
            "there is no bearer/login flow for this surface."
        ),
        evidence_ref=url,
    )


# ===========================================================================
# CERT-DISC — discovery
# ===========================================================================

@pytest.mark.cert("CERT-DISC-01")
def test_cert_disc_01_get_collections(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``client.get_collections()`` enumerates the served collections."""
    _require_stac_api_classes(
        cert_collector, "CERT-DISC-01", landing_page, [cert_lane.CONFORMANCE_COLLECTIONS]
    )
    started = time.perf_counter()

    collections = list(api_client.get_collections())
    ids = [collection.id for collection in collections]

    assert collections, "/stac/collections returned no collections"
    assert COLLECTION_ID in ids, (
        f"canonical collection {COLLECTION_ID!r} missing from {ids}"
    )
    assert len(ids) == len(set(ids)), f"duplicate collection ids returned: {ids}"

    cert_collector.record(
        "CERT-DISC-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(collections),
        notes=(
            f"pystac_client.Client.get_collections() yielded {len(collections)} "
            f"collection(s): {', '.join(sorted(ids))}."
        ),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_cert_disc_02_get_collection_is_valid(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A collection fetched by id satisfies the STAC Collection spec."""
    _require_stac_api_classes(
        cert_collector, "CERT-DISC-02", landing_page, [cert_lane.CONFORMANCE_COLLECTIONS]
    )
    started = time.perf_counter()

    collection = api_client.get_collection(COLLECTION_ID)
    assert collection is not None
    assert collection.id == COLLECTION_ID

    document = collection.to_dict(include_self_link=True, transform_hrefs=False)
    assert document.get("type") == "Collection"
    for required in ("stac_version", "id", "description", "license", "extent", "links"):
        assert document.get(required) not in (None, "", [], {}), (
            f"collection omits required member {required!r}"
        )

    rehydrated = pystac.Collection.from_dict(dict(document))
    assert rehydrated.id == COLLECTION_ID
    assert rehydrated.extent.spatial.bboxes, "collection declares no spatial extent"
    assert rehydrated.extent.temporal.intervals, "collection declares no temporal extent"

    validated, validation_note = _validate_with_pystac(rehydrated)

    cert_collector.record(
        "CERT-DISC-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(document.get("links", [])),
        notes=(
            f"Client.get_collection({COLLECTION_ID!r}) rehydrated under "
            f"pystac.Collection with license={document.get('license')!r} and "
            f"stac_version={document.get('stac_version')}; {validation_note}."
        ),
        evidence_ref="schema-validated" if validated else "structure-validated",
    )


# ===========================================================================
# CERT-SCHM — schema fidelity
# ===========================================================================

@pytest.mark.cert("CERT-SCHM-01")
def test_cert_schm_01_item_properties(
    anchor_item: pystac.Item,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Item properties expose the seeded attribute schema, including ``eo:cloud_cover``."""
    started = time.perf_counter()
    properties = anchor_item.properties

    missing = [name for name in REQUIRED_ITEM_PROPERTIES if name not in properties]
    assert not missing, f"item {anchor_item.id} omits seeded properties {missing}"

    assert properties.get("datetime"), "item omits the required datetime property"
    assert anchor_item.datetime is not None, "pystac could not parse properties.datetime"

    assert CLOUD_COVER_PROPERTY in properties, (
        f"item {anchor_item.id} omits {CLOUD_COVER_PROPERTY!r}, which the seed sets and "
        "the collection's queryables document declares as a filterable numeric property"
    )
    cloud_cover = properties[CLOUD_COVER_PROPERTY]
    assert isinstance(cloud_cover, (int, float)) and not isinstance(cloud_cover, bool), (
        f"{CLOUD_COVER_PROPERTY} came back as {type(cloud_cover).__name__}"
    )
    assert math.isclose(float(cloud_cover), ANCHOR_CLOUD_COVER, rel_tol=0, abs_tol=1e-9)

    validated, validation_note = _validate_with_pystac(anchor_item)

    cert_collector.record(
        "CERT-SCHM-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(properties),
        notes=(
            f"Item {anchor_item.id} exposed {len(properties)} properties covering the "
            f"seeded schema plus {CLOUD_COVER_PROPERTY}={cloud_cover}; {validation_note}."
        ),
        evidence_ref="schema-validated" if validated else "structure-validated",
    )


@pytest.mark.cert("CERT-SCHM-02")
def test_cert_schm_02_geometry_type(
    anchor_item: pystac.Item,
    all_items: Sequence[pystac.Item],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Item geometry type is reported correctly."""
    started = time.perf_counter()

    geometry = anchor_item.geometry
    assert isinstance(geometry, dict)
    assert geometry.get("type") == "Point", (
        f"expected Point geometry, got {geometry.get('type')!r}"
    )
    assert len(geometry.get("coordinates", [])) >= 2

    geometry_bearing = [item for item in all_items if isinstance(item.geometry, dict)]
    types = {item.geometry["type"] for item in geometry_bearing}
    assert types == {"Point"}, f"unexpected geometry types in the fixture collection: {types}"
    assert len(geometry_bearing) == canonical_fixture.FEATURES_WITH_GEOMETRY, (
        f"expected {canonical_fixture.FEATURES_WITH_GEOMETRY} geometry-bearing items, "
        f"got {len(geometry_bearing)}"
    )

    cert_collector.record(
        "CERT-SCHM-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(geometry_bearing),
        notes=(
            f"All {len(geometry_bearing)} geometry-bearing items reported GeoJSON Point; "
            "the one seeded null-geometry row kept an explicit JSON null."
        ),
    )


# ===========================================================================
# CERT-QFLT — query filtering
# ===========================================================================

@pytest.mark.cert("CERT-QFLT-01")
def test_cert_qflt_01_cql2_property_filter(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A CQL2 property filter returns the expected strict subset."""
    _require_stac_api_classes(
        cert_collector,
        "CERT-QFLT-01",
        landing_page,
        [cert_lane.CONFORMANCE_ITEM_SEARCH, cert_lane.CONFORMANCE_FILTER],
    )
    started = time.perf_counter()

    search = api_client.search(
        collections=[COLLECTION_ID],
        filter={
            "op": "=",
            "args": [{"property": canonical_fixture.FILTER_FIELD}, canonical_fixture.FILTER_VALUE],
        },
        filter_lang="cql2-json",
        limit=100,
    )
    items = list(search.items())
    statuses = {item.properties.get(canonical_fixture.FILTER_FIELD) for item in items}

    assert statuses == {canonical_fixture.FILTER_VALUE}, (
        f"CQL2 filter leaked non-matching rows: {statuses}"
    )
    assert len(items) == canonical_fixture.ACTIVE_FEATURES
    assert len(items) < canonical_fixture.TOTAL_FEATURES, "filter did not narrow the result set"

    cert_collector.record(
        "CERT-QFLT-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            f"CQL2-JSON {canonical_fixture.FILTER_FIELD}="
            f"{canonical_fixture.FILTER_VALUE!r} returned "
            f"{len(items)}/{canonical_fixture.TOTAL_FEATURES} items."
        ),
    )


@pytest.mark.cert("CERT-QFLT-02")
def test_cert_qflt_02_bbox_filter(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A bbox search returns the canonical subset."""
    _require_stac_api_classes(
        cert_collector, "CERT-QFLT-02", landing_page, [cert_lane.CONFORMANCE_ITEM_SEARCH]
    )
    started = time.perf_counter()

    search = api_client.search(bbox=list(canonical_fixture.SUBSET_BBOX), limit=100)
    items = list(search.items())

    assert len(items) == canonical_fixture.SUBSET_BBOX_FEATURE_COUNT, (
        f"bbox {canonical_fixture.SUBSET_BBOX} returned {_item_ids(items)}"
    )
    west, south, east, north = canonical_fixture.SUBSET_BBOX
    for item in items:
        lon, lat = _coords(item)
        assert west <= lon <= east and south <= lat <= north, (
            f"item {item.id} at ({lon}, {lat}) is outside the requested bbox"
        )

    cert_collector.record(
        "CERT-QFLT-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            f"client.search(bbox={list(canonical_fixture.SUBSET_BBOX)}) returned "
            f"{len(items)} items ({', '.join(_item_ids(items))}), every one inside the envelope."
        ),
    )


# ===========================================================================
# CERT-PAGE — pagination
# ===========================================================================

@pytest.mark.cert("CERT-PAGE-01")
def test_cert_page_01_first_page_size(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The first page honors the requested page size."""
    started = time.perf_counter()

    search = api_client.search(collections=[COLLECTION_ID], limit=canonical_fixture.PAGE_SIZE)
    first_page = next(iter(search.pages()))
    items = list(first_page.items)

    assert len(items) == canonical_fixture.PAGE_SIZE, (
        f"limit={canonical_fixture.PAGE_SIZE} returned {len(items)} items"
    )

    cert_collector.record(
        "CERT-PAGE-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            f"ItemSearch(limit={canonical_fixture.PAGE_SIZE}).pages() first page "
            f"returned ids {', '.join(_item_ids(items))}."
        ),
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_cert_page_02_next_page_is_disjoint(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Following the ``next`` link yields a disjoint page."""
    started = time.perf_counter()

    search = api_client.search(collections=[COLLECTION_ID], limit=canonical_fixture.PAGE_SIZE)
    pages = search.pages()
    first = list(next(iter(pages)).items)
    second = list(next(iter(pages)).items)

    first_ids = set(_item_ids(first))
    second_ids = set(_item_ids(second))

    assert second, "the next link produced an empty second page"
    assert len(second) == canonical_fixture.PAGE_SIZE
    assert not (first_ids & second_ids), (
        f"pages overlap on {sorted(first_ids & second_ids)}"
    )

    cert_collector.record(
        "CERT-PAGE-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(second),
        notes=(
            f"Page 1 ids {sorted(first_ids)} and page 2 ids {sorted(second_ids)} are disjoint; "
            "pystac-client followed the server's next link without repeating an item."
        ),
    )


# ===========================================================================
# CERT-GEOM — geometry fidelity
# ===========================================================================

@pytest.mark.cert("CERT-GEOM-01")
def test_cert_geom_01_anchor_coordinate_fidelity(
    anchor_item: pystac.Item,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The anchor point round-trips within the geographic tolerance."""
    started = time.perf_counter()

    lon, lat = _coords(anchor_item)
    delta_lon = abs(lon - canonical_fixture.ANCHOR_LON)
    delta_lat = abs(lat - canonical_fixture.ANCHOR_LAT)
    delta = max(delta_lon, delta_lat)

    assert delta <= cert_envelope.GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"anchor drifted {delta} deg (lon={lon}, lat={lat}) beyond "
        f"{cert_envelope.GEOGRAPHIC_TOLERANCE_DEGREES}"
    )

    cert_collector.record(
        "CERT-GEOM-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_delta=delta,
        notes=(
            f"{canonical_fixture.ANCHOR_NAME} returned ({lon}, {lat}) against seeded "
            f"({canonical_fixture.ANCHOR_LON}, {canonical_fixture.ANCHOR_LAT}); max abs "
            f"deviation {delta} <= {cert_envelope.GEOGRAPHIC_TOLERANCE_DEGREES}."
        ),
    )


@pytest.mark.cert("CERT-GEOM-02")
def test_cert_geom_02_crs84_and_extent_consistency(
    api_client: Client,
    all_items: Sequence[pystac.Item],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Coordinates are CRS84 and the collection extent agrees with the items."""
    started = time.perf_counter()

    collection = api_client.get_collection(COLLECTION_ID)
    extent_bbox = collection.extent.spatial.bboxes[0]
    assert len(extent_bbox) in (4, 6), f"malformed spatial extent {extent_bbox}"
    west, south, east, north = extent_bbox[0], extent_bbox[1], extent_bbox[2], extent_bbox[3]

    assert -180.0 <= west <= 180.0 and -180.0 <= east <= 180.0
    assert -90.0 <= south <= 90.0 and -90.0 <= north <= 90.0
    assert west <= east and south <= north, f"extent is inverted: {extent_bbox}"

    checked = 0
    for item in all_items:
        if not isinstance(item.geometry, dict):
            continue
        lon, lat = _coords(item)
        # CRS84 axis order is lon, lat. The fixture sits near -122.4 lon / 37.7 lat,
        # so a lat/lon swap would place the first ordinate outside latitude range.
        assert -180.0 <= lon <= 180.0 and -90.0 <= lat <= 90.0
        assert lon < -90.0 < lat, (
            f"item {item.id} coordinates ({lon}, {lat}) are not in CRS84 lon/lat order"
        )
        assert west <= lon <= east and south <= lat <= north, (
            f"item {item.id} at ({lon}, {lat}) falls outside the declared collection extent"
        )
        checked += 1

    assert checked == canonical_fixture.FEATURES_WITH_GEOMETRY

    cert_collector.record(
        "CERT-GEOM-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=checked,
        notes=(
            f"All {checked} geometry-bearing items are CRS84 lon/lat and fall inside the "
            f"collection's declared extent {extent_bbox}; STAC mandates CRS84 for "
            "item geometry and collection extent alike."
        ),
    )


# ===========================================================================
# CERT-ERRH — error handling
# ===========================================================================

@pytest.mark.cert("CERT-ERRH-01")
def test_cert_errh_01_unknown_collection(
    api_client: Client,
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """An unknown collection surfaces as a structured pystac-client error."""
    started = time.perf_counter()

    with pytest.raises(APIError) as raised:
        api_client.get_collection(canonical_fixture.UNKNOWN_COLLECTION_ID)

    error = raised.value
    status = getattr(error, "status_code", None)
    assert status == 404, f"unknown collection produced status {status!r}, expected 404"
    assert str(error), "APIError carried no message body"

    # The RFC 7807 body is the substantive evidence and APIError only carries the
    # raw text, so it is re-read directly to assert its shape.
    response = cert_lane.get_json(
        f"{base_url.rstrip('/')}/stac/collections/{canonical_fixture.UNKNOWN_COLLECTION_ID}"
    )
    body = response.json()
    assert body.get("status") == 404
    assert canonical_fixture.UNKNOWN_COLLECTION_ID in str(body.get("detail", ""))

    cert_collector.record(
        "CERT-ERRH-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"Client.get_collection({canonical_fixture.UNKNOWN_COLLECTION_ID!r}) raised "
            f"pystac_client.exceptions.APIError with status_code=404; "
            f"body {cert_lane.problem_summary(response)}."
        ),
    )


@pytest.mark.cert("CERT-ERRH-02")
def test_cert_errh_02_malformed_filter(
    api_client: Client,
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A malformed search filter is rejected with a structured error."""
    started = time.perf_counter()

    search = api_client.search(
        collections=[COLLECTION_ID],
        filter=canonical_fixture.MALFORMED_CQL2_FILTER,
        filter_lang="cql2-text",
        limit=10,
    )
    with pytest.raises(APIError) as raised:
        list(search.items())

    status = getattr(raised.value, "status_code", None)
    assert status == 400, f"malformed CQL2 produced status {status!r}, expected 400"

    response = cert_lane.post_json(
        f"{base_url.rstrip('/')}/stac/search",
        {
            "collections": [COLLECTION_ID],
            "filter": canonical_fixture.MALFORMED_CQL2_FILTER,
            "filter-lang": "cql2-text",
            "limit": 10,
        },
    )
    assert response.status_code == 400
    body = response.json()
    assert body.get("status") == 400
    assert body.get("detail"), "400 problem document carried no detail"

    cert_collector.record(
        "CERT-ERRH-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"POST /stac/search with filter={canonical_fixture.MALFORMED_CQL2_FILTER!r} "
            f"(cql2-text) raised APIError(status_code=400); body "
            f"{cert_lane.problem_summary(response)}."
        ),
    )


# ===========================================================================
# NB-STAC-CONF — every declared conformance class must actually be honored
# ===========================================================================
#
# A conformance class the landing page declares but the server does not
# implement is a first-class server bug: spec-first clients branch on
# ``conformsTo``, so a false declaration makes them take a code path that then
# fails. Each declared class gets its own extension ID so the envelope names
# exactly which ones hold.

@pytest.mark.cert("NB-STAC-CONF-01")
def test_nb_conf_01_core(
    landing_page: dict[str, Any],
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``core``: landing-page links plus a matching /stac/conformance document."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_CORE)

    rels = {link.get("rel") for link in landing_page.get("links", [])}
    for required in ("self", "root", "data", "search", "conformance", "service-desc"):
        assert required in rels, f"landing page omits the required {required!r} link"

    conformance = cert_lane.get_json(f"{base_url.rstrip('/')}/stac/conformance")
    assert conformance.status_code == 200
    declared = set(cert_lane.declared_conformance(landing_page))
    published = set(conformance.json().get("conformsTo", []))
    assert declared == published, (
        "landing page and /stac/conformance disagree: "
        f"only-on-landing={sorted(declared - published)} "
        f"only-on-conformance={sorted(published - declared)}"
    )

    cert_collector.record(
        "NB-STAC-CONF-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(declared),
        notes=(
            f"core honored: landing page carries {sorted(rels)} and /stac/conformance "
            f"publishes the same {len(declared)} classes."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-02")
def test_nb_conf_02_collections(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``collections``: every listed collection is individually fetchable by id."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_COLLECTIONS)

    listed = list(api_client.get_collections())
    assert listed, "/stac/collections returned nothing"

    for collection in listed:
        fetched = api_client.get_collection(collection.id)
        assert fetched.id == collection.id
        assert fetched.description, f"collection {collection.id} has no description"

    cert_collector.record(
        "NB-STAC-CONF-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(listed),
        notes=(
            f"collections honored: all {len(listed)} listed collections "
            f"({', '.join(sorted(c.id for c in listed))}) round-tripped through "
            "get_collection(id)."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-03")
def test_nb_conf_03_ogcapi_features(
    landing_page: dict[str, Any],
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``ogcapi-features``: the items resource honors ``limit`` and ``bbox``."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_FEATURES)
    for uri in (
        cert_lane.OGC_FEATURES_CORE,
        cert_lane.OGC_FEATURES_OAS30,
        cert_lane.OGC_FEATURES_GEOJSON,
    ):
        assert cert_lane.declares_uri(landing_page, uri), f"missing OGC API Features class {uri}"

    items_url = f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}/items"
    limited = cert_lane.get_json(items_url, params={"limit": 2})
    assert limited.status_code == 200
    limited_body = limited.json()
    assert limited_body["type"] == "FeatureCollection"
    assert len(limited_body["features"]) == 2

    west, south, east, north = canonical_fixture.SUBSET_BBOX
    bboxed = cert_lane.get_json(
        items_url, params={"limit": 100, "bbox": f"{west},{south},{east},{north}"}
    )
    assert bboxed.status_code == 200
    bboxed_ids = {feature["id"] for feature in bboxed.json()["features"]}
    assert bboxed_ids == SUBSET_ITEM_IDS, f"items?bbox returned {sorted(bboxed_ids)}"

    # Every feature must rehydrate under plain pystac, which is what a STAC
    # consumer of the OGC API Features surface actually does with the payload.
    for feature in limited_body["features"]:
        pystac.Item.from_dict(dict(feature))

    cert_collector.record(
        "NB-STAC-CONF-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(bboxed_ids),
        notes=(
            "ogcapi-features honored: /items honored limit and bbox and every returned "
            "feature rehydrated under pystac.Item.from_dict."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-04")
def test_nb_conf_04_item_search(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``item-search``: both the GET and POST search endpoints answer."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_ITEM_SEARCH)
    assert api_client.conforms_to(ConformanceClasses.ITEM_SEARCH)

    search_links = [
        link for link in landing_page.get("links", []) if link.get("rel") == "search"
    ]
    assert search_links, "landing page advertises no search link"
    methods = {(link.get("method") or "GET").upper() for link in search_links}
    assert {"GET", "POST"} <= methods, f"search link methods are {methods}"

    post_ids = _item_ids(
        api_client.search(collections=[COLLECTION_ID], limit=100, method="POST").items()
    )
    get_ids = _item_ids(
        api_client.search(collections=[COLLECTION_ID], limit=100, method="GET").items()
    )
    assert post_ids and get_ids

    cert_collector.record(
        "NB-STAC-CONF-04",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(post_ids),
        notes=(
            f"item-search honored: landing page advertises {sorted(methods)} search links "
            f"and both methods returned {len(post_ids)} items."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-05")
def test_nb_conf_05_fields_extension(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``item-search#fields``: include and exclude both reshape properties."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_FIELDS)
    assert api_client.conforms_to(ConformanceClasses.FIELDS)

    temporal = {"datetime", "start_datetime", "end_datetime"}

    included = list(
        api_client.search(
            collections=[COLLECTION_ID],
            limit=3,
            fields=["properties.name", "properties.status"],
        ).items_as_dicts()
    )
    assert included
    for feature in included:
        names = set(feature["properties"]) - temporal
        assert names == {"name", "status"}, f"fields include leaked {sorted(names)}"

    excluded = list(
        api_client.search(
            collections=[COLLECTION_ID], limit=3, fields=["-properties.tags"]
        ).items_as_dicts()
    )
    assert excluded
    for feature in excluded:
        assert "tags" not in feature["properties"], "fields exclude did not drop properties.tags"
        assert "name" in feature["properties"], "fields exclude dropped unrelated properties"

    cert_collector.record(
        "NB-STAC-CONF-05",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(included),
        notes=(
            "item-search#fields honored: include narrowed properties to the requested set "
            "and exclude removed only properties.tags."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-06")
def test_nb_conf_06_sort_extension(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``item-search#sort``: ascending and descending sorts are honored."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_SORT)
    assert api_client.conforms_to(ConformanceClasses.SORT)

    ascending = [
        item.properties["name"]
        for item in api_client.search(
            collections=[COLLECTION_ID], limit=100, sortby="+properties.name"
        ).items()
    ]
    descending = [
        item.properties["name"]
        for item in api_client.search(
            collections=[COLLECTION_ID], limit=100, sortby="-properties.name"
        ).items()
    ]

    assert ascending == sorted(ascending), f"ascending sort returned {ascending}"
    assert descending == sorted(descending, reverse=True), f"descending sort returned {descending}"
    assert ascending == list(reversed(descending))

    cert_collector.record(
        "NB-STAC-CONF-06",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(ascending),
        notes=(
            "item-search#sort honored: +properties.name and -properties.name produced "
            f"exactly reversed orderings over {len(ascending)} items."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-07")
def test_nb_conf_07_filter_extension(
    api_client: Client,
    landing_page: dict[str, Any],
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``item-search#filter``: queryables are published and CQL2 narrows results."""
    started = time.perf_counter()
    assert cert_lane.declares_stac_api_class(landing_page, cert_lane.CONFORMANCE_FILTER)
    assert api_client.conforms_to(ConformanceClasses.FILTER)
    assert cert_lane.declares_uri(landing_page, cert_lane.OGC_FEATURES_FILTER)

    queryables_rel = "http://www.opengis.net/def/rel/ogc/1.0/queryables"
    queryables_links = [
        link for link in landing_page.get("links", []) if link.get("rel") == queryables_rel
    ]
    assert queryables_links, "filter class declared without a queryables link"

    queryables = cert_lane.get_json(
        f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}/queryables"
    )
    assert queryables.status_code == 200
    schema = queryables.json()
    assert schema.get("$schema"), "queryables document declares no JSON Schema dialect"
    properties = schema.get("properties", {})
    assert canonical_fixture.FILTER_FIELD in properties
    assert CLOUD_COVER_PROPERTY in properties, (
        "queryables omit the seeded eo:cloud_cover numeric property"
    )

    filtered = list(
        api_client.search(
            collections=[COLLECTION_ID],
            filter={"op": "<", "args": [{"property": CLOUD_COVER_PROPERTY}, 10]},
            filter_lang="cql2-json",
            limit=100,
        ).items()
    )
    assert filtered, "eo:cloud_cover < 10 matched nothing"
    assert len(filtered) < canonical_fixture.TOTAL_FEATURES

    cert_collector.record(
        "NB-STAC-CONF-07",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(filtered),
        notes=(
            f"item-search#filter honored: queryables publish {len(properties)} properties "
            f"(dialect {schema.get('$schema')}) and a CQL2-JSON comparison on "
            f"{CLOUD_COVER_PROPERTY} narrowed to {len(filtered)} items."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-08")
def test_nb_conf_08_service_desc_and_doc(
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``ogcapi-features-1/oas30``: the advertised API definition is reachable."""
    started = time.perf_counter()
    assert cert_lane.declares_uri(landing_page, cert_lane.OGC_FEATURES_OAS30)

    by_rel = {link.get("rel"): link for link in landing_page.get("links", [])}
    service_desc = by_rel.get("service-desc")
    assert service_desc, "oas30 declared without a service-desc link"
    assert "openapi" in (service_desc.get("type") or "").lower()

    response = cert_lane.get_json(service_desc["href"])
    assert response.status_code == 200, (
        f"service-desc {service_desc['href']} returned {response.status_code}"
    )
    document = response.json()
    assert document.get("openapi"), "service-desc is not an OpenAPI document"
    assert document.get("paths"), "OpenAPI document declares no paths"

    service_doc = by_rel.get("service-doc")
    assert service_doc and service_doc.get("href"), "landing page omits the service-doc link"

    cert_collector.record(
        "NB-STAC-CONF-08",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(document.get("paths", {})),
        notes=(
            f"oas30 honored: service-desc served OpenAPI {document.get('openapi')} with "
            f"{len(document.get('paths', {}))} paths; service-doc link present."
        ),
    )


@pytest.mark.cert("NB-STAC-CONF-09")
def test_nb_conf_09_cql2_dialects(
    api_client: Client,
    landing_page: dict[str, Any],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``basic-cql2`` in both dialects: cql2-json and cql2-text agree."""
    started = time.perf_counter()
    for uri in (cert_lane.CQL2_BASIC, cert_lane.CQL2_JSON, cert_lane.CQL2_TEXT):
        assert cert_lane.declares_uri(landing_page, uri), f"missing CQL2 conformance class {uri}"

    json_ids = set(
        _item_ids(
            api_client.search(
                collections=[COLLECTION_ID],
                filter={
                    "op": "and",
                    "args": [
                        {"op": "=", "args": [{"property": "status"}, "active"]},
                        {"op": ">", "args": [{"property": "count"}, 3]},
                    ],
                },
                filter_lang="cql2-json",
                limit=100,
            ).items()
        )
    )
    text_ids = set(
        _item_ids(
            api_client.search(
                collections=[COLLECTION_ID],
                filter="status = 'active' AND count > 3",
                filter_lang="cql2-text",
                limit=100,
            ).items()
        )
    )

    assert json_ids, "CQL2-JSON conjunction matched nothing"
    assert json_ids == text_ids, (
        f"cql2-json returned {sorted(json_ids)} but cql2-text returned {sorted(text_ids)}"
    )

    cert_collector.record(
        "NB-STAC-CONF-09",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(json_ids),
        notes=(
            "basic-cql2 honored in both dialects: an AND of an equality and a numeric "
            f"comparison returned the same {len(json_ids)} items via cql2-json and cql2-text."
        ),
    )


# ===========================================================================
# NB-STAC-SEARCH — the Item Search parameter surface
# ===========================================================================

@pytest.mark.cert("NB-STAC-SEARCH-01")
def test_nb_search_01_intersects_polygon(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``intersects`` with a Polygon matches the equivalent bbox subset."""
    started = time.perf_counter()
    west, south, east, north = canonical_fixture.SUBSET_BBOX
    polygon = {
        "type": "Polygon",
        "coordinates": [[
            [west, south], [east, south], [east, north], [west, north], [west, south],
        ]],
    }

    intersects_ids = set(_item_ids(api_client.search(intersects=polygon, limit=100).items()))
    bbox_ids = set(
        _item_ids(api_client.search(bbox=list(canonical_fixture.SUBSET_BBOX), limit=100).items())
    )

    assert intersects_ids == SUBSET_ITEM_IDS, f"intersects returned {sorted(intersects_ids)}"
    assert intersects_ids == bbox_ids, (
        f"intersects {sorted(intersects_ids)} disagrees with bbox {sorted(bbox_ids)}"
    )

    cert_collector.record(
        "NB-STAC-SEARCH-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(intersects_ids),
        notes=(
            "intersects(Polygon) and the equivalent bbox both returned "
            f"{sorted(intersects_ids)}."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-02")
def test_nb_search_02_intersects_point(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``intersects`` with a Point matches exactly the item at that coordinate."""
    started = time.perf_counter()
    point = {
        "type": "Point",
        "coordinates": [canonical_fixture.ANCHOR_LON, canonical_fixture.ANCHOR_LAT],
    }

    items = list(api_client.search(intersects=point, limit=100).items())
    ids = _item_ids(items)

    assert ids == [ANCHOR_ITEM_ID], f"point intersects returned {ids}"
    lon, lat = _coords(items[0])
    assert math.isclose(lon, canonical_fixture.ANCHOR_LON, abs_tol=1e-9)
    assert math.isclose(lat, canonical_fixture.ANCHOR_LAT, abs_tol=1e-9)

    cert_collector.record(
        "NB-STAC-SEARCH-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(ids),
        notes=(
            "intersects(Point) at the anchor coordinate matched exactly the anchor item; "
            "a degenerate point geometry is the classic spatial-predicate edge case."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-03")
def test_nb_search_03_datetime_instant(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``datetime`` as an RFC 3339 instant selects the single matching item."""
    started = time.perf_counter()
    items = list(
        api_client.search(
            collections=[COLLECTION_ID], datetime="2024-01-03T12:00:00Z", limit=100
        ).items()
    )

    assert len(items) == 1, f"datetime instant returned {_item_ids(items)}"
    assert items[0].properties["name"] == "gamma"

    cert_collector.record(
        "NB-STAC-SEARCH-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            "datetime=2024-01-03T12:00:00Z matched exactly the one seeded item at that instant."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-04")
def test_nb_search_04_datetime_closed_interval(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``datetime`` as a closed interval selects an inclusive range."""
    started = time.perf_counter()
    items = list(
        api_client.search(
            collections=[COLLECTION_ID],
            datetime="2024-01-01T00:00:00Z/2024-01-03T23:59:59Z",
            limit=100,
        ).items()
    )
    names = [item.properties["name"] for item in items]

    assert sorted(names) == ["alpha", "beta", "gamma"], f"closed interval returned {names}"

    cert_collector.record(
        "NB-STAC-SEARCH-04",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            "A closed RFC 3339 interval selected the three items inside it, with both "
            "endpoints treated inclusively."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-05")
def test_nb_search_05_datetime_open_intervals(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``datetime`` open-ended in both directions partitions the collection."""
    started = time.perf_counter()

    open_start = set(
        _item_ids(
            api_client.search(
                collections=[COLLECTION_ID], datetime="../2024-01-03T23:59:59Z", limit=100
            ).items()
        )
    )
    open_end = set(
        _item_ids(
            api_client.search(
                collections=[COLLECTION_ID], datetime="2024-01-08T00:00:00Z/..", limit=100
            ).items()
        )
    )

    assert open_start == SUBSET_ITEM_IDS, f"../end returned {sorted(open_start)}"
    assert open_end == {"8", "9", "10"}, f"start/.. returned {sorted(open_end)}"
    assert not (open_start & open_end), "open-ended intervals overlap"

    cert_collector.record(
        "NB-STAC-SEARCH-05",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(open_start) + len(open_end),
        notes=(
            f"Open-start ../T returned {sorted(open_start)} and open-end T/.. returned "
            f"{sorted(open_end)}; the two halves are disjoint."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-06")
def test_nb_search_06_ids_parameter(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``ids`` selects exactly the requested items; an unknown id yields nothing."""
    started = time.perf_counter()

    requested = ["2", "4"]
    items = list(api_client.search(collections=[COLLECTION_ID], ids=requested, limit=100).items())
    assert sorted(_item_ids(items)) == sorted(requested), f"ids returned {_item_ids(items)}"

    missing = list(
        api_client.search(
            collections=[COLLECTION_ID], ids=["no-such-item-9999"], limit=100
        ).items()
    )
    assert missing == [], f"unknown id matched {_item_ids(missing)}"

    cert_collector.record(
        "NB-STAC-SEARCH-06",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            f"ids={requested} returned exactly those items; an unknown id returned an "
            "empty FeatureCollection rather than an error or the whole collection."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-07")
def test_nb_search_07_collections_scoping(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``collections`` scopes the search and never leaks another collection's items."""
    started = time.perf_counter()

    scoped = list(api_client.search(collections=[COLLECTION_ID], limit=100).items())
    assert scoped
    assert {item.collection_id for item in scoped} == {COLLECTION_ID}

    unscoped = list(api_client.search(limit=200).items())
    unscoped_collections = {item.collection_id for item in unscoped}
    assert COLLECTION_ID in unscoped_collections
    assert len(unscoped) >= len(scoped)

    unknown = list(
        api_client.search(
            collections=[canonical_fixture.UNKNOWN_COLLECTION_ID], limit=100
        ).items()
    )
    assert unknown == [], f"unknown collection filter matched {_item_ids(unknown)}"

    cert_collector.record(
        "NB-STAC-SEARCH-07",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(scoped),
        notes=(
            f"collections=[{COLLECTION_ID!r}] returned {len(scoped)} items all bearing that "
            f"collection back-reference; the unscoped search spanned "
            f"{sorted(unscoped_collections)}; an unknown collection matched nothing."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-08")
def test_nb_search_08_get_post_parity(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The same search over GET and POST returns identical results."""
    started = time.perf_counter()
    west, south, east, north = canonical_fixture.SUBSET_BBOX

    def run(method: str) -> list[str]:
        return _item_ids(
            api_client.search(
                collections=[COLLECTION_ID],
                bbox=[west, south, east, north],
                datetime="2024-01-01T00:00:00Z/2024-01-31T00:00:00Z",
                sortby="+properties.name",
                limit=100,
                method=method,
            ).items()
        )

    get_ids = run("GET")
    post_ids = run("POST")

    assert get_ids, "GET search returned nothing"
    assert get_ids == post_ids, (
        f"GET/POST divergence: GET returned {get_ids}, POST returned {post_ids}"
    )

    cert_collector.record(
        "NB-STAC-SEARCH-08",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(get_ids),
        notes=(
            "An identical bbox + datetime + sortby search returned the same ordered ids "
            f"({get_ids}) over both GET and POST."
        ),
    )


@pytest.mark.cert("NB-STAC-SEARCH-09")
def test_nb_search_09_max_items_and_limit(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``max_items`` truncates client-side while ``limit`` sets the server page size."""
    started = time.perf_counter()

    search = api_client.search(
        collections=[COLLECTION_ID], limit=canonical_fixture.PAGE_SIZE, max_items=4
    )
    items = list(search.items())

    assert len(items) == 4, f"max_items=4 returned {len(items)} items"
    assert len(set(_item_ids(items))) == 4, "max_items truncation repeated an item"

    cert_collector.record(
        "NB-STAC-SEARCH-09",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(items),
        notes=(
            f"limit={canonical_fixture.PAGE_SIZE} with max_items=4 crossed a page boundary "
            "and yielded four distinct items."
        ),
    )


# ===========================================================================
# NB-STAC-PAGE — pagination
# ===========================================================================

@pytest.mark.cert("NB-STAC-PAGE-01")
def test_nb_page_01_full_walk(
    api_client: Client,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Walking ``pages()`` to exhaustion yields every item exactly once."""
    started = time.perf_counter()

    search = api_client.search(collections=[COLLECTION_ID], limit=canonical_fixture.PAGE_SIZE)
    seen: list[str] = []
    page_sizes: list[int] = []
    for page in search.pages():
        ids = _item_ids(page.items)
        page_sizes.append(len(ids))
        seen.extend(ids)

    assert len(seen) == canonical_fixture.TOTAL_FEATURES, (
        f"walk collected {len(seen)} items across pages {page_sizes}"
    )
    assert len(set(seen)) == len(seen), "an item appeared on more than one page"
    assert all(size <= canonical_fixture.PAGE_SIZE for size in page_sizes)

    cert_collector.record(
        "NB-STAC-PAGE-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(seen),
        notes=(
            f"pages() walked to exhaustion in page sizes {page_sizes}, collecting all "
            f"{len(seen)} seeded items with no duplicates and terminating without a next link."
        ),
    )


@pytest.mark.cert("NB-STAC-PAGE-02")
def test_nb_page_02_matched_consistency(
    api_client: Client,
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``matched()``, ``numberMatched`` and ``numberReturned`` agree with reality."""
    started = time.perf_counter()

    search = api_client.search(collections=[COLLECTION_ID], limit=canonical_fixture.PAGE_SIZE)
    matched = search.matched()
    assert matched == canonical_fixture.TOTAL_FEATURES, (
        f"ItemSearch.matched() reported {matched}"
    )

    response = cert_lane.post_json(
        f"{base_url.rstrip('/')}/stac/search",
        {"collections": [COLLECTION_ID], "limit": canonical_fixture.PAGE_SIZE},
    )
    body = response.json()
    assert body.get("numberMatched") == canonical_fixture.TOTAL_FEATURES
    assert body.get("numberReturned") == canonical_fixture.PAGE_SIZE
    assert body["numberReturned"] == len(body["features"])

    cert_collector.record(
        "NB-STAC-PAGE-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=matched,
        notes=(
            f"ItemSearch.matched()={matched} agrees with numberMatched="
            f"{body.get('numberMatched')} and numberReturned={body.get('numberReturned')} "
            "equals the actual feature count on the page."
        ),
    )


@pytest.mark.cert("NB-STAC-PAGE-03")
def test_nb_page_03_next_link_shape(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The ``next`` link is well-formed for both GET and POST search."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    get_body = cert_lane.get_json(
        search_url, params={"collections": COLLECTION_ID, "limit": canonical_fixture.PAGE_SIZE}
    ).json()
    get_next = next(link for link in get_body["links"] if link["rel"] == "next")
    assert get_next["href"].startswith(search_url), f"GET next href is {get_next['href']!r}"
    assert "token=" in get_next["href"], "GET next link carries no pagination token"

    post_body = cert_lane.post_json(
        search_url, {"collections": [COLLECTION_ID], "limit": canonical_fixture.PAGE_SIZE}
    ).json()
    post_next = next(link for link in post_body["links"] if link["rel"] == "next")
    assert (post_next.get("method") or "").upper() == "POST", (
        "POST search next link is not a POST link"
    )
    assert post_next.get("merge") is True, "POST next link is not marked merge:true"
    assert isinstance(post_next.get("body"), dict) and post_next["body"].get("token"), (
        f"POST next link carries no body token: {post_next}"
    )

    followed = cert_lane.post_json(
        post_next["href"],
        {
            "collections": [COLLECTION_ID],
            "limit": canonical_fixture.PAGE_SIZE,
            **post_next["body"],
        },
    ).json()
    first_ids = {feature["id"] for feature in post_body["features"]}
    next_ids = {feature["id"] for feature in followed["features"]}
    assert next_ids and not (first_ids & next_ids), (
        f"following the POST next link re-served {sorted(first_ids & next_ids)}"
    )

    cert_collector.record(
        "NB-STAC-PAGE-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(next_ids),
        notes=(
            "GET next uses a token query parameter; POST next is a body-bearing "
            "method=POST merge=true link whose token advanced the cursor to a disjoint page."
        ),
    )


@pytest.mark.cert("NB-STAC-PAGE-04")
def test_nb_page_04_limit_bounds(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """``limit`` above the server maximum is clamped; ``limit=0`` is rejected."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    oversized = cert_lane.get_json(
        search_url, params={"collections": COLLECTION_ID, "limit": 1_000_000}
    )
    assert oversized.status_code == 200, (
        f"an oversized limit must clamp, not fail: {cert_lane.problem_summary(oversized)}"
    )
    assert len(oversized.json()["features"]) == canonical_fixture.TOTAL_FEATURES

    zero = cert_lane.get_json(search_url, params={"collections": COLLECTION_ID, "limit": 0})
    assert zero.status_code == 400, f"limit=0 returned {zero.status_code}"
    assert zero.json().get("status") == 400

    cert_collector.record(
        "NB-STAC-PAGE-04",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            "limit=1000000 clamped to the server maximum and still answered 200; "
            f"limit=0 was rejected with {cert_lane.problem_summary(zero)}. "
            "Raw httpx used because pystac-client refuses out-of-range limits client-side."
        ),
    )


@pytest.mark.cert("NB-STAC-PAGE-05")
def test_nb_page_05_token_past_end(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A token past the end returns an empty page; a bogus token is rejected."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    past_end = cert_lane.get_json(
        search_url,
        params={"collections": COLLECTION_ID, "limit": 3, "token": "offset:1000"},
    )
    assert past_end.status_code == 200, (
        f"a token past the end must not error: {cert_lane.problem_summary(past_end)}"
    )
    body = past_end.json()
    assert body["features"] == [], f"token past the end returned {len(body['features'])} features"
    assert not [link for link in body["links"] if link["rel"] == "next"], (
        "an exhausted page still advertised a next link"
    )

    bogus = cert_lane.get_json(
        search_url, params={"collections": COLLECTION_ID, "limit": 3, "token": "not-a-token"}
    )
    assert bogus.status_code == 400, f"bogus token returned {bogus.status_code}"
    assert bogus.json().get("status") == 400

    cert_collector.record(
        "NB-STAC-PAGE-05",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            "A token past the end returned an empty FeatureCollection with no next link; "
            f"a malformed token was rejected with {cert_lane.problem_summary(bogus)}."
        ),
    )


# ===========================================================================
# NB-STAC-ITEM — item and asset content
# ===========================================================================

@pytest.mark.cert("NB-STAC-ITEM-01")
def test_nb_item_01_assets(
    anchor_item: pystac.Item,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Item assets declare href/type/roles and the href actually resolves."""
    started = time.perf_counter()
    assets = anchor_item.assets
    assert assets, f"item {anchor_item.id} declares no assets"

    for key, asset in assets.items():
        assert asset.href, f"asset {key!r} has no href"
        assert asset.media_type, f"asset {key!r} declares no type"
        assert asset.roles, f"asset {key!r} declares no roles"

        response = cert_lane.get_json(asset.href, headers={"Accept": asset.media_type})
        assert response.status_code == 200, (
            f"asset {key!r} href {asset.href} returned {response.status_code}"
        )
        media_type = response.headers.get("content-type", "")
        assert asset.media_type.split(";")[0] in media_type, (
            f"asset {key!r} declared {asset.media_type} but served {media_type}"
        )

    cert_collector.record(
        "NB-STAC-ITEM-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(assets),
        notes=(
            f"All {len(assets)} assets on item {anchor_item.id} "
            f"({', '.join(sorted(assets))}) carried href/type/roles and resolved with a "
            "matching content type."
        ),
    )


@pytest.mark.cert("NB-STAC-ITEM-02")
def test_nb_item_02_bbox_matches_geometry(
    all_items: Sequence[pystac.Item],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Every item's ``bbox`` agrees with its ``geometry``; null geometry has no bbox."""
    started = time.perf_counter()
    checked = 0
    null_geometry = 0

    for item in all_items:
        if not isinstance(item.geometry, dict):
            null_geometry += 1
            assert item.bbox in (None, []), (
                f"item {item.id} has null geometry but declares bbox {item.bbox}"
            )
            continue
        lon, lat = _coords(item)
        assert item.bbox, f"item {item.id} has geometry but no bbox"
        west, south, east, north = item.bbox[0], item.bbox[1], item.bbox[2], item.bbox[3]
        assert west <= lon <= east and south <= lat <= north, (
            f"item {item.id} bbox {item.bbox} does not contain its geometry ({lon}, {lat})"
        )
        checked += 1

    assert checked == canonical_fixture.FEATURES_WITH_GEOMETRY
    expected_null = canonical_fixture.TOTAL_FEATURES - canonical_fixture.FEATURES_WITH_GEOMETRY
    assert null_geometry == expected_null

    cert_collector.record(
        "NB-STAC-ITEM-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=checked,
        notes=(
            f"{checked} items had a bbox containing their geometry; the {null_geometry} "
            "null-geometry item correctly omitted bbox."
        ),
    )


@pytest.mark.cert("NB-STAC-ITEM-03")
def test_nb_item_03_links_and_back_reference(
    anchor_item: pystac.Item,
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Item links (self/collection/parent/root) and the collection back-reference resolve."""
    started = time.perf_counter()

    assert anchor_item.collection_id == COLLECTION_ID, (
        f"item collection back-reference is {anchor_item.collection_id!r}"
    )

    by_rel = {link.rel: link for link in anchor_item.links}
    for rel in ("self", "collection", "parent", "root"):
        assert rel in by_rel, f"item omits the {rel!r} link"

    self_href = by_rel["self"].get_href(transform_href=False)
    assert self_href.startswith(f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}/items/")

    response = cert_lane.get_json(self_href)
    assert response.status_code == 200, f"item self link returned {response.status_code}"
    fetched = pystac.Item.from_dict(dict(response.json()))
    assert fetched.id == anchor_item.id
    assert fetched.collection_id == COLLECTION_ID

    collection_response = cert_lane.get_json(
        by_rel["collection"].get_href(transform_href=False)
    )
    assert collection_response.status_code == 200
    assert collection_response.json()["id"] == COLLECTION_ID

    cert_collector.record(
        "NB-STAC-ITEM-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(by_rel),
        notes=(
            f"Item {anchor_item.id} carried {sorted(by_rel)} links; the self link re-fetched "
            "the same item and the collection link resolved to the owning collection."
        ),
    )


@pytest.mark.cert("NB-STAC-ITEM-04")
def test_nb_item_04_datetime_is_rfc3339_utc(
    all_items: Sequence[pystac.Item],
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Every item carries a parseable UTC ``datetime`` (or both interval bounds)."""
    started = time.perf_counter()

    response = cert_lane.get_json(
        f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}/items",
        params={"limit": 100},
    )
    assert response.status_code == 200
    raw_features = response.json()["features"]
    assert len(raw_features) == canonical_fixture.TOTAL_FEATURES

    for feature in raw_features:
        properties = feature["properties"]
        assert "datetime" in properties, f"item {feature['id']} omits datetime"
        if properties["datetime"] is None:
            assert properties.get("start_datetime") and properties.get("end_datetime"), (
                f"item {feature['id']} has datetime:null without both interval bounds"
            )

    for item in all_items:
        assert item.datetime is not None, f"pystac could not parse datetime on item {item.id}"
        assert item.datetime.tzinfo is not None, f"item {item.id} datetime is naive"
        assert item.datetime.utcoffset().total_seconds() == 0, (
            f"item {item.id} datetime is not UTC: {item.properties['datetime']}"
        )

    cert_collector.record(
        "NB-STAC-ITEM-04",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(all_items),
        notes=(
            f"All {len(all_items)} items carried an RFC 3339 UTC datetime that pystac parsed "
            "into a timezone-aware value with zero offset."
        ),
    )


@pytest.mark.cert("NB-STAC-ITEM-05")
def test_nb_item_05_stac_extensions_declaration(
    all_items: Sequence[pystac.Item],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Declared ``stac_extensions`` are well-formed and consistent with properties."""
    started = time.perf_counter()

    declared: set[str] = set()
    for item in all_items:
        extensions = item.stac_extensions or []
        assert isinstance(extensions, list)
        for uri in extensions:
            assert isinstance(uri, str) and uri.startswith("http"), (
                f"item {item.id} declares a non-URI stac_extension {uri!r}"
            )
        declared.update(extensions)

    # eo:cloud_cover is an EO-extension property. If the server declares the EO
    # extension it must be present on the items; if it does not declare it, the
    # property is a legal unprefixed extra but the asymmetry is worth naming in
    # the evidence rather than passing silently.
    eo_declared = any("/eo/" in uri for uri in declared)
    cloud_cover_present = any(CLOUD_COVER_PROPERTY in item.properties for item in all_items)

    if eo_declared:
        assert cloud_cover_present, (
            "collection declares the EO extension but no item exposes eo:cloud_cover"
        )

    cert_collector.record(
        "NB-STAC-ITEM-05",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(declared),
        notes=(
            f"stac_extensions declared: {sorted(declared) or 'none'}; eo:cloud_cover present "
            f"on items: {cloud_cover_present}. The fixture's eo:cloud_cover is projected "
            "without the server claiming the EO extension schema, which is legal but "
            "worth naming."
        ),
    )


@pytest.mark.cert("NB-STAC-ITEM-06")
def test_nb_item_06_pystac_object_validation(
    api_client: Client,
    anchor_item: pystac.Item,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Catalog, Collection and Item all pass pystac schema validation when reachable."""
    started = time.perf_counter()

    catalog = pystac.Catalog.from_dict(
        dict(api_client.to_dict(include_self_link=True, transform_hrefs=False))
    )
    collection = pystac.Collection.from_dict(
        dict(api_client.get_collection(COLLECTION_ID).to_dict(transform_hrefs=False))
    )

    results: dict[str, bool] = {}
    notes: list[str] = []
    for label, obj in (("Catalog", catalog), ("Collection", collection), ("Item", anchor_item)):
        validated, note = _validate_with_pystac(obj)
        results[label] = validated
        notes.append(f"{label}: {note}")

    validated_count = sum(1 for value in results.values() if value)
    unreachable = validated_count != len(results)

    cert_collector.record(
        "NB-STAC-ITEM-06",
        "skip" if unreachable else "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=validated_count,
        notes="; ".join(notes),
    )
    if unreachable:
        pytest.skip("published STAC schemas unreachable from the lane: " + "; ".join(notes))


# ===========================================================================
# NB-STAC-COLL — collection metadata
# ===========================================================================

@pytest.mark.cert("NB-STAC-COLL-01")
def test_nb_coll_01_extent_covers_items(
    api_client: Client,
    all_items: Sequence[pystac.Item],
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The declared temporal extent covers every item's datetime."""
    started = time.perf_counter()

    collection = api_client.get_collection(COLLECTION_ID)
    interval = collection.extent.temporal.intervals[0]
    start, end = interval[0], interval[1]
    assert start is not None, "collection declares an unbounded temporal start"

    datetimes = [item.datetime for item in all_items if item.datetime is not None]
    assert datetimes
    assert min(datetimes) >= start, (
        f"earliest item {min(datetimes)} precedes the declared extent start {start}"
    )
    if end is not None:
        assert max(datetimes) <= end, (
            f"latest item {max(datetimes)} exceeds the declared extent end {end}"
        )

    cert_collector.record(
        "NB-STAC-COLL-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(datetimes),
        notes=(
            f"Declared temporal extent [{start}, {end}] covers all {len(datetimes)} item "
            f"datetimes (observed [{min(datetimes)}, {max(datetimes)}])."
        ),
    )


@pytest.mark.cert("NB-STAC-COLL-02")
def test_nb_coll_02_metadata_completeness(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Collection metadata carries the members STAC requires and names optional gaps."""
    started = time.perf_counter()

    response = cert_lane.get_json(f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}")
    assert response.status_code == 200
    document = response.json()

    assert document.get("type") == "Collection"
    assert document.get("stac_version")
    assert document.get("license"), "collection declares no license (required by STAC)"
    assert document.get("description")
    rels = {link["rel"] for link in document.get("links", [])}
    for required in ("self", "root", "items"):
        assert required in rels, f"collection omits the {required!r} link"

    optional_present = sorted(
        key for key in ("summaries", "providers", "keywords", "title") if document.get(key)
    )

    cert_collector.record(
        "NB-STAC-COLL-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(rels),
        notes=(
            f"Required members present (license={document.get('license')!r}, "
            f"stac_version={document.get('stac_version')}, links {sorted(rels)}); "
            f"optional members present: {optional_present or 'none'}."
        ),
    )


@pytest.mark.cert("NB-STAC-COLL-03")
def test_nb_coll_03_collections_document_shape(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The /collections document is well-formed and each entry rehydrates."""
    started = time.perf_counter()

    response = cert_lane.get_json(f"{base_url.rstrip('/')}/stac/collections")
    assert response.status_code == 200
    document = response.json()

    assert isinstance(document.get("collections"), list)
    rels = {link["rel"] for link in document.get("links", [])}
    assert "self" in rels and "root" in rels, f"/collections links are {sorted(rels)}"

    for entry in document["collections"]:
        rehydrated = pystac.Collection.from_dict(dict(entry))
        assert rehydrated.id
        assert rehydrated.extent.spatial.bboxes

    cert_collector.record(
        "NB-STAC-COLL-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(document["collections"]),
        notes=(
            f"/stac/collections returned {len(document['collections'])} entries, each "
            "rehydrating under pystac.Collection with a spatial extent."
        ),
    )


# ===========================================================================
# NB-STAC-ERR — error surface
# ===========================================================================

@pytest.mark.cert("NB-STAC-ERR-01")
def test_nb_err_01_unknown_item(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """An unknown item id is a structured 404, never a 500 or an empty 200."""
    started = time.perf_counter()
    url = f"{base_url.rstrip('/')}/stac/collections/{COLLECTION_ID}/items/999999"

    response = cert_lane.get_json(url)
    assert response.status_code == 404, f"unknown item returned {response.status_code}"
    body = response.json()
    assert body.get("status") == 404
    assert body.get("title") and body.get("detail")

    with pytest.raises(APIError) as raised:
        StacApiIO(max_retries=0).request(url)
    assert getattr(raised.value, "status_code", None) == 404

    cert_collector.record(
        "NB-STAC-ERR-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"GET an unknown item -> {cert_lane.problem_summary(response)}; pystac-client "
            "surfaced it as APIError(status_code=404)."
        ),
    )


@pytest.mark.cert("NB-STAC-ERR-02")
def test_nb_err_02_malformed_bbox(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """Malformed bbox arity, an inverted bbox and an out-of-range bbox are rejected."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    arity = cert_lane.get_json(search_url, params={"bbox": "1,2,3"})
    assert arity.status_code == 400, f"3-value bbox returned {arity.status_code}"
    assert arity.json().get("status") == 400

    inverted = cert_lane.get_json(search_url, params={"bbox": "10,10,0,0"})
    assert inverted.status_code == 400, (
        f"an inverted bbox (min>max) returned {inverted.status_code}"
    )
    assert inverted.json().get("status") == 400

    out_of_range = cert_lane.get_json(search_url, params={"bbox": "-200,-100,200,100"})
    assert out_of_range.status_code == 400, (
        f"an out-of-range bbox returned {out_of_range.status_code}"
    )

    cert_collector.record(
        "NB-STAC-ERR-02",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"3-value bbox -> {cert_lane.problem_summary(arity)}; inverted bbox -> "
            f"{cert_lane.problem_summary(inverted)}; out-of-range bbox -> "
            f"{cert_lane.problem_summary(out_of_range)}."
        ),
    )


@pytest.mark.cert("NB-STAC-ERR-03")
def test_nb_err_03_malformed_datetime(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A malformed or reversed datetime is rejected with a structured 400."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    response = cert_lane.get_json(search_url, params={"datetime": "not-a-date"})
    assert response.status_code == 400, f"malformed datetime returned {response.status_code}"
    assert response.json().get("status") == 400

    reversed_interval = cert_lane.get_json(
        search_url, params={"datetime": "2024-02-01T00:00:00Z/2024-01-01T00:00:00Z"}
    )
    assert reversed_interval.status_code == 400, (
        f"a reversed datetime interval returned {reversed_interval.status_code}"
    )

    cert_collector.record(
        "NB-STAC-ERR-03",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"datetime=not-a-date -> {cert_lane.problem_summary(response)}; reversed "
            f"interval -> {cert_lane.problem_summary(reversed_interval)}."
        ),
    )


@pytest.mark.cert("NB-STAC-ERR-04")
def test_nb_err_04_unsupported_filter_lang(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """An unsupported ``filter-lang`` is rejected rather than silently ignored."""
    started = time.perf_counter()
    search_url = f"{base_url.rstrip('/')}/stac/search"

    response = cert_lane.get_json(
        search_url, params={"filter": "name = 'alpha'", "filter-lang": "bogus-lang"}
    )
    assert response.status_code == 400, (
        f"unsupported filter-lang returned {response.status_code}; silently ignoring an "
        "unknown filter language would hand a client that asked for a filter an "
        "unfiltered result set"
    )
    body = response.json()
    assert body.get("status") == 400
    assert "filter-lang" in str(body.get("detail", "")).lower()

    orphan = cert_lane.get_json(search_url, params={"filter-lang": "cql2-text"})
    assert orphan.status_code == 400, "filter-lang without filter should be rejected"

    cert_collector.record(
        "NB-STAC-ERR-04",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"filter-lang=bogus-lang -> {cert_lane.problem_summary(response)}; "
            f"filter-lang without filter -> {cert_lane.problem_summary(orphan)}."
        ),
    )


@pytest.mark.cert("NB-STAC-ERR-05")
def test_nb_err_05_wrong_api_key(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """A wrong admin API key is a 401 - not 403, not 500, and never a 200."""
    started = time.perf_counter()
    url = cert_lane.admin_probe_url(base_url)

    response = cert_lane.get_json(
        url, headers={canonical_fixture.ADMIN_API_KEY_HEADER: "definitely-not-the-admin-key"}
    )
    assert response.status_code == 401, (
        f"a wrong API key returned {response.status_code}: {response.text[:200]}"
    )

    cert_collector.record(
        "NB-STAC-ERR-05",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=(
            f"GET {canonical_fixture.ADMIN_PROBE_PATH} with an incorrect "
            f"{canonical_fixture.ADMIN_API_KEY_HEADER} -> 401. Raw httpx: the control "
            "plane is not a STAC endpoint."
        ),
    )


@pytest.mark.cert("NB-STAC-ERR-06")
def test_nb_err_06_unknown_query_parameter(
    base_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """An undeclared search query parameter is rejected rather than ignored."""
    started = time.perf_counter()

    response = cert_lane.get_json(
        f"{base_url.rstrip('/')}/stac/search",
        params={"collections": COLLECTION_ID, "limit": 2, "not-a-real-parameter": "1"},
    )
    assert response.status_code == 400, (
        f"an unknown query parameter returned {response.status_code}; STAC API requires "
        "unknown parameters to be rejected so a typo cannot silently widen a search"
    )
    assert response.json().get("status") == 400

    cert_collector.record(
        "NB-STAC-ERR-06",
        "pass",
        duration_ms=_elapsed_ms(started),
        notes=f"An unknown search query parameter -> {cert_lane.problem_summary(response)}.",
    )


# ===========================================================================
# NB-STAC-VALID — external STAC API conformance validator
# ===========================================================================

#: Polygon the external validator uses for its spatial scenarios. Sized to the
#: seeded fixture extent so item-search returns a non-empty result.
VALIDATOR_GEOMETRY = {
    "type": "Polygon",
    "coordinates": [[
        [-122.5000, 37.7000],
        [-122.4400, 37.7000],
        [-122.4400, 37.7450],
        [-122.5000, 37.7450],
        [-122.5000, 37.7000],
    ]],
}

VALIDATOR_CONFORMANCE_CLASSES = ("core", "collections", "features", "item-search", "filter")
VALIDATOR_TIMEOUT_SECONDS = 240

#: Substrings that mean the validator could not reach the published JSON
#: Schemas rather than that the server is non-conformant. The lane container has
#: no guaranteed egress to schemas.stacspec.org, so this must degrade to a
#: recorded skip instead of a false certification failure.
_SCHEMA_UNREACHABLE_MARKERS = (
    "Max retries exceeded",
    "Temporary failure in name resolution",
    "Name or service not known",
    "NewConnectionError",
    "SSLError",
    "Failed to resolve",
    "unable to resolve",
    "RemoteDisconnected",
)


@pytest.mark.cert("NB-STAC-VALID-01")
def test_nb_valid_01_stac_api_validator(
    stac_api_url: str,
    cert_collector: CertificationEvidenceCollector,
) -> None:
    """The external stac-utils validator agrees the declared classes are conformant.

    Findings are recorded as evidence rather than discarded: a validator run
    that cannot reach the published schemas records ``skip`` with the reason,
    and a genuine conformance finding records ``fail`` with the validator's own
    output, so the envelope always says which of the two happened.
    """
    started = time.perf_counter()

    if importlib.util.find_spec("stac_api_validator") is None:
        note = (
            "stac-api-validator is not installed in this environment; the external "
            "conformance sweep did not run."
        )
        cert_collector.record("NB-STAC-VALID-01", "skip", notes=note)
        pytest.skip(note)

    command = [
        sys.executable,
        "-m",
        "stac_api_validator",
        "--root-url",
        stac_api_url,
        "--collection",
        COLLECTION_ID,
        "--geometry",
        json.dumps(VALIDATOR_GEOMETRY, separators=(",", ":")),
        "--fields-nested-property",
        "properties.name",
        "--no-validate-pagination",
    ]
    for conformance_class in VALIDATOR_CONFORMANCE_CLASSES:
        command.extend(["--conformance", conformance_class])

    try:
        completed = subprocess.run(  # noqa: S603 - fixed argv, no shell
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=VALIDATOR_TIMEOUT_SECONDS,
        )
        returncode = completed.returncode
        output = f"{completed.stdout}\n{completed.stderr}"
    except subprocess.TimeoutExpired as expired:
        returncode = 124
        output = f"{expired.stdout or ''}\n{expired.stderr or ''}\nvalidator timed out"

    unreachable = [marker for marker in _SCHEMA_UNREACHABLE_MARKERS if marker in output]
    failed = (
        returncode != 0
        or "\nFailed.\n" in f"\n{output}\n"
        or "Traceback (most recent call last):" in output
    )
    findings = [
        line.strip()
        for line in output.splitlines()
        if line.strip().startswith(("- ", "Error ", "Failed"))
    ]

    tail = output.strip()[-600:].replace("\n", " | ")

    if failed and unreachable:
        note = (
            "stac-api-validator could not reach the published STAC schemas from the lane "
            f"({', '.join(sorted(set(unreachable)))}); conformance classes "
            f"{', '.join(VALIDATOR_CONFORMANCE_CLASSES)} were not externally validated. "
            f"Validator tail: {tail}"
        )
        cert_collector.record(
            "NB-STAC-VALID-01",
            "skip",
            duration_ms=_elapsed_ms(started),
            measured_count=len(findings),
            notes=note,
        )
        pytest.skip(note)

    if failed:
        note = (
            f"stac-api-validator reported {len(findings)} finding(s) against "
            f"{', '.join(VALIDATOR_CONFORMANCE_CLASSES)} (exit {returncode}). "
            f"Findings: {' || '.join(findings[:12]) or tail}"
        )
        cert_collector.record(
            "NB-STAC-VALID-01",
            "fail",
            duration_ms=_elapsed_ms(started),
            measured_count=len(findings),
            notes=note,
        )
        pytest.fail(note, pytrace=False)

    cert_collector.record(
        "NB-STAC-VALID-01",
        "pass",
        duration_ms=_elapsed_ms(started),
        measured_count=len(VALIDATOR_CONFORMANCE_CLASSES),
        notes=(
            "stac-api-validator validated "
            f"{', '.join(VALIDATOR_CONFORMANCE_CLASSES)} against {stac_api_url} "
            f"with exit code {returncode}."
        ),
    )
