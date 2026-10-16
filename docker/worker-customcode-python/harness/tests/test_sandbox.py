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

    client = _default_client_factory("https://api.honua.test", "abc", "job-sdk")
    assert client is not None
    # Token should be wrapped as an Authorization Bearer header by the provider.
    client.close()


@pytest.mark.parametrize("startup_denial", [False, True])
def test_production_sdk_sends_scoped_context_over_http(startup_denial: bool) -> None:
    from http.server import BaseHTTPRequestHandler, HTTPServer
    from threading import Thread

    seen: dict[str, str | None] = {}
    requests = 0
    readiness_requests = 0

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            nonlocal requests, readiness_requests
            if self.path == "/healthz/live":
                readiness_requests += 1
                assert self.headers.get("Authorization") == "Bearer capability"
                assert self.headers.get("X-Honua-Job-Id") == "job-sdk"
            requests += 1
            if startup_denial and requests == 1:
                self.send_response(401)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            seen["authorization"] = self.headers.get("Authorization")
            seen["job"] = self.headers.get("X-Honua-Job-Id")
            seen["path"] = self.path
            payload = b'{"services":[]}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *args: object) -> None:
            pass

    with HTTPServer(("127.0.0.1", 0), Handler) as server:
        server.timeout = 10
        def serve() -> None:
            for _ in range(3 if startup_denial else 2):
                server.handle_request()

        thread = Thread(target=serve)
        thread.start()
        try:
            with build_scoped_client(
                f"http://127.0.0.1:{server.server_port}", "capability", job_id="job-sdk"
            ) as client:
                client.list_services()
        finally:
            thread.join(timeout=12)
        assert not thread.is_alive()
    assert requests == (3 if startup_denial else 2)
    assert readiness_requests == (2 if startup_denial else 1)
    assert seen["authorization"] == "Bearer capability"
    assert seen["job"] == "job-sdk"
    assert seen["path"] != "/healthz/live"
