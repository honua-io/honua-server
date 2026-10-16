"""Environment hygiene and job-scoped typed SDK construction.

Same-process user code can inspect the intended job capability. Deleting environment
entries is not isolation; deployment must be credential-free and the server enforces
frozen resource scope and the current running attempt.
"""

from __future__ import annotations

import os
from collections.abc import Mapping, MutableMapping
from typing import Any

# Env vars that hand out ambient cloud credentials or the scoped token. These
# are deleted from ``os.environ`` after the scoped client is constructed and
# BEFORE user code is imported.
STRIPPED_ENV_VARS: frozenset[str] = frozenset(
    {
        "HONUA_JOB_TOKEN",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE",
        "ECS_CONTAINER_METADATA_URI",
        "ECS_CONTAINER_METADATA_URI_V4",
        "ECS_CONTAINER_METADATA_FILE",
        # Static keys, if ever injected, must not leak to user code either.
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_WEB_IDENTITY_TOKEN_FILE",
    }
)


def strip_credential_env(env: MutableMapping[str, str] | None = None) -> tuple[str, ...]:
    """Delete ambient-credential + token env vars in place.

    Returns the names that were actually removed (useful for logging/asserting).
    Operates on ``os.environ`` by default.
    """
    target: MutableMapping[str, str] = os.environ if env is None else env
    removed: list[str] = []
    for name in STRIPPED_ENV_VARS:
        if name in target:
            del target[name]
            removed.append(name)
    return tuple(sorted(removed))


def build_scoped_client(base_url: str, job_token: str, *, client_factory: Any = None, job_id: str | None = None) -> Any:
    """Construct the scoped :class:`HonuaClient` from the job-bound bearer token.

    ``client_factory`` is injectable for tests; in production it defaults to the
    real ``honua_sdk.HonuaClient`` + ``StaticAuthProvider``.
    """
    if not base_url:
        raise ValueError("base_url is required to build the scoped client.")
    if not job_token:
        raise ValueError("job_token is required to build the scoped client.")

    if client_factory is not None:
        return client_factory(base_url, job_token)
    if not job_id:
        raise ValueError("job_id is required for the production SDK client.")
    return _default_client_factory(base_url, job_token, job_id)


def _default_client_factory(base_url: str, job_token: str, job_id: str) -> Any:
    # Imported lazily so the harness package imports without the SDK present
    # (tests inject a fake factory). The image always installs honua-sdk.
    from honua_sdk import HonuaClient
    from honua_sdk.auth import StaticAuthProvider

    auth = StaticAuthProvider({"Authorization": f"Bearer {job_token}", "X-Honua-Job-Id": job_id})
    return HonuaClient(base_url, auth_provider=auth)


def assert_credentials_stripped(env: Mapping[str, str] | None = None) -> None:
    """Raise if any stripped var is still present (post-scrub invariant check)."""
    target: Mapping[str, str] = os.environ if env is None else env
    leaked = sorted(name for name in STRIPPED_ENV_VARS if name in target)
    if leaked:
        raise RuntimeError(f"credential env not fully stripped: {leaked}")
