# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Authenticated parity seed for the GeoPandas analyst read suites."""

from __future__ import annotations

import contextlib
import os
import tempfile
import xml.etree.ElementTree as ET

import geopandas
import httpx
import pyogrio
import pytest

from shared import canonical_fixture as fixture
from shared.cert_auth import (
    AUTHENTICATED_MODES,
    NEGATIVE_MODES,
    AuthCredentials,
    AuthMode,
)

from .conftest import wfs_capabilities_url

pytestmark = pytest.mark.geopandas_client

# Later breadth waves extend this list without cloning the auth-mode harness.
CORE_READ_SUITES = ("ogc-features", "wfs")


@pytest.fixture(scope="session")
def cert_credentials() -> AuthCredentials:
    return AuthCredentials.from_environment()


@contextlib.contextmanager
def _gdal_auth(headers: dict[str, str]):
    # OAPIF supplies its own HEADERS request option, overriding GDAL_HTTP_HEADERS.
    # The header-file option is applied by GDAL after those driver headers.
    # Keep it alive through every request and restore the caller's configuration.
    previous = pyogrio.get_gdal_config_option("GDAL_HTTP_HEADER_FILE")
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8") as header_file:
        for name, value in headers.items():
            header_file.write(f"{name}: {value}\n")
        header_file.flush()
        pyogrio.set_gdal_config_options({"GDAL_HTTP_HEADER_FILE": header_file.name})
        try:
            yield
        finally:
            pyogrio.set_gdal_config_options({"GDAL_HTTP_HEADER_FILE": previous})


def _semantics(frame: geopandas.GeoDataFrame) -> list[tuple[str, str, str]]:
    rows: list[tuple[str, str, str]] = []
    for _, row in frame.iterrows():
        properties = {
            name: str(row[name])
            for name in frame.columns
            if name != frame.geometry.name and name.lower() not in {"id", "objectid", "gml_id"}
        }
        rows.append((str(properties.get("name")), str(sorted(properties.items())), row.geometry.wkt if row.geometry is not None else "NULL"))
    return sorted(rows)


@pytest.mark.parametrize("auth_mode", AUTHENTICATED_MODES, ids=lambda mode: mode.value)
@pytest.mark.parametrize("suite", CORE_READ_SUITES)
@pytest.mark.cert("CERT-AUTH-01")
def test_core_analyst_read_authenticated_matches_anonymous_baseline(
    suite: str,
    auth_mode: AuthMode,
    base_url: str,
    geopandas_collection_id: str,
    wfs_typename: str,
    cert_credentials: AuthCredentials,
) -> None:
    """GeoPandas/GDAL returns identical data through both auth schemes."""
    headers = cert_credentials.headers(auth_mode)
    if suite == "ogc-features":
        dsn = f"OAPIF:{base_url}/ogc/features"
        baseline = pyogrio.read_dataframe(dsn, layer=geopandas_collection_id, use_arrow=True)
        protected_id = os.getenv("HONUA_CERT_VECTOR_COLLECTION_ID", "2011")
        with _gdal_auth(headers):
            actual = pyogrio.read_dataframe(dsn, layer=protected_id, use_arrow=True)
    else:
        dsn = f"WFS:{wfs_capabilities_url(base_url)}"
        public_layers = {str(entry[0]) for entry in pyogrio.list_layers(dsn)}
        assert wfs_typename in public_layers
        baseline = pyogrio.read_dataframe(dsn, layer=wfs_typename, use_arrow=True)
        with _gdal_auth(headers):
            authenticated_layers = {str(entry[0]) for entry in pyogrio.list_layers(dsn)}
            protected = {
                name for name in authenticated_layers - public_layers
                if "authenticated_test_layer" in name.lower()
            }
            assert len(protected) == 1, {
                "public": sorted(public_layers),
                "authenticated": sorted(authenticated_layers),
            }
            protected_type = protected.pop()
            actual = pyogrio.read_dataframe(dsn, layer=protected_type, use_arrow=True)
        # GML IDs include the distinct mirror layer name and allocated object ID.
        # Validate both wire identities separately; parity uses canonical uid.
        for frame, type_name in ((baseline, wfs_typename), (actual, protected_type)):
            assert frame["gml_id"].is_unique
            assert frame["gml_id"].tolist() == [
                f"{type_name.split(':')[-1]}.{object_id}"
                for object_id in frame["objectid"]
            ]

    assert len(actual) == fixture.TOTAL_FEATURES
    assert _semantics(actual) == _semantics(baseline)


@pytest.mark.parametrize("credential", NEGATIVE_MODES)
@pytest.mark.parametrize("suite", CORE_READ_SUITES)
@pytest.mark.cert("CERT-AUTH-02")
def test_core_analyst_read_invalid_credential_returns_protocol_challenge(
    suite: str,
    credential: str,
    base_url: str,
    cert_credentials: AuthCredentials,
) -> None:
    """Negative credentials expose the status and wire error GDAL receives."""
    headers = cert_credentials.negative_headers(credential)
    if suite == "ogc-features":
        protected_id = os.getenv("HONUA_CERT_VECTOR_COLLECTION_ID", "2011")
        url = f"{base_url}/ogc/features/collections/{protected_id}/items?limit=1"
    else:
        valid_headers = cert_credentials.headers(AuthMode.API_KEY)
        capabilities = httpx.get(wfs_capabilities_url(base_url), headers=valid_headers, timeout=30.0)
        capabilities.raise_for_status()
        root = ET.fromstring(capabilities.content)
        type_names = [
            element.text.strip()
            for element in root.iter()
            if element.tag.endswith("Name") and element.text
        ]
        matches = [name for name in type_names if name.split(":")[-1] == "authenticated_test_layer"]
        assert len(matches) == 1, type_names
        url = (
            f"{base_url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature"
            f"&TYPENAMES={matches[0]}&COUNT=1"
        )

    response = httpx.get(url, headers=headers, timeout=30.0)
    assert response.status_code == 401, response.text[:500]
    challenge = response.headers.get("WWW-Authenticate", "")
    expected_scheme = "Bearer" if credential == "expired-oidc-bearer" else "ApiKey"
    assert expected_scheme.lower() in challenge.lower(), challenge
    if suite == "ogc-features":
        assert response.headers["Content-Type"].startswith("application/problem+json")
        assert response.json()["status"] == 401
    else:
        assert "xml" in response.headers["Content-Type"].lower()
        assert "AccessDenied" in response.text and "Exception" in response.text
