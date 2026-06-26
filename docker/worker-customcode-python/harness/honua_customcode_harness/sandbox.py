"""Credential-stripping + scoped Honua client construction.

The harness builds the scoped :class:`HonuaClient` from ``HONUA_JOB_TOKEN`` and
then SCRUBS the process environment *before* importing/calling user code, so a
tool cannot:

  * read the raw scoped bearer token (``HONUA_JOB_TOKEN`` is deleted), nor
  * reach the ECS/Batch task role via the container credential provider or IMDS
    (``AWS_CONTAINER_CREDENTIALS_*`` / ``ECS_CONTAINER_METADATA_URI*`` deleted).

The only capability the tool keeps to talk to Honua is the pre-authed scoped
client, whose token is least-privilege and job-bound. AWS SDK calls the user
makes will then fall back to whatever (if anything) the operator chose to leave
in place — by default nothing, so user code is denied the task role.
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


def build_scoped_client(base_url: str, job_token: str, *, client_factory: Any = None) -> Any:
    """Construct the scoped :class:`HonuaClient` from the job-bound bearer token.

    ``client_factory`` is injectable for tests; in production it defaults to the
    real ``honua_sdk.HonuaClient`` + ``StaticAuthProvider``.
    """
    if not base_url:
        raise ValueError("base_url is required to build the scoped client.")
    if not job_token:
        raise ValueError("job_token is required to build the scoped client.")

    if client_factory is None:
        client_factory = _default_client_factory

    return client_factory(base_url, job_token)


def _default_client_factory(base_url: str, job_token: str) -> Any:
    # Imported lazily so the harness package imports without the SDK present
    # (tests inject a fake factory). The image always installs honua-sdk.
    from honua_sdk import HonuaClient
    from honua_sdk.auth import StaticAuthProvider

    auth = StaticAuthProvider({"Authorization": f"Bearer {job_token}"})
    return HonuaClient(base_url, auth_provider=auth)


def assert_credentials_stripped(env: Mapping[str, str] | None = None) -> None:
    """Raise if any stripped var is still present (post-scrub invariant check)."""
    target: Mapping[str, str] = os.environ if env is None else env
    leaked = sorted(name for name in STRIPPED_ENV_VARS if name in target)
    if leaked:
        raise RuntimeError(f"credential env not fully stripped: {leaked}")
