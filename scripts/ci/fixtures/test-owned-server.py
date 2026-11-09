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
    def test_ci_compose_project_remains_visible_to_following_workflow_steps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "compose.yml"
            path.write_text('ports: ["${HTTP_PORT:-9000}:8080"]')
            self.assertEqual(allocation(path, {"CI": "true"}), {"HTTP_PORT": "9000"})

    def test_inaccessible_process_descriptors_fail_explicitly(self):
        if os.access("/proc/1/fd", os.R_OK):
            self.skipTest("PID 1 descriptors are readable for this user")
        with self.assertRaisesRegex(RuntimeError, "cannot be inspected"):
            OWNED.process_ports.process_owns_listener(1, 23456)

    def test_procfs_error_is_not_hidden_by_glob_suppression(self):
        with patch.object(Path, "glob", return_value=iter(())), \
             patch.object(Path, "iterdir", side_effect=PermissionError):
            with self.assertRaisesRegex(RuntimeError, "cannot be inspected"):
                OWNED.process_ports.process_owns_listener(1, 23456)

    def test_host_network_container_reads_sockets_as_its_own_user(self):
        with child(HTTP) as process:
            check = probe(process)
            check.options.container = "owned"
            state = {"id": "a" * 64, "running": True, "pid": process.pid, "started": "one"}
            sockets = "\n".join(OWNED.process_ports._listening_inodes(check.port))
            result = subprocess.CompletedProcess([], 0, sockets, "")
            with patch.object(OWNED, "container_state", return_value=state), \
                 patch.object(OWNED.subprocess, "run", return_value=result) as execute, \
                 patch.object(OWNED.process_ports, "process_owns_listener", side_effect=PermissionError):
                check.wait()
                self.assertEqual(execute.call_args.args[0][:3], ["docker", "exec", "a" * 64])
                result.stdout = ""
                with self.assertRaisesRegex(RuntimeError, "owned listener"):
                    check.wait()

    def test_container_socket_command_reads_real_owned_descriptors(self):
        with child(HTTP) as process, child(HTTP) as foreign:
            own, other = probe(process), probe(foreign)
            execute = subprocess.run

            def container_command(command, **options):
                self.assertEqual(command[:3], ["docker", "exec", "owned"])
                script = command[-1].replace("/proc/1/fd", f"/proc/{process.pid}/fd")
                script = script.replace("/proc/[0-9]*/fd/*", f"/proc/{process.pid}/fd/*")
                return execute(["/bin/sh", "-c", script], **options)

            with patch.object(OWNED.subprocess, "run", side_effect=container_command):
                self.assertTrue(OWNED.container_owns_listener("owned", own.port))
                self.assertFalse(OWNED.container_owns_listener("owned", other.port))

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
            with patch.object(OWNED, "container_state", side_effect=[state, {**state, "started": "two"}]), \
                 patch.object(OWNED, "container_owns_listener", return_value=True):
                with self.assertRaisesRegex(RuntimeError, "owner changed"):
                    check.wait()

    def test_published_container_does_not_require_host_process_visibility(self):
        with child(HTTP) as process:
            check = probe(process)
            check.options.pid = None
            check.launcher = None
            check.options.container = "owned"
            check.options.container_port = 8080
            state = {"id": "a" * 64, "running": True, "pid": 987654321, "started": "one",
                     "ports": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(check.port)}]}}
            with patch.object(OWNED, "container_state", return_value=state), \
                 patch.object(OWNED, "process_identity", side_effect=RuntimeError("host PID is invisible")):
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
