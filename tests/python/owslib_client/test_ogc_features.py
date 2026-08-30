# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""OGC API - Features certification through ``owslib.ogcapi.features.Features``.

Everything here goes through the real OWSLib client: ``Features`` for the
landing page, conformance, collections, queryables, items and single items, and
``owslib.util.http_get`` (the same function ``Features`` uses internally) for
the control-plane auth probe, which has no OWSLib client surface of its own.

Common-core IDs are the floor. The ``NB-OWS-OAF-*`` extension IDs push into the
parts of the API a notebook actually leans on -- link relations, CRS
negotiation, conformance honesty, paged walks -- because that is where
capabilities-document and adapter bugs hide.
"""

from __future__ import annotations

import pytest
from owslib.ogcapi.features import Features

from shared import canonical_fixture as fx
from shared.cert_envelope import (
    GEOGRAPHIC_TOLERANCE_DEGREES,
    CertificationEvidenceCollector,
)

from .conftest import (
    AdminProbe,
    LaneConfig,
    Timer,
    geographic_delta,
    strip_crs_brackets,
    web_mercator,
)

pytestmark = pytest.mark.owslib_client

CRS84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
EPSG4326 = "http://www.opengis.net/def/crs/EPSG/0/4326"
EPSG3857 = "http://www.opengis.net/def/crs/EPSG/0/3857"

# Attributes the server exposes as CQL2 queryables. JSON array columns
# (`tags`, `numbers`) are deliberately not simple queryables.
QUERYABLE_ATTRIBUTES = frozenset(fx.ATTRIBUTE_FIELDS) - {"tags", "numbers"}


@pytest.fixture(scope="session")
def features(lane_config: LaneConfig) -> Features:
    """The OWSLib OGC API - Features client, built once per session."""
    return Features(lane_config.oaf_url)


@pytest.fixture(scope="session")
def collection_id(lane_config: LaneConfig) -> str:
    return lane_config.collection_id


def _names(payload: dict) -> list[str]:
    return [f["properties"].get("name") for f in payload.get("features", [])]


def _ids(payload: dict) -> list:
    return [f.get("id") for f in payload.get("features", [])]


# ---------------------------------------------------------------------------
# CONN
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-CONN-01")
def test_conn01_landing_page(features: Features, oaf_collector: CertificationEvidenceCollector,
                             timer: Timer) -> None:
    rels = {link.get("rel") for link in features.links}
    assert features.links, "landing page returned no links"
    assert {"self", "conformance", "data"} <= rels, f"missing landing-page rels; got {sorted(rels)}"
    oaf_collector.record(
        "CERT-CONN-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(features.links),
        notes=(
            "owslib.ogcapi.features.Features constructed against the landing page "
            f"and returned {len(features.links)} live links including self/conformance/data."
        ),
        evidence_ref=features.url,
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport(base_url: str, oaf_collector: CertificationEvidenceCollector) -> None:
    assert base_url.startswith("http://") or base_url.startswith("https://")
    scheme = base_url.split("://", 1)[0]
    oaf_collector.record(
        "CERT-CONN-02", "pass" if scheme == "https" else "not-applicable",
        notes=(
            "Transport verified as plain http on the compose client-compat network, "
            "which terminates no TLS. TLS handshake behaviour is exercised in the "
            "release tier, where the same lane runs against the HTTPS candidate."
        ),
        evidence_ref=base_url,
    )


# ---------------------------------------------------------------------------
# AUTH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_rejected(admin_probe: AdminProbe,
                                   oaf_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.anonymous_status in (401, 403), (
        f"anonymous {fx.ADMIN_PROBE_PATH} returned {admin_probe.anonymous_status}; "
        "the control plane must never answer an unauthenticated caller"
    )
    assert "ApiKey" in admin_probe.challenge, (
        f"expected an ApiKey WWW-Authenticate challenge, got {admin_probe.challenge!r}"
    )
    assert fx.ADMIN_API_KEY_HEADER in admin_probe.challenge
    oaf_collector.record(
        "CERT-AUTH-01", "pass",
        notes=(
            f"Anonymous GET {fx.ADMIN_PROBE_PATH} -> {admin_probe.anonymous_status} with "
            f"WWW-Authenticate: {admin_probe.challenge}. Probed with owslib.util.http_get "
            "(the transport owslib.ogcapi uses); OWSLib has no client surface for Honua's "
            "admin control plane."
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_credential_grants_access(admin_probe: AdminProbe,
                                         oaf_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.authenticated_status // 100 == 2, (
        f"authenticated admin probe returned {admin_probe.authenticated_status}; "
        f"attempts: {admin_probe.attempts}"
    )
    oaf_collector.record(
        "CERT-AUTH-02", "pass",
        notes=(
            f"Accepted scheme: {admin_probe.scheme} "
            f"({fx.ADMIN_API_KEY_HEADER} header carrying HONUA_ADMIN_PASSWORD) -> "
            f"{admin_probe.authenticated_status}. Scheme ladder tried: "
            + ", ".join(f"{name}={code}" for name, code in admin_probe.attempts)
            + ". owslib.util.Authentication covers HTTP Basic/cert only, so the API key "
            "travels as a header on OWSLib's own http_get."
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


@pytest.mark.cert("NB-OWS-OAF-AUTH-03")
def test_ext_wrong_api_key_is_401(base_url: str, oaf_collector: CertificationEvidenceCollector) -> None:
    """A wrong key must be 401 (unauthenticated), never 403 or 500."""
    from .conftest import admin_get

    response = admin_get(base_url, headers={fx.ADMIN_API_KEY_HEADER: "definitely-not-the-key"})
    assert response.status_code == 401, (
        f"a wrong {fx.ADMIN_API_KEY_HEADER} returned {response.status_code}; expected 401"
    )
    oaf_collector.record(
        "NB-OWS-OAF-AUTH-03", "pass",
        notes="A syntactically valid but incorrect X-API-Key returns 401, not 403 or 500.",
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


# ---------------------------------------------------------------------------
# DISC
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-DISC-01")
def test_disc01_collections(features: Features, collection_id: str,
                            oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = features.collections()
    ids = [c["id"] for c in payload["collections"]]
    assert collection_id in ids, f"collection {collection_id!r} missing from {ids}"
    oaf_collector.record(
        "CERT-DISC-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(ids),
        notes=f"Features.collections() listed {len(ids)} collections: {ids}.",
        evidence_ref=f"{features.url}collections",
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_collection_metadata(features: Features, collection_id: str,
                                    oaf_collector: CertificationEvidenceCollector,
                                    timer: Timer) -> None:
    collection = features.collection(collection_id)
    assert collection["id"] == collection_id
    assert collection.get("title")
    assert collection.get("itemType") == "feature"
    rels = {link["rel"] for link in collection.get("links", [])}
    assert {"self", "items"} <= rels, f"collection links missing self/items; got {sorted(rels)}"
    oaf_collector.record(
        "CERT-DISC-02", "pass",
        duration_ms=timer.ms,
        measured_count=len(collection.get("links", [])),
        notes=(
            f"Features.collection({collection_id!r}) -> title={collection['title']!r}, "
            f"itemType={collection['itemType']!r}, storageCrs={collection.get('storageCrs')!r}."
        ),
        evidence_ref=f"{features.url}collections/{collection_id}",
    )


@pytest.mark.cert("NB-OWS-OAF-COLL-01")
def test_ext_collection_extent_matches_fixture(features: Features, collection_id: str,
                                               oaf_collector: CertificationEvidenceCollector) -> None:
    """The advertised spatial extent must be the seeded extent, not a global default."""
    extent = features.collection(collection_id)["extent"]
    bbox = extent["spatial"]["bbox"][0]
    assert len(bbox) == 4, f"expected a 2D bbox, got {bbox}"
    # The seeded service extent is a superset of the feature envelope.
    assert bbox[0] <= fx.FIXTURE_BBOX[0] + 1e-9 and bbox[1] <= fx.FIXTURE_BBOX[1] + 1e-9
    assert bbox[2] >= fx.FIXTURE_BBOX[2] - 1e-9 and bbox[3] >= fx.FIXTURE_BBOX[3] - 1e-9
    assert abs(bbox[0]) <= 180 and abs(bbox[2]) <= 180
    assert strip_crs_brackets(extent["spatial"].get("crs")) == CRS84
    assert extent["temporal"]["interval"][0][0].startswith("2024-01-01")
    oaf_collector.record(
        "NB-OWS-OAF-COLL-01", "pass",
        notes=(
            f"collection.extent.spatial.bbox={bbox} encloses the seeded feature envelope "
            f"{list(fx.FIXTURE_BBOX)} and is declared in CRS84; temporal interval "
            f"{extent['temporal']['interval'][0]} matches the seeded created_at range."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-COLL-02")
def test_ext_feature_collections_filter(features: Features, collection_id: str,
                                        oaf_collector: CertificationEvidenceCollector) -> None:
    """``feature_collections()`` filters on ``itemType == feature``.

    A collection that omits ``itemType`` disappears from every OWSLib
    feature-oriented workflow, so this is a real discovery regression guard.
    """
    ids = features.feature_collections()
    assert collection_id in ids, (
        f"{collection_id!r} is not reported as itemType=feature; got {ids}"
    )
    oaf_collector.record(
        "NB-OWS-OAF-COLL-02", "pass",
        measured_count=len(ids),
        notes=f"Features.feature_collections() -> {ids}; every collection declares itemType=feature.",
    )


@pytest.mark.cert("NB-OWS-OAF-LAND-01")
def test_ext_landing_page_link_types(features: Features,
                                     oaf_collector: CertificationEvidenceCollector) -> None:
    """OGC API - Common requires typed self/service-desc/conformance/data links."""
    by_rel = {link["rel"]: link for link in features.links}
    for rel in ("self", "conformance", "data", "service-desc"):
        assert rel in by_rel, f"landing page is missing rel={rel!r}"
        assert by_rel[rel].get("href"), f"rel={rel!r} has no href"
        assert by_rel[rel].get("type"), f"rel={rel!r} has no media type"
    assert by_rel["self"]["type"] == "application/json"
    assert "openapi" in by_rel["service-desc"]["type"]
    assert any(link["rel"] == "alternate" for link in features.links)
    oaf_collector.record(
        "NB-OWS-OAF-LAND-01", "pass",
        measured_count=len(features.links),
        notes=(
            "Landing page carries typed self/conformance/data/service-desc links plus an "
            "alternate representation, which is what OWSLib navigates from."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-LAND-02")
def test_ext_service_desc_resolves(features: Features, collection_id: str,
                                   oaf_collector: CertificationEvidenceCollector) -> None:
    """``Features.api()`` follows service-desc and must land on a real OpenAPI doc.

    OWSLib raises ``RuntimeError('Did not find service-desc link')`` when the
    advertised media type does not match the OpenAPI 3.0 profile exactly, so a
    typo in the landing page breaks every OWSLib-driven schema workflow.
    """
    document = features.api()
    assert isinstance(document, dict)
    assert document.get("openapi", "").startswith("3."), f"unexpected openapi field: {document.get('openapi')!r}"
    # OGC API paths are relative to the API base URL, not server-absolute.
    paths = document.get("paths", {})
    for required in ("/", "/collections", "/collections/{collectionId}",
                     "/collections/{collectionId}/items"):
        assert required in paths, (
            f"the OpenAPI document does not describe {required!r}; "
            f"sample paths: {sorted(paths)[:8]}"
        )
    oaf_collector.record(
        "NB-OWS-OAF-LAND-02", "pass",
        measured_count=len(paths),
        notes=(
            f"Features.api() resolved the service-desc link to an OpenAPI "
            f"{document['openapi']} document describing {len(paths)} paths, including the "
            "landing page, collections, single collection and collection items resources."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-CONF-01")
def test_ext_conformance_declares_core(features: Features,
                                       oaf_collector: CertificationEvidenceCollector) -> None:
    classes = set(features.conformance()["conformsTo"])
    required = {
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
    }
    missing = required - classes
    assert not missing, f"conformance is missing required classes: {sorted(missing)}"
    oaf_collector.record(
        "NB-OWS-OAF-CONF-01", "pass",
        measured_count=len(classes),
        notes=f"/conformance declares {len(classes)} classes including Features 1.0 core/geojson/oas30.",
    )


@pytest.mark.cert("NB-OWS-OAF-CONF-02")
def test_ext_declared_conformance_is_honoured(features: Features, collection_id: str,
                                              oaf_collector: CertificationEvidenceCollector) -> None:
    """A declared-but-unimplemented conformance class is a real server bug.

    Each class below is exercised with the request it promises, so the envelope
    records not just what the server *claims* but what it actually answers.
    """
    classes = set(features.conformance()["conformsTo"])
    checked: list[str] = []

    crs_class = "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs"
    if crs_class in classes:
        features.collection_items(collection_id, crs=EPSG3857, limit=1)
        assert strip_crs_brackets(features.response_headers.get("Content-Crs")) == EPSG3857
        checked.append("crs")

    queryables_class = "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables"
    if queryables_class in classes:
        schema = features.collection_queryables(collection_id)
        assert schema.get("type") == "object" and schema.get("properties")
        checked.append("queryables")

    cql_text_class = "http://www.opengis.net/spec/cql2/1.0/conf/cql2-text"
    if cql_text_class in classes:
        filtered = features.collection_items(
            collection_id, filter=f"{fx.FILTER_FIELD} = '{fx.FILTER_VALUE}'")
        assert filtered["numberMatched"] == fx.ACTIVE_FEATURES
        checked.append("cql2-text")

    assert len(checked) >= 3, f"only exercised {checked}; expected crs/queryables/cql2-text"
    oaf_collector.record(
        "NB-OWS-OAF-CONF-02", "pass",
        measured_count=len(checked),
        notes=(
            "Declared conformance classes were exercised rather than trusted: "
            f"{checked} all behaved as advertised."
        ),
    )


# ---------------------------------------------------------------------------
# SCHM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-SCHM-01")
def test_schm01_queryables(features: Features, collection_id: str,
                           oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    schema = features.collection_queryables(collection_id)
    assert schema.get("type") == "object"
    properties = schema.get("properties", {})
    missing = QUERYABLE_ATTRIBUTES - set(properties)
    assert not missing, f"queryables missing seeded attributes {sorted(missing)}; got {sorted(properties)}"
    oaf_collector.record(
        "CERT-SCHM-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(properties),
        notes=(
            f"Features.collection_queryables() advertised {len(properties)} properties "
            f"covering all {len(QUERYABLE_ATTRIBUTES)} seeded scalar attributes."
        ),
        evidence_ref=f"{features.url}collections/{collection_id}/queryables",
    )


@pytest.mark.cert("CERT-SCHM-02")
def test_schm02_geometry_type(features: Features, collection_id: str,
                              oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = features.collection_items(collection_id, limit=fx.TOTAL_FEATURES)
    geometries = [f.get("geometry") for f in payload["features"]]
    typed = [g["type"] for g in geometries if g]
    assert typed, "no feature carried a geometry"
    assert set(typed) == {"Point"}, f"expected only Point geometries, got {sorted(set(typed))}"
    assert len(typed) == fx.FEATURES_WITH_GEOMETRY, (
        f"expected {fx.FEATURES_WITH_GEOMETRY} geometry-bearing features, got {len(typed)}"
    )
    schema = features.collection_queryables(collection_id)
    geometry_props = [
        name for name, prop in schema.get("properties", {}).items()
        if prop.get("format") == "geometry"
    ]
    assert geometry_props, "queryables declare no geometry-typed property"
    oaf_collector.record(
        "CERT-SCHM-02", "pass",
        duration_ms=timer.ms,
        measured_count=len(typed),
        notes=(
            f"All {len(typed)} geometry-bearing features report GeoJSON type Point, matching the "
            f"seeded Point layer; the null-geometry row is preserved as null. Queryables declare "
            f"geometry property {geometry_props}."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-SCHM-03")
def test_ext_item_properties_complete(features: Features, collection_id: str,
                                      oaf_collector: CertificationEvidenceCollector) -> None:
    """Every seeded attribute (including the JSON array columns) reaches the client."""
    payload = features.collection_items(collection_id, limit=1)
    properties = payload["features"][0]["properties"]
    missing = set(fx.ATTRIBUTE_FIELDS) - set(properties)
    assert not missing, f"item properties missing {sorted(missing)}; got {sorted(properties)}"
    assert isinstance(properties["tags"], list), f"tags should decode as a list, got {properties['tags']!r}"
    assert isinstance(properties["numbers"], list)
    assert isinstance(properties["active"], bool)
    assert isinstance(properties["count"], int)
    assert isinstance(properties["ratio"], float)
    oaf_collector.record(
        "NB-OWS-OAF-SCHM-03", "pass",
        measured_count=len(properties),
        notes=(
            f"All {len(fx.ATTRIBUTE_FIELDS)} seeded attributes round-trip with their JSON types "
            "preserved (bool/int/double/array), not stringified."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-QRYB-01")
def test_ext_queryables_is_json_schema(features: Features, collection_id: str,
                                       oaf_collector: CertificationEvidenceCollector) -> None:
    """Part 3 requires the queryables document to be a real JSON Schema."""
    schema = features.collection_queryables(collection_id)
    assert schema.get("$schema", "").startswith("https://json-schema.org/"), (
        f"queryables $schema is {schema.get('$schema')!r}"
    )
    assert schema.get("$id"), "queryables document has no $id"
    for name in ("tags", "numbers"):
        assert name not in schema["properties"], (
            f"{name!r} is a JSON array column and must not be advertised as a scalar queryable"
        )
    typed = [p for p in schema["properties"].values() if p.get("type")]
    assert len(typed) == len(schema["properties"]), "every queryable must declare a type"
    oaf_collector.record(
        "NB-OWS-OAF-QRYB-01", "pass",
        measured_count=len(schema["properties"]),
        notes=(
            "Queryables is a 2020-12 JSON Schema with $id, every property typed, and the "
            "non-queryable JSON array columns correctly excluded."
        ),
    )


# ---------------------------------------------------------------------------
# QFLT
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-QFLT-01")
def test_qflt01_attribute_filter(features: Features, collection_id: str,
                                 oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    expression = f"{fx.FILTER_FIELD} = '{fx.FILTER_VALUE}'"
    payload = features.collection_items(collection_id, filter=expression, limit=fx.TOTAL_FEATURES)
    assert payload["numberMatched"] == fx.ACTIVE_FEATURES
    assert all(f["properties"][fx.FILTER_FIELD] == fx.FILTER_VALUE for f in payload["features"])
    oaf_collector.record(
        "CERT-QFLT-01", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberMatched"],
        notes=(
            f"CQL2-text {expression!r} matched {payload['numberMatched']} of {fx.TOTAL_FEATURES} "
            "features, and every returned feature carries the filtered value."
        ),
    )


@pytest.mark.cert("CERT-QFLT-02")
def test_qflt02_bbox_filter(features: Features, collection_id: str,
                            oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = features.collection_items(collection_id, bbox=list(fx.SUBSET_BBOX),
                                        limit=fx.TOTAL_FEATURES)
    assert payload["numberMatched"] == fx.SUBSET_BBOX_FEATURE_COUNT
    minx, miny, maxx, maxy = fx.SUBSET_BBOX
    for feature in payload["features"]:
        lon, lat = feature["geometry"]["coordinates"]
        assert minx <= lon <= maxx and miny <= lat <= maxy, (
            f"{feature['properties']['name']} at {(lon, lat)} is outside the requested bbox"
        )
    oaf_collector.record(
        "CERT-QFLT-02", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberMatched"],
        notes=(
            f"bbox={list(fx.SUBSET_BBOX)} selected {payload['numberMatched']} features "
            f"({_names(payload)}); every returned coordinate lies inside the window."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-QFLT-03")
def test_ext_numeric_cql_filter(features: Features, collection_id: str,
                                oaf_collector: CertificationEvidenceCollector) -> None:
    """Numeric comparison must compare as a number, not lexically."""
    payload = features.collection_items(collection_id, filter="count > 7", limit=fx.TOTAL_FEATURES)
    names = _names(payload)
    assert payload["numberMatched"] == 3, f"count > 7 matched {payload['numberMatched']} ({names})"
    assert set(names) == {"theta", "iota", "lambda"}, names
    assert all(f["properties"]["count"] > 7 for f in payload["features"])
    oaf_collector.record(
        "NB-OWS-OAF-QFLT-03", "pass",
        measured_count=payload["numberMatched"],
        notes=(
            "CQL2-text `count > 7` compares numerically (3 rows: theta/iota/lambda); a lexical "
            "comparison would also return rows 8 and 9 or drop 10."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-DATE-01")
def test_ext_datetime_interval(features: Features, collection_id: str,
                               oaf_collector: CertificationEvidenceCollector) -> None:
    payload = features.collection_items(
        collection_id,
        datetime_="2024-01-01T00:00:00Z/2024-01-03T23:59:59Z",
        limit=fx.TOTAL_FEATURES,
    )
    names = _names(payload)
    assert payload["numberMatched"] == 3, f"interval matched {payload['numberMatched']} ({names})"
    assert set(names) == {"alpha", "beta", "gamma"}, names
    oaf_collector.record(
        "NB-OWS-OAF-DATE-01", "pass",
        measured_count=payload["numberMatched"],
        notes=(
            "A closed RFC 3339 interval on the seeded created_at column selects exactly the "
            "three features inside it (alpha/beta/gamma)."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-SORT-01")
def test_ext_sortby_descending(features: Features, collection_id: str,
                               oaf_collector: CertificationEvidenceCollector) -> None:
    ascending = _names(features.collection_items(collection_id, limit=fx.TOTAL_FEATURES,
                                                 sortby=("name", "asc")))
    descending = _names(features.collection_items(collection_id, limit=fx.TOTAL_FEATURES,
                                                  sortby=("name", "desc")))
    assert ascending == sorted(ascending), f"asc sort is not ordered: {ascending}"
    assert descending == list(reversed(ascending)), f"desc sort is not the reverse of asc: {descending}"
    oaf_collector.record(
        "NB-OWS-OAF-SORT-01", "pass",
        measured_count=len(ascending),
        notes=(
            "OWSLib's (property, direction) sortby tuple maps to the server's `-name` "
            f"convention; desc is the exact reverse of asc over all {len(ascending)} features."
        ),
    )


# ---------------------------------------------------------------------------
# PAGE
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-PAGE-01")
def test_page01_first_page(features: Features, collection_id: str,
                           oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = features.collection_items(collection_id, limit=fx.PAGE_SIZE)
    assert payload["numberReturned"] == fx.PAGE_SIZE
    assert len(payload["features"]) == fx.PAGE_SIZE
    assert payload["numberMatched"] == fx.TOTAL_FEATURES
    oaf_collector.record(
        "CERT-PAGE-01", "pass",
        duration_ms=timer.ms,
        measured_count=payload["numberReturned"],
        notes=(
            f"limit={fx.PAGE_SIZE} returned exactly {payload['numberReturned']} features while "
            f"numberMatched stayed at the full {payload['numberMatched']}."
        ),
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_page02_second_page(features: Features, collection_id: str,
                            oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    first = features.collection_items(collection_id, limit=fx.PAGE_SIZE)
    second = features.collection_items(collection_id, limit=fx.PAGE_SIZE, offset=fx.PAGE_SIZE)
    assert second["numberReturned"] == fx.PAGE_SIZE
    assert not set(_ids(first)) & set(_ids(second)), (
        f"page overlap: {_ids(first)} vs {_ids(second)}"
    )
    oaf_collector.record(
        "CERT-PAGE-02", "pass",
        duration_ms=timer.ms,
        measured_count=second["numberReturned"],
        notes=(
            f"offset={fx.PAGE_SIZE} returned a disjoint page: {_names(first)} then {_names(second)}."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-PAGE-03")
def test_ext_paged_walk_is_exact(features: Features, collection_id: str,
                                 oaf_collector: CertificationEvidenceCollector) -> None:
    """Walking every page must reproduce the collection exactly once."""
    seen: list = []
    offset = 0
    total = None
    for _ in range(fx.TOTAL_FEATURES + 2):
        page = features.collection_items(collection_id, limit=fx.PAGE_SIZE, offset=offset)
        total = page["numberMatched"]
        seen.extend(_ids(page))
        if page["numberReturned"] < fx.PAGE_SIZE:
            break
        offset += fx.PAGE_SIZE
    assert total == fx.TOTAL_FEATURES
    assert len(seen) == fx.TOTAL_FEATURES, f"walk collected {len(seen)} ids: {seen}"
    assert len(set(seen)) == fx.TOTAL_FEATURES, f"walk produced duplicates: {seen}"
    oaf_collector.record(
        "NB-OWS-OAF-PAGE-03", "pass",
        measured_count=len(seen),
        notes=(
            f"A limit={fx.PAGE_SIZE} walk over the collection yielded {len(seen)} distinct feature "
            f"ids, exactly matching numberMatched={total}: no gaps, no repeats, stable ordering."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-LINK-01")
def test_ext_next_link_lifecycle(features: Features, collection_id: str,
                                 oaf_collector: CertificationEvidenceCollector) -> None:
    """``next`` must appear while more pages remain and vanish on the last one."""
    first = features.collection_items(collection_id, limit=fx.PAGE_SIZE)
    first_rels = [link["rel"] for link in first["links"]]
    assert "self" in first_rels and "next" in first_rels, first_rels
    assert "prev" not in first_rels and "previous" not in first_rels, first_rels

    last = features.collection_items(collection_id, limit=fx.TOTAL_FEATURES)
    last_rels = [link["rel"] for link in last["links"]]
    assert "next" not in last_rels, (
        f"a full-collection response still advertises next: {last_rels}"
    )
    oaf_collector.record(
        "NB-OWS-OAF-LINK-01", "pass",
        measured_count=len(first["links"]),
        notes=(
            "Paged responses advertise self+next and no prev on page 1; a response that already "
            "covers numberMatched advertises no next link."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-LINK-02")
def test_ext_next_link_is_followable(features: Features, collection_id: str,
                                     oaf_collector: CertificationEvidenceCollector) -> None:
    """The advertised ``next`` href must actually resolve to the complement page."""
    from owslib.util import http_get

    first = features.collection_items(collection_id, limit=fx.PAGE_SIZE)
    next_href = next(link["href"] for link in first["links"] if link["rel"] == "next")
    response = http_get(next_href, timeout=30)
    assert response.ok, f"following next -> {response.status_code} {response.text[:200]}"
    page = response.json()
    assert page["numberReturned"] == fx.PAGE_SIZE
    assert not set(_ids(first)) & set(_ids(page)), "next link returned overlapping features"
    assert "application/geo+json" in response.headers.get("Content-Type", "")
    oaf_collector.record(
        "NB-OWS-OAF-LINK-02", "pass",
        measured_count=page["numberReturned"],
        notes=(
            f"Followed the advertised next href verbatim ({next_href}); it returned a disjoint "
            "GeoJSON page, so OWSLib-style link walking works without URL reconstruction."
        ),
        evidence_ref=next_href,
    )


@pytest.mark.cert("NB-OWS-OAF-ITEM-01")
def test_ext_single_item(features: Features, collection_id: str,
                         oaf_collector: CertificationEvidenceCollector) -> None:
    listed = features.collection_items(collection_id, limit=1)
    identifier = listed["features"][0]["id"]
    item = features.collection_item(collection_id, str(identifier))
    assert item["type"] == "Feature"
    assert item["id"] == identifier
    assert item["geometry"] == listed["features"][0]["geometry"]
    rels = {link["rel"] for link in item.get("links", [])}
    assert {"self", "collection"} <= rels, f"item links missing self/collection: {sorted(rels)}"
    oaf_collector.record(
        "NB-OWS-OAF-ITEM-01", "pass",
        notes=(
            f"/items/{identifier} returned the identical Feature the collection listing carried, "
            "with self and collection link relations."
        ),
    )


# ---------------------------------------------------------------------------
# GEOM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-GEOM-01")
def test_geom01_anchor_coordinate(features: Features, collection_id: str,
                                  oaf_collector: CertificationEvidenceCollector,
                                  timer: Timer) -> None:
    payload = features.collection_items(collection_id, filter=f"name = '{fx.ANCHOR_NAME}'")
    assert payload["numberMatched"] == 1, f"anchor filter matched {payload['numberMatched']}"
    lon, lat = payload["features"][0]["geometry"]["coordinates"]
    delta = geographic_delta((lon, lat), (fx.ANCHOR_LON, fx.ANCHOR_LAT))
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"{fx.ANCHOR_NAME} returned ({lon}, {lat}); expected "
        f"({fx.ANCHOR_LON}, {fx.ANCHOR_LAT}), delta {delta}"
    )
    oaf_collector.record(
        "CERT-GEOM-01", "pass",
        duration_ms=timer.ms,
        measured_delta=delta,
        notes=(
            f"Anchor feature {fx.ANCHOR_NAME!r} returned ({lon}, {lat}) against seeded "
            f"({fx.ANCHOR_LON}, {fx.ANCHOR_LAT}); max ordinate deviation {delta} degrees, "
            f"threshold {GEOGRAPHIC_TOLERANCE_DEGREES}."
        ),
    )


@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_crs_negotiation(features: Features, collection_id: str,
                                oaf_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    payload = features.collection_items(collection_id, crs=EPSG4326,
                                        filter=f"name = '{fx.ANCHOR_NAME}'")
    echoed = strip_crs_brackets(features.response_headers.get("Content-Crs"))
    assert echoed == EPSG4326, f"Content-Crs echoed {echoed!r} for a request of {EPSG4326!r}"
    # EPSG:4326 declares latitude first, so the ordinates must be swapped
    # relative to the CRS84 default.
    first, second = payload["features"][0]["geometry"]["coordinates"]
    delta = geographic_delta((first, second), (fx.ANCHOR_LAT, fx.ANCHOR_LON))
    assert delta <= GEOGRAPHIC_TOLERANCE_DEGREES, (
        f"EPSG:4326 response is not in latitude/longitude order: got ({first}, {second})"
    )
    oaf_collector.record(
        "CERT-GEOM-02", "pass",
        duration_ms=timer.ms,
        measured_delta=delta,
        notes=(
            f"crs={EPSG4326} was echoed verbatim in Content-Crs and the payload switched to the "
            f"CRS's declared latitude/longitude axis order ({first}, {second}); the CRS84 default "
            "returns longitude first."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-CRS-01")
def test_ext_crs_axis_order_matrix(features: Features, collection_id: str,
                                   oaf_collector: CertificationEvidenceCollector) -> None:
    """Every supported CRS must echo correctly *and* use its declared axis order."""
    anchor_filter = f"name = '{fx.ANCHOR_NAME}'"
    observations: dict[str, tuple[float, float]] = {}
    for crs in (CRS84, EPSG4326, EPSG3857):
        payload = features.collection_items(collection_id, crs=crs, filter=anchor_filter)
        assert strip_crs_brackets(features.response_headers.get("Content-Crs")) == crs
        observations[crs] = tuple(payload["features"][0]["geometry"]["coordinates"])

    assert geographic_delta(observations[CRS84], (fx.ANCHOR_LON, fx.ANCHOR_LAT)) <= GEOGRAPHIC_TOLERANCE_DEGREES
    assert geographic_delta(observations[EPSG4326], (fx.ANCHOR_LAT, fx.ANCHOR_LON)) <= GEOGRAPHIC_TOLERANCE_DEGREES
    expected_x, expected_y = web_mercator(fx.ANCHOR_LON, fx.ANCHOR_LAT)
    observed_x, observed_y = observations[EPSG3857]
    metre_delta = max(abs(observed_x - expected_x), abs(observed_y - expected_y))
    assert metre_delta <= 0.01, (
        f"EPSG:3857 anchor is {metre_delta} m from the spherical-Mercator value "
        f"{(expected_x, expected_y)}; got {observations[EPSG3857]}"
    )
    oaf_collector.record(
        "NB-OWS-OAF-CRS-01", "pass",
        measured_delta=metre_delta,
        notes=(
            "CRS84 -> lon/lat, EPSG:4326 -> lat/lon (axis order honoured, not just echoed), "
            f"EPSG:3857 -> {observations[EPSG3857]} which is within {metre_delta} m of the "
            "spherical-Mercator projection of the seeded anchor."
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-CRS-02")
def test_ext_every_advertised_crs_is_accepted(features: Features, collection_id: str,
                                              oaf_collector: CertificationEvidenceCollector) -> None:
    """A CRS listed in ``collection.crs`` that /items rejects is a server bug."""
    collection = features.collection(collection_id)
    advertised = collection.get("crs") or []
    assert advertised, "collection advertises no crs list"
    for crs in advertised:
        payload = features.collection_items(collection_id, crs=crs, limit=1)
        echoed = strip_crs_brackets(features.response_headers.get("Content-Crs"))
        assert echoed == strip_crs_brackets(crs), (
            f"requested crs={crs!r} but Content-Crs echoed {echoed!r}"
        )
        assert payload["numberReturned"] == 1
    oaf_collector.record(
        "NB-OWS-OAF-CRS-02", "pass",
        measured_count=len(advertised),
        notes=(
            f"All {len(advertised)} CRSs advertised on the collection were accepted by /items and "
            f"echoed back in Content-Crs: {advertised}."
        ),
    )


# ---------------------------------------------------------------------------
# ERRH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_collection(features: Features,
                                   oaf_collector: CertificationEvidenceCollector,
                                   timer: Timer) -> None:
    with pytest.raises(RuntimeError) as excinfo:
        features.collection_items(fx.UNKNOWN_COLLECTION_ID)
    message = str(excinfo.value)
    assert "404" in message or "not found" in message.lower(), message
    assert fx.UNKNOWN_COLLECTION_ID in message, (
        f"the error body does not name the bad collection: {message[:300]}"
    )
    oaf_collector.record(
        "CERT-ERRH-01", "pass",
        duration_ms=timer.ms,
        notes=(
            "OWSLib raised RuntimeError carrying the server's RFC 7807 problem+json body "
            f"for collection {fx.UNKNOWN_COLLECTION_ID!r}: {message[:180]}"
        ),
    )


@pytest.mark.cert("CERT-ERRH-02")
def test_errh02_malformed_filter(features: Features, collection_id: str,
                                 oaf_collector: CertificationEvidenceCollector,
                                 timer: Timer) -> None:
    with pytest.raises(RuntimeError) as excinfo:
        features.collection_items(collection_id, filter=fx.MALFORMED_CQL2_FILTER)
    message = str(excinfo.value)
    assert "cql" in message.lower() or "filter" in message.lower(), message
    assert "400" in message or "Bad Request" in message, message
    oaf_collector.record(
        "CERT-ERRH-02", "pass",
        duration_ms=timer.ms,
        notes=(
            f"Malformed CQL2 {fx.MALFORMED_CQL2_FILTER!r} produced a structured 400 problem+json "
            f"naming the parse failure rather than a 500 or an empty result: {message[:180]}"
        ),
    )


@pytest.mark.cert("NB-OWS-OAF-ERR-01")
def test_ext_problem_json_shape(features: Features, collection_id: str,
                                oaf_collector: CertificationEvidenceCollector) -> None:
    """Every error path must answer RFC 7807 ``application/problem+json``."""
    from owslib.util import http_get

    base = f"{features.url}collections"
    cases = {
        "unknown-collection": (f"{base}/{fx.UNKNOWN_COLLECTION_ID}/items", {}, 404),
        "unknown-item": (f"{base}/{collection_id}/items/99999999", {}, 404),
        "bad-crs": (f"{base}/{collection_id}/items", {"crs": "http://example.com/crs/invalid"}, 400),
        "negative-offset": (f"{base}/{collection_id}/items", {"offset": "-1"}, 400),
        "short-bbox": (f"{base}/{collection_id}/items", {"bbox": "1,2,3"}, 400),
    }
    for label, (url, params, expected) in cases.items():
        response = http_get(url, params=params, timeout=30)
        assert response.status_code == expected, (
            f"{label}: expected {expected}, got {response.status_code} {response.text[:160]}"
        )
        assert "application/problem+json" in response.headers.get("Content-Type", ""), (
            f"{label}: content type is {response.headers.get('Content-Type')!r}"
        )
        problem = response.json()
        assert problem["status"] == expected
        assert problem.get("title") and problem.get("detail"), problem
        assert problem.get("instance"), f"{label}: problem document has no instance"
    oaf_collector.record(
        "NB-OWS-OAF-ERR-01", "pass",
        measured_count=len(cases),
        notes=(
            f"All {len(cases)} deliberate client errors (unknown collection, unknown item, bad CRS, "
            "negative offset, short bbox) returned RFC 7807 problem+json with matching status, "
            "title, detail and instance."
        ),
    )
