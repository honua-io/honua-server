# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Deterministic authentication inputs for real-client certification lanes."""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import time
from dataclasses import dataclass
from enum import Enum


class AuthMode(str, Enum):
    """Authentication modes exercised by the Wave 1 read-suite seed."""

    ANONYMOUS = "anonymous"
    API_KEY = "api-key"
    OIDC_BEARER = "oidc-bearer"


AUTHENTICATED_MODES = (AuthMode.API_KEY, AuthMode.OIDC_BEARER)
NEGATIVE_MODES = ("wrong-api-key", "expired-oidc-bearer")


@dataclass(frozen=True)
class AuthCredentials:
    """Credentials configured on the local client-compat server fixture."""

    api_key: str
    oidc_issuer: str
    oidc_audience: str
    oidc_signing_key: str

    @classmethod
    def from_environment(cls) -> "AuthCredentials":
        return cls(
            api_key=os.getenv("HONUA_CERT_API_KEY", "ClientCompatAdmin123!"),
            oidc_issuer=os.getenv("HONUA_CERT_OIDC_ISSUER", "https://cert-auth.honua.test"),
            oidc_audience=os.getenv("HONUA_CERT_OIDC_AUDIENCE", "honua-client-compat"),
            oidc_signing_key=os.getenv(
                "HONUA_CERT_OIDC_SIGNING_KEY",
                "client-compat-certification-signing-key-2026-wave-1",
            ),
        )

    def headers(self, mode: AuthMode) -> dict[str, str]:
        """Return the request headers for one certification auth mode."""
        if mode is AuthMode.ANONYMOUS:
            return {}
        if mode is AuthMode.API_KEY:
            return {"X-API-Key": self.api_key}
        return {"Authorization": f"Bearer {self.bearer()}"}

    def negative_headers(self, mode: str) -> dict[str, str]:
        """Return a deterministic invalid credential for a negative case."""
        if mode == "wrong-api-key":
            return {"X-API-Key": "wrong-client-compat-key"}
        if mode == "expired-oidc-bearer":
            return {"Authorization": f"Bearer {self.bearer(expired=True)}"}
        raise ValueError(f"Unknown negative auth mode: {mode}")

    def bearer(self, *, expired: bool = False) -> str:
        """Mint an HS256 JWT accepted by the deterministic OIDC fixture."""
        now = int(time.time())
        expiry = now - 900 if expired else now + 3600
        header = {"alg": "HS256", "typ": "JWT"}
        payload = {
            "iss": self.oidc_issuer,
            "aud": self.oidc_audience,
            "sub": "cert-auth-reader",
            "name": "Certification Auth Reader",
            "roles": ["admin"],
            "iat": now - 60,
            "nbf": now - 60,
            "exp": expiry,
            "jti": f"cert-auth-{'expired' if expired else now}",
        }
        signing_input = b".".join((_encode(header), _encode(payload)))
        signature = hmac.new(
            self.oidc_signing_key.encode(), signing_input, hashlib.sha256
        ).digest()
        return b".".join((signing_input, _base64url(signature))).decode()


def _encode(value: object) -> bytes:
    return _base64url(json.dumps(value, separators=(",", ":")).encode())


def _base64url(value: bytes) -> bytes:
    return base64.urlsafe_b64encode(value).rstrip(b"=")
