# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Independent Esri REST identity probes.

These describe the ArcGIS-compatible identity contract: what `/rest/info` must
advertise, how an invalid token must be reported, whether the header the ArcGIS
JS API sends survives a CORS preflight, whether OAuth `userinfo` resolves, and
whether a token in a query string leaks into the request log.

Originally written against the 2026-09-03 Esri bug hunt and expected to fail.
Two of those findings now pass, so the module is no longer a list of known
defects - it is a live contract, and it is wired into the shared harness so it
actually runs. Previously it hardcoded its own base URL and credentials for a
standalone probe stack, which is why it drifted out of every entrypoint: the
string `esri_client_compat` appeared in no other file in the repository.
"""

from __future__ import annotations

import os
from pathlib import Path

import httpx
import pytest

from shared.canonical_fixture import COLLECTION_ID, SERVICE_ID, TOTAL_FEATURES

# The shared harness and the docker/client-compat fixture both provision this
# password (tests/python/shared/server.py, docker/client-compat/compose.yml), so
# the probes authenticate against whichever one is running rather than against a
# stack that no longer exists.
ADMIN_USERNAME = os.getenv("HONUA_ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("HONUA_ADMIN_PASSWORD", "ClientCompatAdmin123!")

# The origin the fixture allows. The assertion below is only meaningful when some
# origin is configured: with no Cors__AllowedOrigins the server correctly omits
# every CORS header, so a preflight returns 204 with nothing to assert on.
PROBE_ORIGIN = os.getenv("HONUA_ESRI_PROBE_ORIGIN", "http://localhost:3000")

QUERY_PATH = f"/rest/services/{SERVICE_ID}/FeatureServer/{COLLECTION_ID}/query"


def _json(base_url: str, path: str, **kwargs: object) -> tuple[httpx.Response, dict]:
    response = httpx.request("GET", f"{base_url.rstrip('/')}{path}", timeout=30, **kwargs)
    return response, response.json()


def _generate_token(base_url: str, **extra: str) -> str:
    """Mint a portal token, failing loudly rather than raising KeyError.

    A wrong password returns HTTP 200 carrying an Esri error envelope with no
    `token` key, so indexing it blind produced `KeyError: 'token'` and looked
    like a server defect.
    """
    payload = {
        "username": ADMIN_USERNAME,
        "password": ADMIN_PASSWORD,
        "client": "requestip",
        "f": "json",
    }
    payload.update(extra)
    response = httpx.post(
        f"{base_url.rstrip('/')}/sharing/rest/generateToken",
        data=payload,
        timeout=30,
    )
    response.raise_for_status()
    body = response.json()
    assert "token" in body, f"generateToken returned no token: {body}"
    return body["token"]


def test_rest_info_advertises_portal_token_service(base_url: str) -> None:
    response, body = _json(base_url, "/rest/info", params={"f": "json"})

    assert response.status_code == 200
    auth_info = body.get("authInfo", {})
    assert auth_info.get("isTokenBasedSecurity") is True
    assert auth_info.get("tokenServicesUrl", "").endswith("/sharing/rest/generateToken")


def test_invalid_portal_token_uses_esri_498_envelope(base_url: str) -> None:
    _, body = _json(
        base_url,
        QUERY_PATH,
        params={"where": "1=1", "f": "json", "token": "not-a-real-token"},
    )

    # Esri convention: HTTP 200 carrying the error in the body, code 498 for an
    # invalid token. The body decides, not the status line.
    assert body.get("error", {}).get("code") == 498


def test_x_esri_authorization_is_allowed_by_cors_preflight(base_url: str) -> None:
    response = httpx.options(
        f"{base_url.rstrip('/')}{QUERY_PATH}",
        headers={
            "Origin": PROBE_ORIGIN,
            "Access-Control-Request-Method": "POST",
            "Access-Control-Request-Headers": "content-type,x-esri-authorization",
        },
        timeout=30,
    )

    assert response.status_code == 204
    allowed = response.headers.get("access-control-allow-headers", "").lower()
    if not allowed:
        pytest.skip(
            f"no CORS headers returned for origin {PROBE_ORIGIN}; set "
            "Cors__AllowedOrigins__0 on the fixture to exercise this contract"
        )
    # The ArcGIS JS API sends its credential in X-Esri-Authorization, so a
    # preflight that omits it from the allow-list blocks every browser client.
    assert "x-esri-authorization" in allowed


def test_oauth_userinfo_is_served(base_url: str) -> None:
    token = _generate_token(base_url)
    response, body = _json(
        base_url,
        "/sharing/rest/oauth2/userinfo",
        params={"f": "json", "token": token},
    )

    assert response.status_code == 200
    assert body.get("error", {}).get("code") != 404
    assert body.get("sub") or body.get("username")


def test_services_directory_does_not_duplicate_name_and_type(base_url: str) -> None:
    token = _generate_token(base_url, expiration="60")
    response, body = _json(
        base_url,
        "/rest/services",
        params={"f": "json", "token": token},
    )

    assert response.status_code == 200
    # The canonical graph projects one logical service across several protocol
    # facets that share a display name, so the directory must dedupe on
    # (name, type) or a client sees the same service listed repeatedly.
    entries = [(service["name"], service["type"]) for service in body["services"]]
    assert len(entries) == len(set(entries)), f"duplicate name+type entries: {entries}"


def test_arcgis_python_username_password_login_and_feature_query(base_url: str) -> None:
    pytest.importorskip("arcgis")
    from arcgis.features import FeatureLayer
    from arcgis.gis import GIS

    gis = GIS(
        base_url.rstrip("/"),
        username=ADMIN_USERNAME,
        password=ADMIN_PASSWORD,
        verify_cert=False,
    )
    layer = FeatureLayer(
        f"{base_url.rstrip('/')}/rest/services/{SERVICE_ID}/FeatureServer/{COLLECTION_ID}",
        gis=gis,
    )
    result = layer.query(where="1=1", out_fields="*", return_geometry=False)

    assert len(result.features) == TOTAL_FEATURES


def test_query_token_is_not_written_to_request_log(base_url: str) -> None:
    log_path = os.getenv("HONUA_ESRI_LOG_FILE")
    if not log_path:
        pytest.skip("set HONUA_ESRI_LOG_FILE to the captured server log")

    marker = "ESRI_PROBE_LOG_MARKER"
    _json(
        base_url,
        QUERY_PATH,
        params={
            "where": "1=1",
            "outFields": "*",
            "returnGeometry": "false",
            "token": marker,
            "f": "json",
        },
    )
    assert marker not in Path(log_path).read_text(encoding="utf-8")
