"""Unit tests for ``honua_qgis.client``.

The HTTP client is tested with an injected fake ``urlopen`` so the tests
do not require a running Honua server. The fake returns canned responses
keyed by URL.
"""

from __future__ import annotations

import json
from urllib.error import URLError

import pytest

from honua_qgis.auth import HonuaConnection
from honua_qgis.client import (
    HonuaClient,
    HonuaClientError,
    parse_wms_capabilities,
    _extract_bbox,
    _extract_storage_crs,
)


class FakeResponse:
    """Minimal stand-in for ``http.client.HTTPResponse``."""

    def __init__(self, body: bytes, status: int = 200, headers: dict | None = None):
        self._body = body
        self.status = status
        self.headers = headers or {"Content-Type": "application/json"}

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        return False


class FakeOpener:
    """Routes a URL → response and records the requests it served."""

    def __init__(self, routes: dict[str, FakeResponse]):
        self.routes = routes
        self.calls: list[tuple[str, dict]] = []

    def __call__(self, request, timeout=None):
        url = request.full_url
        headers = dict(request.headers)
        self.calls.append((url, headers))
        for prefix, response in self.routes.items():
            if url.startswith(prefix):
                return response
        # Unknown URLs simulate a network/HTTP failure so callers' error
        # paths are exercised rather than the test bombing on a bare
        # ``AssertionError``. Each test that needs a specific URL must
        # register a route for it.
        raise URLError(f"unexpected URL: {url}")


@pytest.fixture
def conn():
    return HonuaConnection(name="t", base_url="https://example.test", api_key="secret")


def _client(conn, routes):
    return HonuaClient(conn, opener=FakeOpener(routes))


# ---------------------------------------------------------------------------
# ping / connectivity
# ---------------------------------------------------------------------------


def test_ping_succeeds_on_200_json(conn):
    routes = {"https://example.test/ogc/features": FakeResponse(b'{"links": []}')}
    client = _client(conn, routes)
    client.ping()


def test_ping_rejects_non_json_body(conn):
    routes = {"https://example.test/ogc/features": FakeResponse(b"<html>oops</html>")}
    client = _client(conn, routes)
    with pytest.raises(HonuaClientError):
        client.ping()


def test_ping_attaches_api_key_header(conn):
    routes = {"https://example.test/ogc/features": FakeResponse(b"{}")}
    opener = FakeOpener(routes)
    client = HonuaClient(conn, opener=opener)
    client.ping()
    _, headers = opener.calls[0]
    assert headers.get("X-api-key") == "secret" or headers.get("X-Api-Key") == "secret"


# ---------------------------------------------------------------------------
# collections
# ---------------------------------------------------------------------------


def test_list_collections_extracts_id_title_bbox_crs(conn):
    body = json.dumps(
        {
            "collections": [
                {
                    "id": "parcels",
                    "title": "Parcels",
                    "extent": {"spatial": {"bbox": [[-100.0, 40.0, -99.0, 41.0]]}},
                    "storageCrs": "http://www.opengis.net/def/crs/EPSG/0/4326",
                },
                {"id": "roads"},
            ]
        }
    ).encode()
    routes = {"https://example.test/ogc/features/collections": FakeResponse(body)}
    client = _client(conn, routes)
    collections = client.list_collections()
    assert [c.collection_id for c in collections] == ["parcels", "roads"]
    parcels = collections[0]
    assert parcels.title == "Parcels"
    assert parcels.bbox == (-100.0, 40.0, -99.0, 41.0)
    assert parcels.crs == "EPSG:4326"


def test_list_collections_handles_empty_payload(conn):
    routes = {"https://example.test/ogc/features/collections": FakeResponse(b"{}")}
    client = _client(conn, routes)
    assert client.list_collections() == []


def test_list_collections_raises_on_garbage(conn):
    routes = {"https://example.test/ogc/features/collections": FakeResponse(b"not json")}
    client = _client(conn, routes)
    with pytest.raises(HonuaClientError):
        client.list_collections()


# ---------------------------------------------------------------------------
# WMS discovery
# ---------------------------------------------------------------------------


_WMS_XML = b"""<?xml version="1.0"?>
<WMS_Capabilities xmlns="http://www.opengis.net/wms" version="1.3.0">
  <Service><Name>WMS</Name></Service>
  <Capability>
    <Layer>
      <Name>root</Name>
      <Title>Root</Title>
      <CRS>EPSG:3857</CRS>
      <Layer>
        <Name>parcels</Name>
        <Title>Parcels Layer</Title>
        <CRS>EPSG:3857</CRS>
      </Layer>
      <Layer>
        <Title>group only - no name, skip me</Title>
      </Layer>
    </Layer>
  </Capability>
</WMS_Capabilities>
"""


def test_parse_wms_capabilities_picks_named_layers():
    layers = parse_wms_capabilities("svc", _WMS_XML)
    names = [layer.layer_name for layer in layers]
    assert "parcels" in names
    assert "root" in names
    parcels = next(layer for layer in layers if layer.layer_name == "parcels")
    assert parcels.title == "Parcels Layer"
    assert parcels.crs == "EPSG:3857"


def test_parse_wms_capabilities_skips_groups_without_name():
    layers = parse_wms_capabilities("svc", _WMS_XML)
    assert all(layer.layer_name for layer in layers)


def test_list_wms_layers_returns_empty_on_missing_endpoint(conn):
    """Honua services that do not enable WMS return 404 — must not crash."""
    routes = {"https://example.test/rest/services": FakeResponse(b'{"services": [{"name": "svc"}]}')}
    client = _client(conn, routes)
    # No route configured for /ogc/services/svc/wms — fake opener will raise,
    # but list_wms_layers swallows HonuaClientError into an empty list.
    assert client.list_wms_layers("nonexistent") == []


# ---------------------------------------------------------------------------
# parsing helpers
# ---------------------------------------------------------------------------


def test_extract_bbox_handles_missing_extent():
    assert _extract_bbox(None) is None
    assert _extract_bbox({}) is None
    assert _extract_bbox({"spatial": {"bbox": []}}) is None


def test_extract_bbox_picks_first_bbox():
    bbox = _extract_bbox(
        {"spatial": {"bbox": [[-1.0, -2.0, 1.0, 2.0], [-10.0, -20.0, 10.0, 20.0]]}}
    )
    assert bbox == (-1.0, -2.0, 1.0, 2.0)


@pytest.mark.parametrize(
    "raw,expected",
    [
        ("EPSG:4326", "EPSG:4326"),
        ("epsg:3857", "EPSG:3857"),
        ("http://www.opengis.net/def/crs/EPSG/0/4326", "EPSG:4326"),
        ("http://www.opengis.net/def/crs/EPSG/3857", "EPSG:3857"),
        ("", "EPSG:4326"),
        ("nonsense", "EPSG:4326"),
    ],
)
def test_extract_storage_crs_handles_urn_and_short_form(raw, expected):
    assert _extract_storage_crs({"storageCrs": raw}) == expected


def test_extract_storage_crs_falls_back_when_missing():
    assert _extract_storage_crs({}) == "EPSG:4326"
