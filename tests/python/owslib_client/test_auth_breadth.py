# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Authenticated parity seed for the core OWSLib read suites."""

from __future__ import annotations

import hashlib
import io
import json
import os
from contextlib import contextmanager

import pytest
from owslib.ogcapi.features import Features
from owslib.ogcapi import REQUEST_HEADERS
from owslib.util import http_get
from owslib.wfs import WebFeatureService
from owslib.wms import WebMapService
from PIL import Image

from shared import canonical_fixture as fixture
from shared.cert_auth import (
    AUTHENTICATED_MODES,
    NEGATIVE_MODES,
    AuthCredentials,
    AuthMode,
)

from .conftest import LaneConfig
from .test_wms import VIEW_BBOX

pytestmark = pytest.mark.owslib_client

# Extending later waves is a list change: each entry names the protocol and
# protected target while the shared mode matrix and parity assertions stay put.
CORE_READ_SUITES = ("ogc-features", "wfs", "wms")


@pytest.fixture(scope="session")
def cert_credentials() -> AuthCredentials:
    return AuthCredentials.from_environment()


def _feature_semantics(payload: dict) -> list[tuple[str, str, str]]:
    return sorted(
        (
            str(feature["properties"].get("name")),
            json.dumps(
                {
                    name: value
                    for name, value in feature["properties"].items()
                    if name.lower() not in {"id", "objectid"}
                },
                sort_keys=True,
                default=str,
            ),
            json.dumps(feature.get("geometry"), sort_keys=True),
        )
        for feature in payload["features"]
    )


def _wfs_typename(client: WebFeatureService, title: str) -> str:
    matches = [name for name, feature_type in client.contents.items() if feature_type.title == title]
    assert len(matches) == 1, {
        "expected_title": title,
        "advertised": {name: value.title for name, value in client.contents.items()},
    }
    return matches[0]


def _image_digest(payload: bytes) -> str:
    image = Image.open(io.BytesIO(payload)).convert("RGBA")
    return hashlib.sha256(image.tobytes()).hexdigest()


@contextmanager
def _owslib_headers(headers: dict[str, str]):
    """Contain OWSLib's process-global OGC API request-header mutation."""
    original = dict(REQUEST_HEADERS)
    try:
        yield headers
    finally:
        REQUEST_HEADERS.clear()
        REQUEST_HEADERS.update(original)


@pytest.mark.parametrize("auth_mode", AUTHENTICATED_MODES, ids=lambda mode: mode.value)
@pytest.mark.parametrize("suite", CORE_READ_SUITES)
def test_core_read_suite_authenticated_matches_anonymous_baseline(
    suite: str,
    auth_mode: AuthMode,
    lane_config: LaneConfig,
    cert_credentials: AuthCredentials,
) -> None:
    """API-key and OIDC reads must be semantically equal to public baselines."""
    headers = cert_credentials.headers(auth_mode)

    if suite == "ogc-features":
        anonymous = Features(lane_config.oaf_url)
        baseline = anonymous.collection_items(
            lane_config.collection_id, limit=fixture.TOTAL_FEATURES
        )
        protected_id = os.getenv("HONUA_CERT_VECTOR_COLLECTION_ID", "10")
        with _owslib_headers(headers):
            authenticated = Features(lane_config.oaf_url, headers=headers)
            actual = authenticated.collection_items(protected_id, limit=fixture.TOTAL_FEATURES)
        assert _feature_semantics(actual) == _feature_semantics(baseline)
        return

    if suite == "wfs":
        anonymous = WebFeatureService(lane_config.wfs_url, version="2.0.0")
        public_title = Features(lane_config.oaf_url).collection(lane_config.collection_id)["title"]
        protected_id = os.getenv("HONUA_CERT_VECTOR_COLLECTION_ID", "10")
        baseline = json.loads(
            anonymous.getfeature(
                typename=[_wfs_typename(anonymous, public_title)],
                outputFormat="application/json",
                maxfeatures=fixture.TOTAL_FEATURES,
            ).read()
        )
        with _owslib_headers(headers):
            authenticated = WebFeatureService(
                lane_config.wfs_url, version="2.0.0", headers=headers
            )
            protected_types = {
                name for name in set(authenticated.contents) - set(anonymous.contents)
                if "authenticated_test_layer" in name.lower()
            }
            assert len(protected_types) == 1, protected_types
            actual = json.loads(
                authenticated.getfeature(
                    typename=[protected_types.pop()],
                    outputFormat="application/json",
                    maxfeatures=fixture.TOTAL_FEATURES,
                ).read()
            )
        assert _feature_semantics(actual) == _feature_semantics(baseline)
        return

    anonymous = WebMapService(lane_config.wms_url, version="1.3.0")
    protected_url = (
        f"{lane_config.base_url}/rest/services/"
        f"{os.getenv('HONUA_CERT_RASTER_SERVICE_ID', 'cert_auth_raster')}/MapServer/WMS"
    )
    baseline_layer = next(iter(anonymous.contents))
    protected_layer = os.getenv("HONUA_CERT_RASTER_LAYER_ID", "2010")
    request = {
        "srs": "CRS:84",
        "bbox": VIEW_BBOX,
        "size": (128, 128),
        "format": "image/png",
        "transparent": True,
    }
    baseline = anonymous.getmap(layers=[baseline_layer], **request).read()
    with _owslib_headers(headers):
        authenticated = WebMapService(protected_url, version="1.3.0", headers=headers)
        if protected_layer not in authenticated.contents:
            protected_layer = next(iter(authenticated.contents))
        actual = authenticated.getmap(layers=[protected_layer], **request).read()
    assert _image_digest(actual) == _image_digest(baseline)


@pytest.mark.parametrize("credential", NEGATIVE_MODES)
@pytest.mark.parametrize("suite", CORE_READ_SUITES)
def test_core_read_suite_invalid_credential_returns_protocol_challenge(
    suite: str,
    credential: str,
    lane_config: LaneConfig,
    cert_credentials: AuthCredentials,
) -> None:
    """Wrong and expired credentials fail with protocol-shaped 401 challenges."""
    headers = cert_credentials.negative_headers(credential)
    if suite == "ogc-features":
        protected_id = os.getenv("HONUA_CERT_VECTOR_COLLECTION_ID", "10")
        url = f"{lane_config.oaf_url}/collections/{protected_id}/items?limit=1"
    elif suite == "wfs":
        valid_headers = cert_credentials.headers(AuthMode.API_KEY)
        anonymous = WebFeatureService(lane_config.wfs_url, version="2.0.0")
        with _owslib_headers(valid_headers):
            client = WebFeatureService(lane_config.wfs_url, version="2.0.0", headers=valid_headers)
            protected_types = {
                name for name in set(client.contents) - set(anonymous.contents)
                if "authenticated_test_layer" in name.lower()
            }
            assert len(protected_types) == 1, protected_types
            typename = protected_types.pop()
        url = (
            f"{lane_config.wfs_url}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"
            f"&TYPENAMES={typename}&COUNT=1"
        )
    else:
        service = os.getenv("HONUA_CERT_RASTER_SERVICE_ID", "cert_auth_raster")
        url = (
            f"{lane_config.base_url}/rest/services/{service}/MapServer/WMS"
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetCapabilities"
        )

    response = http_get(url, headers=headers, timeout=30)
    expected_status = 200 if suite == "wms" else 401
    assert response.status_code == expected_status, response.text[:500]
    challenge = response.headers.get("WWW-Authenticate", "")
    expected_scheme = "Bearer" if credential == "expired-oidc-bearer" else "ApiKey"
    assert expected_scheme.lower() in challenge.lower(), challenge

    content_type = response.headers.get("Content-Type", "").lower()
    if suite == "ogc-features":
        assert "json" in content_type
        problem = response.json()
        assert problem.get("status") == 401
        assert problem.get("title")
    else:
        assert "xml" in content_type
        assert b"Exception" in response.content and b"AccessDenied" in response.content
