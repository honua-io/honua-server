#!/usr/bin/env python3
"""Exercise the real smoke script with mocked external processes."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("server-boot-smoke.sh").resolve()

DOCKER = r'''#!/usr/bin/env bash
case "$1" in
  image|run|rm) exit 0 ;;
  inspect) echo true; exit 0 ;;
  logs) echo 'PostgreSQL init process complete; ready for start up.'; exit 0 ;;
  exec)
    if [[ "$3" == pg_isready ]]; then
      count=$(cat "$MOCK_ROOT/probes" 2>/dev/null || echo 0)
      count=$((count + 1)); echo "$count" > "$MOCK_ROOT/probes.next"
      mv "$MOCK_ROOT/probes.next" "$MOCK_ROOT/probes"
      [[ "$READY_AFTER" != never && "$count" -ge "$READY_AFTER" ]]
      exit $?
    fi
    [[ "$*" != *'SELECT count'* ]] || echo 2
    exit 0 ;;
esac
exit 1
'''


class BootSmokeReadinessTests(unittest.TestCase):
    def run_smoke(self, ready_after):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "server.dll").touch()
            commands = {
                "docker": DOCKER,
                "dotnet": '#!/usr/bin/env bash\ntouch "$MOCK_ROOT/server-started"\nexec /bin/sleep 30\n',
                "curl": '#!/usr/bin/env bash\necho Ready\n',
                "timeout": '#!/usr/bin/env bash\nif [[ "$2" == bash ]]; then shift; exec /usr/bin/timeout 0.7s "$@"; fi\nshift; exec "$@"\n',
                "sleep": '#!/usr/bin/env bash\nexec /bin/sleep 0.01\n',
            }
            for name, body in commands.items():
                path = root / name
                path.write_text(body)
                path.chmod(0o755)
            environment = dict(os.environ, PATH=f"{root}:{os.environ['PATH']}",
                               MOCK_ROOT=str(root), READY_AFTER=str(ready_after),
                               HONUA_PR_GATE_POSTGIS_IMAGE="mock-postgis",
                               HONUA_SERVER_BOOT_SMOKE_DLL=str(root / "server.dll"),
                               HONUA_SERVER_BOOT_SMOKE_LOG=str(root / "server.log"),
                               HONUA_SERVER_BOOT_SMOKE_DB_LOG=str(root / "db.log"))
            result = subprocess.run(["bash", str(SCRIPT)], env=environment,
                                    capture_output=True, text=True, timeout=5)
            return result, int((root / "probes").read_text()), (root / "server-started").exists()

    def test_interrupted_probe_preserves_last_completed_count(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "probes").write_text("1\n")
            interrupted_docker = DOCKER.replace(
                'case "$1" in',
                'echo() { kill -TERM "$$"; }\ncase "$1" in',
            )
            result = subprocess.run(
                ["bash", "-c", interrupted_docker, "docker", "exec", "db", "pg_isready"],
                env=dict(os.environ, MOCK_ROOT=str(root), READY_AFTER="never"),
                capture_output=True, text=True, timeout=5,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("1\n", (root / "probes").read_text())

    def test_delayed_readiness_retries_before_starting_server(self):
        result, probes, started = self.run_smoke(3)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(3, probes)
        self.assertIn("readiness is Ready", result.stdout)

    def test_never_ready_fails_without_starting_server(self):
        result, probes, started = self.run_smoke("never")
        self.assertNotEqual(0, result.returncode)
        self.assertGreater(probes, 1)
        self.assertFalse(started)
        self.assertIn("PostGIS sidecar did not expose", result.stderr)
        self.assertIn("PostGIS sidecar log", result.stderr)

    def test_immediate_readiness_needs_one_probe(self):
        result, probes, started = self.run_smoke(1)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(1, probes)


if __name__ == "__main__":
    unittest.main()
