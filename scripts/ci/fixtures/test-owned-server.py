from contextlib import contextmanager, redirect_stdout
import importlib.util
from io import StringIO
import os
from pathlib import Path
import select
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("owned_server", Path(__file__).resolve().parents[1] / "owned-server.py")
OWNED = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OWNED)
HTTP = """
from http.server import BaseHTTPRequestHandler,HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'Ready')
    def log_message(self,*args): pass
server=HTTPServer(('127.0.0.1',0),Handler)
print(server.server_port,flush=True)
server.serve_forever()
"""


@contextmanager
def child(script):
    process = subprocess.Popen([sys.executable, "-u", "-c", script], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        yield process
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=3)
        process.stdout.close()
        process.stderr.close()


def probe(process):
    assert select.select([process.stdout], [], [], 3)[0], "fake HTTP server did not start"
    port = int(process.stdout.readline())
    options = SimpleNamespace(pid=process.pid, container=None, container_port=None,
                              url=f"http://127.0.0.1:{port}/healthz/ready", body="Ready", timeout=0.2)
    return OWNED.ReadinessProbe(options)


def allocation(path, environment):
    output = StringIO()
    with patch.dict(os.environ, environment, clear=True), redirect_stdout(output):
        OWNED.compose_environment(path)
    return dict(line.split("=", 1) for line in output.getvalue().splitlines())


class OwnedServerTests(unittest.TestCase):
    def test_compose_defaults_isolate_projects_and_local_ports(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "compose.yml"
            path.write_text('ports: ["${HTTP_PORT:-9000}:8080", "${POSTGRES_PORT:-5432}:5432"]')
            first, second = allocation(path, {}), allocation(path, {})
            self.assertNotEqual(first["COMPOSE_PROJECT_NAME"], second["COMPOSE_PROJECT_NAME"])
            self.assertNotEqual(first["HTTP_PORT"], "9000")
            self.assertNotEqual(first["POSTGRES_PORT"], "5432")
            self.assertNotEqual(first["HTTP_PORT"], first["POSTGRES_PORT"])

    def test_compose_preserves_ci_defaults_and_explicit_overrides(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "compose.yml"
            path.write_text('ports: ["${HTTP_PORT:-9000}:8080", "${POSTGRES_PORT:-5432}:5432"]')
            result = allocation(path, {"CI": "true", "COMPOSE_PROJECT_NAME": "owned-ci", "HTTP_PORT": "23456"})
            self.assertEqual(result, {"HTTP_PORT": "23456", "POSTGRES_PORT": "5432", "COMPOSE_PROJECT_NAME": "owned-ci"})

    def test_owned_http_listener_is_accepted(self):
        with child(HTTP) as process:
            probe(process).wait()

    def test_another_live_process_cannot_claim_the_listener(self):
        with child(HTTP) as server, child("import time;time.sleep(30)") as other:
            check = probe(server)
            check.options.pid = other.pid
            check.launcher = OWNED.process_identity(other.pid)
            with self.assertRaisesRegex(RuntimeError, "owned listener"):
                check.wait()

    def test_process_exit_after_health_is_refused(self):
        with child(HTTP) as process:
            check = probe(process)
            def healthy_after_exit():
                process.terminate()
                process.wait(timeout=3)
                return True
            check.healthy = healthy_after_exit
            with self.assertRaisesRegex(RuntimeError, "exited"):
                check.wait()

    def test_container_identity_change_after_health_is_refused(self):
        with child(HTTP) as process:
            check = probe(process)
            check.options.container = "owned"
            state = {"id": "a" * 64, "running": True, "pid": process.pid, "started": "one"}
            with patch.object(OWNED, "container_state", side_effect=[state, {**state, "started": "two"}]):
                with self.assertRaisesRegex(RuntimeError, "owner changed"):
                    check.wait()

    def test_docker_published_port_is_bound_to_the_exact_container(self):
        with child(HTTP) as process:
            check = probe(process)
            check.options.container = "owned"
            check.options.container_port = 8080
            state = {"id": "a" * 64, "running": True, "pid": process.pid, "started": "one",
                     "ports": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(check.port)}]}}
            with patch.object(OWNED, "container_state", return_value=state):
                check.wait()
                state["ports"]["8080/tcp"][0]["HostPort"] = str(check.port + 1)
                with self.assertRaisesRegex(RuntimeError, "owned listener"):
                    check.wait()


if __name__ == "__main__":
    unittest.main()
