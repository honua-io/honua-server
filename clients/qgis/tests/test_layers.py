"""Unit tests for ``honua_qgis.layers`` URI builders."""

from __future__ import annotations

from urllib.parse import parse_qs, unquote, urlparse

from honua_qgis.auth import CollectionEntry, HonuaConnection, WmsLayerEntry
from honua_qgis.layers import build_wfs_uri, build_wms_uri, display_label


def _conn(api_key: str = "secret") -> HonuaConnection:
    return HonuaConnection(name="local", base_url="https://example.test/", api_key=api_key)


def test_build_wfs_uri_includes_collection_items_url_and_typename():
    collection = CollectionEntry(collection_id="parcels", title="Parcels", crs="EPSG:4326")
    uri = build_wfs_uri(_conn(), collection)
    parts = dict(item.split("=", 1) for item in uri.split(" "))
    assert "url" in parts
    decoded_url = unquote(parts["url"])
    assert decoded_url.startswith("https://example.test/ogc/features/collections/parcels/items")
    assert "apikey=secret" in decoded_url
    assert parts["typename"] == "parcels"
    assert parts["version"] == "auto"
    assert parts["srsname"] == "EPSG%3A4326"


def test_build_wfs_uri_omits_api_key_query_when_anonymous():
    collection = CollectionEntry(collection_id="parcels", title="Parcels")
    uri = build_wfs_uri(_conn(api_key=""), collection)
    parts = dict(item.split("=", 1) for item in uri.split(" "))
    assert "apikey=" not in unquote(parts["url"])


def test_build_wms_uri_includes_required_keys_and_api_key():
    layer = WmsLayerEntry(service_id="svc", layer_name="basemap", title="Basemap", crs="EPSG:3857")
    uri = build_wms_uri(_conn(), layer)
    pairs = dict(part.split("=", 1) for part in uri.split("&") if "=" in part)
    assert pairs["format"] == "image/png"
    assert pairs["layers"] == "basemap"
    assert pairs["crs"] == "EPSG:3857"
    assert pairs["version"] == "1.3.0"
    decoded_endpoint = unquote(pairs["url"])
    assert decoded_endpoint.startswith("https://example.test/ogc/services/svc/wms")
    assert "apikey=secret" in decoded_endpoint


def test_build_wms_uri_handles_anonymous_connection():
    layer = WmsLayerEntry(service_id="svc", layer_name="basemap", title="Basemap")
    uri = build_wms_uri(_conn(api_key=""), layer)
    pairs = dict(part.split("=", 1) for part in uri.split("&") if "=" in part)
    assert "apikey=" not in unquote(pairs["url"])


def test_build_wms_uri_escapes_ampersands_inside_values():
    layer = WmsLayerEntry(
        service_id="svc/root",
        layer_name="base&map",
        title="Basemap",
    )
    uri = build_wms_uri(_conn(api_key="a&b"), layer)
    pairs = dict(part.split("=", 1) for part in uri.split("&") if "=" in part)
    assert unquote(pairs["url"]) == "https://example.test/ogc/services/svc/root/wms?apikey=a&b"
    assert unquote(pairs["layers"]) == "base&map"


def test_display_label_combines_title_and_identifier():
    assert display_label(kind="vector", title="Parcels", identifier="parcels") == "Parcels (parcels)"
    assert display_label(kind="vector", title="parcels", identifier="parcels") == "parcels"
    assert display_label(kind="raster", title="", identifier="svc/foo") == "svc/foo"
