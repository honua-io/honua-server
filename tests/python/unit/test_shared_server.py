# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Tests for the shared out-of-process Honua server harness."""

from __future__ import annotations

import importlib
from pathlib import Path

from shared.server import HonuaServer


def test_server_environment_overrides_auth_defaults(monkeypatch, tmp_path: Path) -> None:
    """Per-server values override the anonymous shared-harness defaults."""
    (tmp_path / "src" / "Honua.Server").mkdir(parents=True)
    captured: dict[str, str] = {}
    server_module = importlib.import_module("shared.server")

    class Process:
        stdout = None
        stderr = None

    def fake_popen(*args, **kwargs):
        del args
        captured.update(kwargs["env"])
        return Process()

    monkeypatch.setattr(server_module.subprocess, "Popen", fake_popen)
    monkeypatch.setattr(HonuaServer, "_wait_for_health", lambda self, timeout: None)

    server = HonuaServer(
        connection_string="Host=localhost;Database=honua",
        project_root=tmp_path,
        environment={
            "HONUA_DEV_AUTH": "false",
            "HONUA_DEV_AUTH_ALLOW_BYPASS": "false",
        },
    )
    server.start()

    assert captured["HONUA_DEV_AUTH"] == "false"
    assert captured["HONUA_DEV_AUTH_ALLOW_BYPASS"] == "false"
    assert captured["HONUA_REGISTER_TEST_INFRASTRUCTURE"] == "true"
