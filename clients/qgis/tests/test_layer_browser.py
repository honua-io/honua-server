"""Unit tests for ``honua_qgis.layer_browser``.

The Qt dock widget itself is exercised inside QGIS (covered by the Docker
end-to-end test); these tests focus on the view-model that converts a
``DiscoveryResult`` into the rows the tree displays.
"""

from __future__ import annotations

from honua_qgis.auth import (
    CollectionEntry,
    DiscoveryResult,
    HonuaConnection,
    WmsLayerEntry,
)
from honua_qgis.layer_browser import flatten_for_view


def _conn():
    return HonuaConnection(name="srv", base_url="https://example.test", api_key="abc")


def test_flatten_for_view_orders_vector_before_raster():
    conn = _conn()
    discovery = DiscoveryResult(
        collections=[
            CollectionEntry(collection_id="parcels", title="Parcels"),
            CollectionEntry(collection_id="roads", title="Roads"),
        ],
        wms_layers=[
            WmsLayerEntry(service_id="svc", layer_name="basemap", title="Basemap"),
        ],
    )
    rows = flatten_for_view(conn, discovery)
    assert [row.kind for row in rows] == ["vector", "vector", "raster"]
    assert [row.identifier for row in rows] == ["parcels", "roads", "svc:basemap"]


def test_flatten_for_view_marks_provider_per_kind():
    conn = _conn()
    discovery = DiscoveryResult(
        collections=[CollectionEntry(collection_id="parcels", title="Parcels")],
        wms_layers=[WmsLayerEntry(service_id="svc", layer_name="basemap", title="Basemap")],
    )
    rows = flatten_for_view(conn, discovery)
    by_kind = {row.kind: row for row in rows}
    assert by_kind["vector"].provider == "WFS"
    assert by_kind["raster"].provider == "wms"


def test_flatten_for_view_carries_connection_name():
    conn = _conn()
    discovery = DiscoveryResult(
        collections=[CollectionEntry(collection_id="parcels", title="Parcels")],
    )
    rows = flatten_for_view(conn, discovery)
    assert rows[0].connection_name == "srv"


def test_flatten_for_view_returns_empty_for_empty_discovery():
    rows = flatten_for_view(_conn(), DiscoveryResult())
    assert rows == []
