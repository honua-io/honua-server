"""Tests for admin layer publishing and discovery endpoints."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaHttpError
from honua_sdk.admin import (
    ColumnInfo,
    HonuaAdminClient,
    PublishLayerRequest,
    PublishedLayerSummary,
    TableDiscoveryResponse,
    TableInfo,
)
from .conftest import make_api_response


_LAYER_SUMMARY = {
    "layerId": 1,
    "layerName": "parcels",
    "schema": "public",
    "table": "parcels",
    "description": "Parcel boundaries",
    "geometryType": "Polygon",
    "srid": 4326,
    "primaryKey": "gid",
    "fieldCount": 12,
    "enabled": True,
    "serviceName": "default",
}


def test_list_layers(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json=make_api_response([_LAYER_SUMMARY]))

    with make_client(handler) as client:
        result = client.list_layers("conn-001")

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/connections/conn-001/layers"
    assert seen["params"] == {}
    assert len(result) == 1
    assert isinstance(result[0], PublishedLayerSummary)
    assert result[0].layer_id == 1
    assert result[0].layer_name == "parcels"
    assert result[0].geometry_type == "Polygon"
    assert result[0].enabled is True


def test_list_layers_with_service_name(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json=make_api_response([]))

    with make_client(handler) as client:
        result = client.list_layers("conn-001", service_name="staging")

    assert seen["params"]["serviceName"] == "staging"
    assert result == []


def test_publish_layer(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(201, json=make_api_response(_LAYER_SUMMARY))

    req = PublishLayerRequest(
        schema="public",
        table="parcels",
        layer_name="parcels",
        description="Parcel boundaries",
        geometry_column="geom",
        geometry_type="Polygon",
        srid=4326,
        primary_key="gid",
        service_name="default",
        enabled=True,
    )

    with make_client(handler) as client:
        result = client.publish_layer("conn-001", req)

    assert seen["method"] == "POST"
    assert seen["path"] == "/api/v1/admin/connections/conn-001/layers"
    assert seen["body"]["schema"] == "public"
    assert seen["body"]["table"] == "parcels"
    assert seen["body"]["layerName"] == "parcels"
    assert seen["body"]["geometryColumn"] == "geom"
    assert seen["body"]["srid"] == 4326
    assert isinstance(result, PublishedLayerSummary)
    assert result.layer_name == "parcels"


def test_publish_layer_with_fields(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(201, json=make_api_response(_LAYER_SUMMARY))

    req = PublishLayerRequest(
        table="parcels",
        fields_list=["gid", "name", "area"],
    )

    with make_client(handler) as client:
        client.publish_layer("conn-001", req)

    # fields_list should be serialised as "fields" in the API body
    assert seen["body"]["fields"] == ["gid", "name", "area"]
    assert "fieldsList" not in seen["body"]


def test_set_layer_enabled(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(
            200,
            json=make_api_response({**_LAYER_SUMMARY, "enabled": False}),
        )

    with make_client(handler) as client:
        result = client.set_layer_enabled("conn-001", 1, False)

    assert seen["method"] == "PUT"
    assert seen["path"] == "/api/v1/admin/connections/conn-001/layers/1/enabled"
    assert seen["body"]["enabled"] is False
    assert isinstance(result, PublishedLayerSummary)
    assert result.enabled is False


def test_set_layer_enabled_with_service_name(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json=make_api_response(_LAYER_SUMMARY))

    with make_client(handler) as client:
        client.set_layer_enabled("conn-001", 1, True, service_name="staging")

    assert seen["params"]["serviceName"] == "staging"


def test_set_service_layers_enabled(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json=make_api_response([_LAYER_SUMMARY]))

    with make_client(handler) as client:
        result = client.set_service_layers_enabled("conn-001", True)

    assert seen["method"] == "PUT"
    assert seen["path"] == "/api/v1/admin/connections/conn-001/layers/enabled"
    assert seen["body"]["enabled"] is True
    assert len(result) == 1
    assert isinstance(result[0], PublishedLayerSummary)


def test_set_service_layers_enabled_with_service_name(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json=make_api_response([]))

    with make_client(handler) as client:
        client.set_service_layers_enabled("conn-001", False, service_name="staging")

    assert seen["params"]["serviceName"] == "staging"


def test_discover_tables(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        return httpx.Response(
            200,
            json=make_api_response({
                "tables": [
                    {
                        "schema": "public",
                        "table": "parcels",
                        "geometryColumn": "geom",
                        "geometryType": "Polygon",
                        "srid": 4326,
                        "estimatedRows": 50000,
                        "columns": [
                            {
                                "name": "gid",
                                "dataType": "integer",
                                "isNullable": False,
                                "isPrimaryKey": True,
                                "maxLength": None,
                            },
                            {
                                "name": "name",
                                "dataType": "varchar",
                                "isNullable": True,
                                "isPrimaryKey": False,
                                "maxLength": 255,
                            },
                        ],
                    },
                ],
            }),
        )

    with make_client(handler) as client:
        result = client.discover_tables("conn-001")

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/connections/conn-001/tables"
    assert isinstance(result, TableDiscoveryResponse)
    assert len(result.tables) == 1

    tbl = result.tables[0]
    assert isinstance(tbl, TableInfo)
    assert tbl.schema == "public"
    assert tbl.table == "parcels"
    assert tbl.geometry_type == "Polygon"
    assert tbl.srid == 4326
    assert tbl.estimated_rows == 50000
    assert len(tbl.columns) == 2

    col = tbl.columns[0]
    assert isinstance(col, ColumnInfo)
    assert col.name == "gid"
    assert col.is_primary_key is True
    assert col.max_length is None

    col2 = tbl.columns[1]
    assert col2.name == "name"
    assert col2.max_length == 255


def test_publish_layer_400_error(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(400, json={"message": "Invalid layer configuration"})

    req = PublishLayerRequest(table="bad")

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.publish_layer("conn-001", req)

    assert exc_info.value.status_code == 400


def test_discover_tables_404_error(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"message": "Connection not found"})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.discover_tables("nonexistent")

    assert exc_info.value.status_code == 404
