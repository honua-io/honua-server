"""Error types for the Honua Python SDK."""

from __future__ import annotations

from typing import Any


class HonuaError(Exception):
    """Base exception for SDK failures."""


class HonuaHttpError(HonuaError):
    """Raised when an API request returns a non-success response."""

    def __init__(
        self,
        status_code: int,
        message: str,
        *,
        body: Any | None = None,
    ) -> None:
        super().__init__(f"HTTP {status_code}: {message}")
        self.status_code = status_code
        self.message = message
        self.body = body


class HonuaGrpcError(HonuaError):
    """Raised when a gRPC call fails."""

    def __init__(self, code: Any, message: str, details: Any = None) -> None:
        super().__init__(f"gRPC {code}: {message}")
        self.code = code
        self.message = message
        self.details = details
