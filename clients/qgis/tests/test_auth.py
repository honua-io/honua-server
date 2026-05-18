"""Unit tests for ``honua_qgis.auth``."""

from __future__ import annotations

import pytest

from honua_qgis.auth import (
    HonuaConnection,
    encode_api_key_query,
    filter_unique_connection_names,
)


class TestHonuaConnection:
    def test_strips_trailing_slash(self):
        conn = HonuaConnection(name="local", base_url="https://example.test/")
        assert conn.normalized_base_url == "https://example.test"

    def test_request_headers_include_api_key(self):
        conn = HonuaConnection(name="local", base_url="https://example.test", api_key="abc")
        headers = conn.request_headers()
        assert headers["X-API-Key"] == "abc"
        assert headers["Accept"] == "application/json"

    def test_request_headers_omit_key_when_empty(self):
        conn = HonuaConnection(name="local", base_url="https://example.test")
        assert "X-API-Key" not in conn.request_headers()

    @pytest.mark.parametrize("bad_url", ["", "ftp://example.test", "not-a-url", "https:///"])
    def test_rejects_invalid_url(self, bad_url):
        with pytest.raises(ValueError):
            HonuaConnection(name="local", base_url=bad_url)

    def test_rejects_blank_name(self):
        with pytest.raises(ValueError):
            HonuaConnection(name="   ", base_url="https://example.test")


def test_encode_api_key_query_url_safe():
    qs = encode_api_key_query("hello world&special?")
    assert qs == "apikey=hello%20world%26special%3F"


def test_encode_api_key_query_empty():
    assert encode_api_key_query("") == ""


def test_filter_unique_connection_names_dedupes_and_strips():
    names = filter_unique_connection_names(["a", " a ", "b", "", None, "b", "c"])
    assert names == ["a", "b", "c"]
