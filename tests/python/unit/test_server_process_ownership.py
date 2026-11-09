"""Readiness must belong to the launched process, never a shared-host listener."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
import signal
import select
import socket
import subprocess
import sys
from threading import Thread

import pytest

from shared.server import HonuaServer
from shared.process_ports import server_port

HTTP_SCRIPT = """
from http.server import BaseHTTPRequestHandler, HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
    def log_message(self, *_):
        pass
server = HTTPServer(('127.0.0.1', 0), Handler)
print(server.server_port, flush=True)
server.serve_forever()
"""


class HealthyHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()

    def log_message(self, *_):
        pass


@pytest.fixture
def foreign_listener():
    listener = ThreadingHTTPServer(("127.0.0.1", 0), HealthyHandler)
    thread = Thread(target=listener.serve_forever, daemon=True)
    thread.start()
    try:
        yield listener.server_port
    finally:
        listener.shutdown()
        listener.server_close()
        thread.join()


@contextmanager
def child_process(script):
    process = subprocess.Popen(
        [sys.executable, "-u", "-c", script],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True,
    )
    try:
        yield process
    finally:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        process.wait(timeout=5)


def test_dead_process_cannot_accept_another_servers_health(foreign_listener):
    with child_process("pass") as process:
        process.wait(timeout=5)
        server = HonuaServer("Host=localhost", port=foreign_listener)
        server._process = process
        with pytest.raises(RuntimeError, match="process died"):
            server._wait_for_health(0.2)


def test_running_process_cannot_accept_a_foreign_listener(foreign_listener):
    with child_process("import time; time.sleep(30)") as process:
        server = HonuaServer("Host=localhost", port=foreign_listener)
        server._process = process
        with pytest.raises(RuntimeError, match="healthy"):
            server._wait_for_health(0.2)


def test_default_port_can_be_overridden_by_environment(monkeypatch):
    monkeypatch.setenv("HONUA_TEST_PORT", "23456")
    assert HonuaServer("Host=localhost").port == 23456


def test_ci_preserves_its_default_port(monkeypatch):
    monkeypatch.delenv("HONUA_TEST_PORT", raising=False)
    monkeypatch.setenv("CI", "true")
    assert HonuaServer("Host=localhost").port == HonuaServer.DEFAULT_PORT


def test_local_default_selects_a_free_ephemeral_port(monkeypatch):
    monkeypatch.delenv("HONUA_TEST_PORT", raising=False)
    monkeypatch.delenv("CI", raising=False)
    selected = HonuaServer("Host=localhost").port
    assert selected != HonuaServer.DEFAULT_PORT
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", selected))


def test_invalid_configured_port_is_rejected(monkeypatch):
    monkeypatch.setenv("HONUA_TEST_PORT", "65536")
    with pytest.raises(ValueError, match="port"):
        HonuaServer("Host=localhost")


@pytest.mark.parametrize("server_location", ["process", "descendant"])
def test_owned_listener_and_descendant_are_accepted(server_location):
    script = HTTP_SCRIPT
    if server_location == "descendant":
        script = f"import subprocess,sys; subprocess.run([sys.executable, '-u', '-c', {HTTP_SCRIPT!r}])"
    with child_process(script) as process:
        assert select.select([process.stdout], [], [], 5)[0]
        port = int(process.stdout.readline())
        server = HonuaServer("Host=localhost", port=port)
        server._process = process
        server._wait_for_health(2)


def test_process_exit_during_health_cannot_be_accepted(monkeypatch):
    import httpx
    from types import SimpleNamespace

    with child_process(HTTP_SCRIPT) as process:
        assert select.select([process.stdout], [], [], 5)[0]
        server = HonuaServer("Host=localhost", port=int(process.stdout.readline()))
        server._process = process

        def response_after_exit(*_, **__):
            process.terminate()
            process.wait(timeout=5)
            return SimpleNamespace(status_code=200)

        monkeypatch.setattr(httpx, "get", response_after_exit)
        with pytest.raises(RuntimeError, match="process died"):
            server._wait_for_health(2)


def test_authenticated_port_override_keeps_worker_offset(monkeypatch):
    monkeypatch.setenv("HONUA_STAC_COMPAT_AUTH_PORT", "24500")
    assert server_port("HONUA_STAC_COMPAT_AUTH_PORT", 6565, 3) == 24503


def test_explicit_port_takes_precedence_over_environment(monkeypatch):
    monkeypatch.setenv("HONUA_TEST_PORT", "24500")
    assert HonuaServer("Host=localhost", port=24501).port == 24501


def test_failed_start_reaps_only_its_process(monkeypatch, tmp_path, foreign_listener):
    import shared.server as server_module

    (tmp_path / "src" / "Honua.Server").mkdir(parents=True)
    with child_process("import time; time.sleep(30)") as process:
        monkeypatch.setattr(server_module.subprocess, "Popen", lambda *_, **__: process)
        server = HonuaServer("Host=localhost", port=foreign_listener, project_root=tmp_path)
        with pytest.raises(RuntimeError, match="healthy"):
            server.start(timeout=0.2)
        assert process.poll() is not None
        assert server._process is None


def test_anonymous_and_authenticated_ports_are_independent(monkeypatch):
    monkeypatch.setenv("HONUA_STAC_COMPAT_PORT", "24500")
    monkeypatch.setenv("HONUA_STAC_COMPAT_AUTH_PORT", "25500")
    assert server_port("HONUA_STAC_COMPAT_PORT", 5565, 2) == 24502
    assert server_port("HONUA_STAC_COMPAT_AUTH_PORT", 6565, 2) == 25502
