"""Independent Esri REST identity probes.

These tests intentionally describe the ArcGIS-compatible contract.  They are
expected to fail on the current server for the findings recorded by the
2026-09-03 Esri bug hunt.
"""

from __future__ import annotations

import os
from pathlib import Path

import httpx
import pytest


BASE_URL = os.getenv("HONUA_ESRI_PROBE_URL", "http://127.0.0.1:5555").rstrip("/")


def _json(path: str, **kwargs: object) -> tuple[httpx.Response, dict]:
    response = httpx.request("GET", f"{BASE_URL}{path}", timeout=30, **kwargs)
    return response, response.json()


def test_rest_info_advertises_portal_token_service() -> None:
    response, body = _json("/rest/info", params={"f": "json"})

    assert response.status_code == 200
    auth_info = body.get("authInfo", {})
    assert auth_info.get("isTokenBasedSecurity") is True
    assert auth_info.get("tokenServicesUrl", "").endswith("/sharing/rest/generateToken")


def test_invalid_portal_token_uses_esri_498_envelope() -> None:
    response, body = _json(
        "/rest/services/admin_sample/FeatureServer/3000/query",
        params={
            "where": "1=1",
            "outFields": "*",
            "returnGeometry": "false",
            "resultRecordCount": "991",
            "token": "esri-probe-invalid-token",
            "f": "json",
        },
    )

    assert response.status_code == 200
    assert body.get("error", {}).get("code") == 498


def test_x_esri_authorization_is_allowed_by_cors_preflight() -> None:
    response = httpx.options(
        f"{BASE_URL}/rest/services/admin_sample/FeatureServer/3000/query",
        headers={
            "Origin": "http://localhost:3000",
            "Access-Control-Request-Method": "POST",
            "Access-Control-Request-Headers": "content-type,x-esri-authorization",
        },
        timeout=30,
    )

    assert response.status_code == 204
    allowed = response.headers.get("access-control-allow-headers", "").lower()
    assert "x-esri-authorization" in allowed


def test_oauth_userinfo_is_served() -> None:
    response, body = _json("/sharing/rest/oauth2/userinfo", params={"f": "json"})

    assert response.status_code == 200
    assert body.get("error", {}).get("code") != 404
    assert body.get("sub") or body.get("username")


def test_services_directory_does_not_duplicate_name_and_type() -> None:
    token_response = httpx.post(
        f"{BASE_URL}/sharing/rest/generateToken",
        data={
            "username": "admin",
            "password": "EsriProbeAdmin123!",
            "client": "requestip",
            "expiration": "60",
            "f": "json",
        },
        timeout=30,
    )
    token_response.raise_for_status()
    token = token_response.json()["token"]
    response, body = _json(
        "/rest/services",
        params={"f": "json", "token": token, "resultRecordCount": "994"},
    )

    assert response.status_code == 200
    entries = [(service["name"], service["type"]) for service in body["services"]]
    assert len(entries) == len(set(entries))


def test_arcgis_python_username_password_login_and_feature_query() -> None:
    arcgis = pytest.importorskip("arcgis")
    from arcgis.features import FeatureLayer
    from arcgis.gis import GIS

    base = os.getenv("HONUA_ESRI_PYTHON_URL", "https://127.0.0.1:5557").rstrip("/")
    gis = GIS(
        base,
        username="admin",
        password="EsriProbeAdmin123!",
        verify_cert=False,
    )
    layer = FeatureLayer(
        f"{base}/rest/services/admin_sample/FeatureServer/3000",
        gis=gis,
    )
    result = layer.query(where="1=1", out_fields="*", return_geometry=False)

    assert len(result.features) == 4


def test_query_token_is_not_written_to_request_log() -> None:
    log_path = os.getenv("HONUA_ESRI_LOG_FILE")
    if not log_path:
        pytest.skip("set HONUA_ESRI_LOG_FILE to the captured server log")

    marker = "ESRI_PROBE_LOG_MARKER"
    _json(
        "/rest/services/admin_sample/FeatureServer/3000/query",
        params={
            "where": "1=1",
            "outFields": "*",
            "returnGeometry": "false",
            "resultRecordCount": "992",
            "token": marker,
            "f": "json",
        },
    )
    assert marker not in Path(log_path).read_text(encoding="utf-8")
