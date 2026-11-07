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
        tools = root / "tools"
        tools.mkdir()
        for name, source in {
            "docker": '#!/bin/sh\nprintf "%s\\n" "$*" >> "$DOCKER_CALLS"\n',
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
            self.assertIn('"successful"', (root / "artifacts/validated-examples/gp-result.json").read_text())

    def test_missing_external_sample_fails_with_configuration_guidance(self):
        with tempfile.TemporaryDirectory() as directory:
            script, environment = self.create_workspace(Path(directory))

            result = subprocess.run(["bash", str(script), "gp-local-dev"], env=environment, capture_output=True, text=True)

            self.assertEqual(result.returncode, 2)
            self.assertIn("Set HONUA_SAMPLES_ROOT", result.stderr)


if __name__ == "__main__":
    unittest.main()
