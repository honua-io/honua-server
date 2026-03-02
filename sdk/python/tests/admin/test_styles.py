"""Tests for admin layer style endpoints."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaHttpError
from honua_sdk.admin import (
    HonuaAdminClient,
    LayerStyleResponse,
    LayerStyleUpdateRequest,
)
from .conftest import make_api_response


_STYLE_DATA = {
    "mapLibreStyle": {
        "version": 8,
        "layers": [
            {
                "id": "parcels-fill",
                "type": "fill",
                "paint": {"fill-color": "#3388ff", "fill-opacity": 0.4},
            },
        ],
    },
    "drawingInfo": {
        "renderer": {
            "type": "simple",
            "symbol": {"color": [51, 136, 255, 102]},
        },
    },
}


def test_get_layer_style(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        return httpx.Response(200, json=make_api_response(_STYLE_DATA))

    with make_client(handler) as client:
        result = client.get_layer_style(42)

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/metadata/layers/42/style"
    assert isinstance(result, LayerStyleResponse)
    assert result.map_libre_style is not None
    assert result.map_libre_style["version"] == 8
    assert result.drawing_info is not None
    assert result.drawing_info["renderer"]["type"] == "simple"


def test_get_layer_style_both_none(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json=make_api_response({"mapLibreStyle": None, "drawingInfo": None}),
        )

    with make_client(handler) as client:
        result = client.get_layer_style(99)

    assert isinstance(result, LayerStyleResponse)
    assert result.map_libre_style is None
    assert result.drawing_info is None


def test_update_layer_style(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json=make_api_response(_STYLE_DATA))

    new_style = {
        "version": 8,
        "layers": [
            {
                "id": "parcels-fill",
                "type": "fill",
                "paint": {"fill-color": "#ff0000", "fill-opacity": 0.8},
            },
        ],
    }
    req = LayerStyleUpdateRequest(map_libre_style=new_style)

    with make_client(handler) as client:
        result = client.update_layer_style(42, req)

    assert seen["method"] == "PUT"
    assert seen["path"] == "/api/v1/admin/metadata/layers/42/style"
    assert seen["body"]["mapLibreStyle"]["version"] == 8
    assert "drawingInfo" not in seen["body"]
    assert isinstance(result, LayerStyleResponse)


def test_update_layer_style_drawing_info_only(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json=make_api_response(_STYLE_DATA))

    drawing = {"renderer": {"type": "simple", "symbol": {"color": [255, 0, 0, 255]}}}
    req = LayerStyleUpdateRequest(drawing_info=drawing)

    with make_client(handler) as client:
        client.update_layer_style(42, req)

    assert "mapLibreStyle" not in seen["body"]
    assert seen["body"]["drawingInfo"]["renderer"]["type"] == "simple"


def test_update_layer_style_both_fields(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json=make_api_response(_STYLE_DATA))

    req = LayerStyleUpdateRequest(
        map_libre_style={"version": 8, "layers": []},
        drawing_info={"renderer": {"type": "simple"}},
    )

    with make_client(handler) as client:
        client.update_layer_style(42, req)

    assert "mapLibreStyle" in seen["body"]
    assert "drawingInfo" in seen["body"]


def test_get_layer_style_404(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"message": "Layer not found"})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.get_layer_style(9999)

    assert exc_info.value.status_code == 404


def test_update_layer_style_400(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(400, json={"message": "Invalid style data"})

    req = LayerStyleUpdateRequest(map_libre_style={"invalid": True})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.update_layer_style(42, req)

    assert exc_info.value.status_code == 400
