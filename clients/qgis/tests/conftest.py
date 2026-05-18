"""Shared test fixtures.

Adds the plugin sources to ``sys.path`` so tests can import ``honua_qgis``
without an editable install (the plugin ships as a flat directory
shipped to QGIS's Python plugin folder).
"""

from __future__ import annotations

import os
import sys

import pytest


_HERE = os.path.dirname(__file__)
_PLUGIN_ROOT = os.path.abspath(os.path.join(_HERE, ".."))
if _PLUGIN_ROOT not in sys.path:
    sys.path.insert(0, _PLUGIN_ROOT)


@pytest.fixture
def fake_connection():
    from honua_qgis.auth import HonuaConnection

    return HonuaConnection(name="local", base_url="https://example.test", api_key="key-xyz")
