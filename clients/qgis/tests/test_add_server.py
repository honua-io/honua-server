"""Unit tests for ``honua_qgis.dialog_add_server``.

The Qt dialog itself is exercised inside QGIS (covered by the Docker
end-to-end test); these tests focus on ``validate_form`` and
``test_connection``, the pure helpers behind the dialog widgets.
"""

from __future__ import annotations

import pytest

from honua_qgis.auth import HonuaConnection
from honua_qgis.client import HonuaClientError
from honua_qgis.dialog_add_server import test_connection as run_connection_test
from honua_qgis.dialog_add_server import validate_form


def test_validate_form_requires_name():
    result = validate_form("", "https://example.test", "")
    assert not result.ok
    assert "name" in result.error.lower()


def test_validate_form_requires_url():
    result = validate_form("local", "", "")
    assert not result.ok
    assert "url" in result.error.lower()


def test_validate_form_rejects_non_http_scheme():
    result = validate_form("local", "ftp://example.test", "")
    assert not result.ok


def test_validate_form_accepts_minimal_input():
    result = validate_form("local", "https://example.test", "")
    assert result.ok


def test_validate_form_accepts_optional_api_key():
    result = validate_form("local", "https://example.test", "secret-key")
    assert result.ok


class _StubClient:
    def __init__(self, connection, *, raise_with: HonuaClientError | None = None):
        self.connection = connection
        self._raise_with = raise_with
        self.pinged = False

    def ping(self) -> None:
        self.pinged = True
        if self._raise_with is not None:
            raise self._raise_with


def test_run_connection_test_returns_ok_when_ping_succeeds():
    conn = HonuaConnection(name="local", base_url="https://example.test")
    result = run_connection_test(conn, client_factory=_StubClient)
    assert result.ok
    assert result.error == ""


def test_run_connection_test_surfaces_client_error():
    conn = HonuaConnection(name="local", base_url="https://example.test")

    def factory(c):
        return _StubClient(c, raise_with=HonuaClientError("boom"))

    result = run_connection_test(conn, client_factory=factory)
    assert not result.ok
    assert "boom" in result.error
