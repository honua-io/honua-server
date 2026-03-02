"""Tests for admin metadata resource endpoints."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaHttpError
from honua_sdk.admin import (
    HonuaAdminClient,
    MetadataResource,
    ResourceMetadata,
)
from .conftest import make_api_response


_RESOURCE_DATA = {
    "apiVersion": "v1",
    "kind": "Layer",
    "metadata": {
        "id": "res-001",
        "name": "parcels",
        "namespace": "default",
        "labels": {"env": "prod"},
        "annotations": {},
        "resourceVersion": "5",
        "generation": 2,
        "createdAt": "2026-01-01T00:00:00Z",
        "updatedAt": "2026-02-01T00:00:00Z",
    },
    "spec": {"tableName": "parcels", "schema": "public"},
    "status": {"phase": "Active"},
}


def _make_resource() -> MetadataResource:
    """Build a MetadataResource for use in request tests."""
    return MetadataResource(
        api_version="v1",
        kind="Layer",
        metadata=ResourceMetadata(
            id=None,
            name="parcels",
            namespace="default",
            labels={"env": "prod"},
            annotations={},
            resource_version=None,
            generation=None,
            created_at=None,
            updated_at=None,
        ),
        spec={"tableName": "parcels", "schema": "public"},
        status=None,
    )


def test_list_metadata_resources(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json=make_api_response([_RESOURCE_DATA]))

    with make_client(handler) as client:
        result = client.list_metadata_resources(kind="Layer", namespace="default")

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/metadata/resources"
    assert seen["params"]["kind"] == "Layer"
    assert seen["params"]["namespace"] == "default"
    assert len(result) == 1
    assert isinstance(result[0], MetadataResource)
    assert result[0].kind == "Layer"
    assert result[0].metadata.name == "parcels"


def test_list_metadata_resources_no_filters(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert not request.url.params
        return httpx.Response(200, json=make_api_response([]))

    with make_client(handler) as client:
        result = client.list_metadata_resources()

    assert result == []


def test_get_metadata_resource_returns_etag(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["path"] = request.url.path
        return httpx.Response(
            200,
            json=make_api_response(_RESOURCE_DATA),
            headers={"ETag": '"abc123"'},
        )

    with make_client(handler) as client:
        resource, etag = client.get_metadata_resource("Layer", "default", "parcels")

    assert seen["path"] == "/api/v1/admin/metadata/resources/Layer/default/parcels"
    assert isinstance(resource, MetadataResource)
    assert resource.api_version == "v1"
    assert resource.metadata.labels == {"env": "prod"}
    assert etag == '"abc123"'


def test_get_metadata_resource_no_etag(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=make_api_response(_RESOURCE_DATA))

    with make_client(handler) as client:
        resource, etag = client.get_metadata_resource("Layer", "default", "parcels")

    assert isinstance(resource, MetadataResource)
    assert etag is None


def test_create_metadata_resource_sends_body(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(201, json=make_api_response(_RESOURCE_DATA))

    res = _make_resource()

    with make_client(handler) as client:
        result = client.create_metadata_resource(res)

    assert seen["method"] == "POST"
    assert seen["path"] == "/api/v1/admin/metadata/resources"
    assert seen["body"]["apiVersion"] == "v1"
    assert seen["body"]["kind"] == "Layer"
    assert seen["body"]["metadata"]["name"] == "parcels"
    assert isinstance(result, MetadataResource)


def test_update_metadata_resource_with_if_match(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["if_match"] = request.headers.get("if-match")
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json=make_api_response(_RESOURCE_DATA))

    res = _make_resource()

    with make_client(handler) as client:
        result = client.update_metadata_resource(
            "Layer",
            "default",
            "parcels",
            res,
            if_match='"abc123"',
        )

    assert seen["method"] == "PUT"
    assert seen["path"] == "/api/v1/admin/metadata/resources/Layer/default/parcels"
    assert seen["if_match"] == '"abc123"'
    assert isinstance(result, MetadataResource)


def test_update_metadata_resource_without_if_match(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["if_match"] = request.headers.get("if-match")
        return httpx.Response(200, json=make_api_response(_RESOURCE_DATA))

    res = _make_resource()

    with make_client(handler) as client:
        client.update_metadata_resource("Layer", "default", "parcels", res)

    assert seen["if_match"] is None


def test_delete_metadata_resource_with_if_match(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["if_match"] = request.headers.get("if-match")
        return httpx.Response(204)

    with make_client(handler) as client:
        client.delete_metadata_resource("Layer", "default", "parcels", if_match='"abc123"')

    assert seen["method"] == "DELETE"
    assert seen["path"] == "/api/v1/admin/metadata/resources/Layer/default/parcels"
    assert seen["if_match"] == '"abc123"'


def test_delete_metadata_resource_without_if_match(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["if_match"] = request.headers.get("if-match")
        return httpx.Response(204)

    with make_client(handler) as client:
        client.delete_metadata_resource("Layer", "default", "parcels")

    assert seen["method"] == "DELETE"
    assert seen["if_match"] is None


def test_etag_round_trip(make_client) -> None:
    """Verify that an ETag fetched with GET is properly sent back as If-Match on PUT."""
    call_count = {"n": 0}
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        call_count["n"] += 1
        if request.method == "GET":
            return httpx.Response(
                200,
                json=make_api_response(_RESOURCE_DATA),
                headers={"ETag": '"version-7"'},
            )
        # PUT
        seen["if_match"] = request.headers.get("if-match")
        return httpx.Response(200, json=make_api_response(_RESOURCE_DATA))

    with make_client(handler) as client:
        resource, etag = client.get_metadata_resource("Layer", "default", "parcels")
        assert etag == '"version-7"'

        client.update_metadata_resource(
            "Layer",
            "default",
            "parcels",
            resource,
            if_match=etag,
        )

    assert seen["if_match"] == '"version-7"'
    assert call_count["n"] == 2


def test_get_metadata_resource_404(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"message": "Resource not found"})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.get_metadata_resource("Layer", "default", "missing")

    assert exc_info.value.status_code == 404


def test_update_metadata_resource_412_precondition_failed(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(412, json={"message": "ETag mismatch"})

    res = _make_resource()

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.update_metadata_resource(
                "Layer",
                "default",
                "parcels",
                res,
                if_match='"stale"',
            )

    assert exc_info.value.status_code == 412


def test_update_metadata_resource_428_precondition_required(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(428, json={"message": "If-Match header required"})

    res = _make_resource()

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.update_metadata_resource("Layer", "default", "parcels", res)

    assert exc_info.value.status_code == 428
