from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaClient, HonuaHttpError


def test_query_features_builds_expected_request() -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["query"] = dict(request.url.params.multi_items())
        return httpx.Response(200, json={"features": []})

    transport = httpx.MockTransport(handler)
    with HonuaClient("http://example.test", transport=transport) as client:
        response = client.query_features(
            "default",
            2,
            where="objectid > 10",
            out_fields=["objectid", "name"],
            return_geometry=False,
        )

    assert response == {"features": []}
    assert seen["method"] == "GET"
    assert seen["path"] == "/rest/services/default/FeatureServer/2/query"
    assert seen["query"]["f"] == "json"
    assert seen["query"]["where"] == "objectid > 10"
    assert seen["query"]["outFields"] == "objectid,name"
    assert seen["query"]["returnGeometry"] == "false"


def test_apply_edits_posts_json_payload() -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["payload"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(200, json={"addResults": [{"success": True}]})

    transport = httpx.MockTransport(handler)
    with HonuaClient("http://example.test", transport=transport) as client:
        response = client.apply_edits(
            "default",
            5,
            adds=[{"attributes": {"name": "A"}}],
            deletes=[1, 3],
            rollback_on_failure=True,
        )

    assert response["addResults"][0]["success"] is True
    assert seen["method"] == "POST"
    assert seen["path"] == "/rest/services/default/FeatureServer/5/applyEdits"
    assert seen["payload"]["f"] == "json"
    assert seen["payload"]["rollbackOnFailure"] is True
    assert seen["payload"]["adds"][0]["attributes"]["name"] == "A"
    assert seen["payload"]["deletes"] == [1, 3]


def test_auth_headers_are_attached() -> None:
    seen: dict[str, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["x_api_key"] = request.headers.get("x-api-key", "")
        seen["authorization"] = request.headers.get("authorization", "")
        return httpx.Response(200, json={"status": "ready"})

    transport = httpx.MockTransport(handler)
    with HonuaClient(
        "http://example.test",
        transport=transport,
        api_key="test-key",
        bearer_token="test-token",
    ) as client:
        response = client.readiness()

    assert response["status"] == "ready"
    assert seen["x_api_key"] == "test-key"
    assert seen["authorization"] == "Bearer test-token"


def test_non_success_raises_honua_http_error() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            404,
            json={"error": {"code": 404, "message": "Service not found"}},
        )

    transport = httpx.MockTransport(handler)
    with HonuaClient("http://example.test", transport=transport) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            _ = client.list_services()

    err = exc_info.value
    assert err.status_code == 404
    assert err.message == "Service not found"
    assert isinstance(err.body, dict)
