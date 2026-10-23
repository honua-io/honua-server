"""Shared lane inputs: the candidate base URL and the fixture's auth profile.

The auth profile is the client-compat fixture's (``docker/client-compat/compose.yml``
and ``tests/python/shared/cert_auth.py``): an admin API key and a deterministic
HS256 OIDC issuer. Both are test-fixture values configured on the candidate
container, not deployment credentials. Receipts never carry them; the wire log
records only the credential scheme.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, "/roster")

from shared.cert_auth import AuthCredentials, AuthMode  # noqa: E402

BASE_URL = os.environ.get("ROSTER_BASE_URL", "http://honua:5000").rstrip("/")
CREDENTIALS = AuthCredentials.from_environment()

WRONG_API_KEY = CREDENTIALS.negative_headers("wrong-api-key")
EXPIRED_BEARER = CREDENTIALS.negative_headers("expired-oidc-bearer")


def api_key_headers() -> dict[str, str]:
    return CREDENTIALS.headers(AuthMode.API_KEY)


def bearer_headers() -> dict[str, str]:
    return CREDENTIALS.headers(AuthMode.OIDC_BEARER)


def url(path: str) -> str:
    return BASE_URL + path
