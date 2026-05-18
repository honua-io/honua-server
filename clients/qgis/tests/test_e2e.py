"""End-to-end smoke test inside ``qgis/qgis:ltr``.

Skipped when ``HONUA_BASE_URL`` is unset (i.e. when running on a
developer laptop without docker-compose). The CI / Make ``e2e`` target
sets that env var inside the container.

The test:
1. Pings the Honua OGC API Features landing page through the plugin
   client (proves connectivity + auth header propagation).
2. Discovers the first OGC API Features collection.
3. Builds the QGIS WFS provider URI and instantiates a
   ``QgsVectorLayer`` against it; asserts ``layer.isValid()``.
"""

from __future__ import annotations

import os

import pytest


REQUIRED_ENV = ("HONUA_BASE_URL", "HONUA_API_KEY")


def _env_present() -> bool:
    return all(os.environ.get(name) for name in REQUIRED_ENV)


pytestmark = pytest.mark.skipif(
    not _env_present(),
    reason="set HONUA_BASE_URL and HONUA_API_KEY (run via `make e2e`)",
)


@pytest.fixture(scope="module")
def qgis_app():
    """Boot a headless QGIS application once per module."""
    from qgis.core import QgsApplication

    app = QgsApplication([], False)
    QgsApplication.setPrefixPath("/usr", True)
    app.initQgis()
    yield app
    app.exitQgis()


@pytest.fixture
def connection():
    from honua_qgis.auth import HonuaConnection

    return HonuaConnection(
        name="ci",
        base_url=os.environ["HONUA_BASE_URL"],
        api_key=os.environ["HONUA_API_KEY"],
    )


def test_plugin_pings_reference_server(connection):
    from honua_qgis.client import HonuaClient

    HonuaClient(connection).ping()


def test_plugin_loads_first_collection_into_qgis(qgis_app, connection):
    from qgis.core import QgsVectorLayer

    from honua_qgis.client import HonuaClient
    from honua_qgis.layers import build_wfs_uri

    collections = HonuaClient(connection).list_collections()
    assert collections, "reference Honua server returned no OGC API Features collections"
    target = collections[0]

    uri = build_wfs_uri(connection, target)
    layer = QgsVectorLayer(uri, target.title or target.collection_id, "WFS")
    assert layer.isValid(), f"WFS layer for {target.collection_id} did not become valid"
