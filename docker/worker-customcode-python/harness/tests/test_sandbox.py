from __future__ import annotations

import pytest

from honua_customcode_harness.sandbox import (
    STRIPPED_ENV_VARS,
    assert_credentials_stripped,
    build_scoped_client,
    strip_credential_env,
)


def test_strip_removes_all_credential_vars() -> None:
    env = {
        "HONUA_JOB_TOKEN": "tok",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI": "/v2/creds",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI": "http://169.254.170.2/creds",
        "ECS_CONTAINER_METADATA_URI": "http://169.254.170.2/meta",
        "ECS_CONTAINER_METADATA_URI_V4": "http://169.254.170.2/v4",
        "AWS_ACCESS_KEY_ID": "AKIA",
        "AWS_SECRET_ACCESS_KEY": "secret",
        "AWS_SESSION_TOKEN": "session",
        "HONUA_BASE_URL": "https://keep.me",  # not stripped
        "PATH": "/usr/bin",  # not stripped
    }
    removed = strip_credential_env(env)

    # Every sensitive var is gone.
    for name in STRIPPED_ENV_VARS:
        assert name not in env
    # Token specifically gone.
    assert "HONUA_JOB_TOKEN" not in env
    # Non-sensitive survive.
    assert env["HONUA_BASE_URL"] == "https://keep.me"
    assert env["PATH"] == "/usr/bin"
    # Reported names cover what was present.
    assert "HONUA_JOB_TOKEN" in removed
    assert "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI" in removed
    # assert_credentials_stripped passes on the scrubbed env.
    assert_credentials_stripped(env)


def test_assert_raises_when_leaked() -> None:
    with pytest.raises(RuntimeError, match="not fully stripped"):
        assert_credentials_stripped({"HONUA_JOB_TOKEN": "still-here"})


def test_strip_is_idempotent_on_clean_env() -> None:
    env = {"PATH": "/bin"}
    assert strip_credential_env(env) == ()
    assert env == {"PATH": "/bin"}


def test_build_scoped_client_uses_factory_and_passes_token() -> None:
    seen: dict[str, str] = {}

    def fake_factory(base_url: str, token: str):
        seen["base_url"] = base_url
        seen["token"] = token
        return object()

    client = build_scoped_client(
        "https://api.honua.test", "scoped-tok", client_factory=fake_factory
    )
    assert client is not None
    assert seen == {"base_url": "https://api.honua.test", "token": "scoped-tok"}


def test_build_scoped_client_requires_token() -> None:
    with pytest.raises(ValueError, match="job_token"):
        build_scoped_client("https://x", "", client_factory=lambda *a: None)


def test_default_factory_builds_static_bearer_provider() -> None:
    # Exercises the real SDK wiring if honua_sdk is installed; otherwise skip.
    pytest.importorskip("honua_sdk")
    from honua_customcode_harness.sandbox import _default_client_factory

    client = _default_client_factory("https://api.honua.test", "abc")
    assert client is not None
    # Token should be wrapped as an Authorization Bearer header by the provider.
    client.close()
