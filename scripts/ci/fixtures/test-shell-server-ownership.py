"""Exercise real shell entrypoints with a foreign HTTP listener and fake tools."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
import json
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
from threading import Thread
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[3]
DOCKER = """#!/usr/bin/env python3
import sys
args=sys.argv[1:]
if args[0]=='run': print('fixture-database-container')
elif args[0]=='logs': print('PostgreSQL init process complete; ready for start up.')
elif args[0]=='inspect':
    if any('"running"' in a for a in args):
        import json,os
        print(json.dumps({'id':'a'*64,'running':True,'pid':int(os.environ['OWNERSHIP_FAKE_CONTAINER_PID']),'started':'fixture','status':'running'}))
    else: print('true')
elif args[0]=='exec' and any('SELECT count' in a for a in args): print('2')
"""


class HealthyHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"Ready")

    def log_message(self, *_):
        pass


class ShellServerOwnershipTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="honua-shell-ownership-")
        self.root = Path(self.directory.name)
        self.tools = self.root / "tools"
        self.tools.mkdir()
        (self.tools / "python").symlink_to(sys.executable)
        for name, content in {"docker": DOCKER, "dotnet": "#!/usr/bin/env python3\nimport time\ntime.sleep(30)\n"}.items():
            tool = self.tools / name
            tool.write_text(content)
            tool.chmod(0o755)
        (self.root / "server.dll").touch()
        self.foreign = ThreadingHTTPServer(("127.0.0.1", 0), HealthyHandler)
        self.thread = Thread(target=self.foreign.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.foreign.shutdown()
        self.foreign.server_close()
        self.thread.join()
        self.directory.cleanup()

    def run_harness(self, script, port=None):
        selected_port = self.foreign.server_port if port is None else port
        environment = {**os.environ, "PATH": str(self.tools) + os.pathsep + os.environ["PATH"],
                       "RUNNER_TEMP": str(self.root),
                       "GITHUB_ENV": str(self.root / "github-env"), "GITHUB_OUTPUT": str(self.root / "github-output"),
                       "GITHUB_STEP_SUMMARY": str(self.root / "github-summary"), "GITHUB_WORKSPACE": str(ROOT), "HONUA_PR_GATE_REPO_ROOT": str(ROOT),
                       "HONUA_PR_GATE_POSTGIS_IMAGE": "fixture", "HONUA_LOCAL_TARGET_POSTGIS_IMAGE": "fixture",
                       "HONUA_SERVER_BOOT_SMOKE_DLL": str(self.root / "server.dll"),
                       "HONUA_LOCAL_TARGET_SERVER_DLL": str(self.root / "server.dll"),
                       "HONUA_SERVER_BOOT_SMOKE_PORT": str(selected_port),
                       "HONUA_LOCAL_TARGET_PORT": str(selected_port),
                       "HONUA_SERVER_BOOT_SMOKE_READY_SECONDS": "2" if port == 0 else "0.25",
                       "HONUA_LOCAL_TARGET_READY_SECONDS": "2" if port == 0 else "0.25"}
        environment.update({"LOG_PATH": str(self.root / "action.log"), "SERVER_IMAGE": "",
                            "HONUA_CI_SERVER_PORT": str(self.foreign.server_port),
                            "HONUA_CI_SERVER_READY_SECONDS": "0.25",
                            "HONUA_SOAK_READY_SECONDS": "0.25",
                            "HONUA_DOCKER_SMOKE_PORT": str(self.foreign.server_port),
                            "HONUA_DOCKER_SMOKE_READY_SECONDS": "0.25",
                            "BASE_URL": f"http://127.0.0.1:{self.foreign.server_port}"})
        process = subprocess.Popen(["bash", str(ROOT / script)], cwd=ROOT, env=environment,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        try:
            stdout, stderr = process.communicate(timeout=8)
            return process.returncode, (stdout + stderr).decode()
        finally:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            process.wait(timeout=3)

    def test_boot_smoke_does_not_qualify_a_foreign_ready_server(self):
        code, output = self.run_harness("scripts/ci/server-boot-smoke.sh")
        self.assertNotEqual(code, 0, output)

    def test_post_apply_does_not_qualify_a_foreign_ready_server(self):
        code, output = self.run_harness("scripts/cloud/start-local-post-apply-target.sh")
        self.assertNotEqual(code, 0, output)

    def test_shell_boot_probes_accept_their_owned_listener(self):
        (self.tools / "dotnet").write_text((ROOT / "scripts/ci/fixtures/ready-server.py").read_text())
        for script in ("scripts/ci/server-boot-smoke.sh", "scripts/cloud/start-local-post-apply-target.sh"):
            with self.subTest(script=script):
                code, output = self.run_harness(script, 0)
                self.assertEqual(code, 0, output)

    def test_shared_ci_action_rejects_a_foreign_listener(self):
        action = yaml.safe_load((ROOT / ".github/actions/setup-honua-server/action.yml").read_text())
        start = next(step for step in action["runs"]["steps"] if step["name"] == "Start Honua Server")
        script = self.root / "action.sh"
        script.write_text("set -e\n" + start["run"])
        self.install_redirected_curl()
        code, output = self.run_harness(script)
        self.assertNotEqual(code, 0, output)

    def install_redirected_curl(self):
        curl = self.tools / "curl"
        curl.write_text("#!/usr/bin/env python3\nimport os,sys\n"
                        f"args=[a.replace('http://localhost:5000','http://127.0.0.1:{self.foreign.server_port}').replace('http://localhost:8080','http://127.0.0.1:{self.foreign.server_port}') for a in sys.argv[1:]]\n"
                        "os.execv('/usr/bin/curl',['curl',*args])\n")
        curl.chmod(0o755)

    def test_load_soak_wait_rejects_a_foreign_listener(self):
        self.run_workflow_wait(("load-soak-nightly.yml", "Wait for readiness"))

    def test_container_smoke_rejects_a_foreign_listener(self):
        self.run_workflow_wait(("ci.yml", "Run Docker container"))

    def run_workflow_wait(self, target):
        definition = yaml.safe_load((ROOT / ".github/workflows" / target[0]).read_text())
        steps = [step for job in definition["jobs"].values() for step in job.get("steps", [])]
        source = next(step["run"] for step in steps if step.get("name") == target[1])
        script = self.root / "workflow.sh"
        script.write_text("set -e\nmkdir -p artifacts/load-soak\n"
                          "dotnet fixture > /dev/null 2>&1 &\n"
                          "export OWNERSHIP_FAKE_CONTAINER_PID=$!\necho $! > artifacts/load-soak/server.pid\n" + source)
        self.install_redirected_curl()
        sleeper = self.tools / "sleep"
        sleeper.write_text("#!/bin/sh\nexit 0\n")
        sleeper.chmod(0o755)
        code, output = self.run_harness(script)
        self.assertNotEqual(code, 0, output)

    def test_sdk_geoserver_fixture_rejects_a_foreign_listener(self):
        result = self.run_sdk_fixture("start_geoserver_fixture")
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("owned readiness refused", result.stderr)

    def test_sdk_migration_server_rejects_a_foreign_listener(self):
        result = self.run_sdk_fixture("start_migration_honua_server")
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("owned readiness refused", result.stderr)

    def test_explicit_external_sdk_fixtures_remain_probes(self):
        overrides = {"HONUA_SDK_GEOSERVER_EXTERNAL": "true", "HONUA_SDK_MIGRATION_EXTERNAL": "true"}
        for function in ("start_geoserver_fixture", "start_migration_honua_server"):
            with self.subTest(function=function):
                result = self.run_sdk_fixture(function, overrides)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def run_sdk_fixture(self, function, overrides=None):
        source = (ROOT / "scripts/ci/run-sdk-server-compatibility.sh").read_text()
        prefix = source[:source.index("\nmkdir -p \\\n")]
        fixture = self.root / "geoserver.json"
        fixture.write_text(json.dumps({"responses": {"/geoserver/rest/about/version.xml": "<Version/>"}}))
        script = self.root / "sdk-fixture.sh"
        script.write_text(prefix + f"\n{function}\n")
        environment = {**os.environ, "PATH": str(self.tools) + os.pathsep + os.environ["PATH"],
                       "RESULTS_DIR": str(self.root),
                       "SDK_COMPATIBILITY_RESULTS_DIR": str(self.root),
                       "HONUA_SDK_MIGRATION_GEOSERVER_FIXTURE": str(fixture),
                       "HONUA_SDK_MIGRATION_GEOSERVER_URL": f"http://127.0.0.1:{self.foreign.server_port}/geoserver/rest",
                       "HONUA_SDK_MIGRATION_SERVER_BASE_URL": f"http://127.0.0.1:{self.foreign.server_port}",
                       "HONUA_SDK_MIGRATION_SERVER_IMAGE": "",
                       "HONUA_SDK_GEOSERVER_EXTERNAL": "false", "HONUA_SDK_MIGRATION_EXTERNAL": "false",
                       "HONUA_SDK_FIXTURE_READY_SECONDS": "0.25"}
        environment.update(overrides or {})
        result = subprocess.run(["bash", str(script)], cwd=ROOT, env=environment,
                                capture_output=True, text=True, timeout=8)
        return result


if __name__ == "__main__":
    unittest.main()
