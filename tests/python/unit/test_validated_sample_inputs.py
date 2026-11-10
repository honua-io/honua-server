import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[3] / "scripts/examples/validate-customer-paths.sh"


class ValidatedSampleInputTests(unittest.TestCase):
    def create_workspace(self, root):
        script = root / "scripts/examples/validate-customer-paths.sh"
        script.parent.mkdir(parents=True)
        script.write_text(SCRIPT.read_text())
        ownership = root / "scripts/ci/owned-server.py"
        ownership.parent.mkdir()
        ownership.write_text('''import os,sys
from pathlib import Path
if sys.argv[1] == "port":
    print(sys.argv[sys.argv.index("--ci-default") + 1])
elif sys.argv[1] == "wait":
    assert sys.argv[sys.argv.index("--container-port") + 1] == "8080"
    Path(os.environ["OWNED_CALLS"]).write_text(" ".join(sys.argv[1:]))
else:
    raise SystemExit(2)
''')
        tools = root / "tools"
        tools.mkdir()
        for name, source in {
            "docker": '#!/bin/sh\nprintf "%s\\n" "$*" >> "$DOCKER_CALLS"\ncase "$*" in *"ps -q honua") echo fixture-container ;; esac\n',
            "curl": '#!/bin/sh\nprintf "Ready\\n"\n',
        }.items():
            executable = tools / name
            executable.write_text(source)
            executable.chmod(0o755)
        environment = os.environ | {
            "PATH": str(tools) + os.pathsep + os.environ["PATH"],
            "HONUA_SAMPLES_ROOT": str(root / "external-samples"),
            "HONUA_EXAMPLES_CANDIDATE_IMAGE": "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64,
            "DOCKER_CALLS": str(root / "docker-calls.txt"),
            "SAMPLE_BASE": str(root / "sample-base.txt"),
            "OWNED_CALLS": str(root / "owned-calls.txt"),
        }
        return script, environment

    def test_candidate_uses_external_sample_without_building_server(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script, environment = self.create_workspace(root)
            sample = Path(environment["HONUA_SAMPLES_ROOT"]) / "samples/gp-local-dev/submit-buffer.sh"
            sample.parent.mkdir(parents=True)
            sample.write_text('printf "%s" "$BASE" > "$SAMPLE_BASE"\nprintf \'{"status":"successful"}\\n\'\n')

            result = subprocess.run(["bash", str(script), "gp-local-dev"], env=environment, capture_output=True, text=True)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((root / "sample-base.txt").read_text(), "http://127.0.0.1:18084")
            calls = (root / "docker-calls.txt").read_text()
            self.assertIn("up -d --no-build", calls)
            self.assertNotIn("--build", calls)
            self.assertIn('"successful"', (root / "artifacts/validated-examples/gp-result.txt").read_text())
            self.assertIn("wait --container fixture-container --container-port 8080", (root / "owned-calls.txt").read_text())

    def test_external_sample_reader_and_tests_select_pr_validation(self):
        selector = SCRIPT.parent.parent / "ci/pr-touches-ci-surface.sh"
        for path in ("scripts/examples/validate-customer-paths.sh", "tests/python/unit/test_validated_sample_inputs.py"):
            with self.subTest(path=path):
                result = subprocess.run(
                    ["bash", str(selector), "--paths"], input=path + "\n", capture_output=True, text=True
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("ci_surface=true", result.stdout)

    def test_missing_external_sample_fails_with_configuration_guidance(self):
        with tempfile.TemporaryDirectory() as directory:
            script, environment = self.create_workspace(Path(directory))

            result = subprocess.run(["bash", str(script), "gp-local-dev"], env=environment, capture_output=True, text=True)

            self.assertEqual(result.returncode, 2)
            self.assertIn("Set HONUA_SAMPLES_ROOT", result.stderr)
            calls = Path(directory) / "docker-calls.txt"
            self.assertFalse(calls.exists(), "missing samples must fail before starting Compose")


if __name__ == "__main__":
    unittest.main()
