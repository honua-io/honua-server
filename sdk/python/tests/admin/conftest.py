"""Shared fixtures for admin client tests."""

from __future__ import annotations

from typing import Any

import httpx
import pytest

from honua_sdk.admin import HonuaAdminClient


def make_api_response(data: Any, message: str | None = None) -> dict[str, Any]:
    """Wrap data in the ApiResponse envelope."""
    return {
        "success": True,
        "data": data,
        "message": message,
        "timestamp": "2026-03-01T00:00:00Z",
    }


@pytest.fixture
def make_client():
    """Factory fixture that creates an admin client with a mock transport."""

    def _factory(handler):
        transport = httpx.MockTransport(handler)
        return HonuaAdminClient("http://test.honua.io", transport=transport)

    return _factory
