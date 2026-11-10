import base64
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class QuickstartConsoleSmokeTests(unittest.TestCase):
    def test_compose_receives_a_disposable_audit_chain_key(self):
        script = Path(__file__).resolve().parents[1] / "smoke-quickstart-console.sh"
        # Exercise the real setup and Compose wrapper without building containers.
        setup = script.read_text().split("\nif ! command -v jq", 1)[0]
        setup = setup.replace('trap cleanup EXIT', '')
        setup = setup.replace('ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"',
                              f'ROOT_DIR="{script.parents[2]}"')
        with tempfile.TemporaryDirectory() as directory:
            docker = Path(directory) / "docker"
            docker.write_text('#!/usr/bin/env python3\nimport os\nprint(os.getenv("AuditLog__ChainVerification__Key", ""))\n')
            docker.chmod(0o755)
            environment = dict(os.environ, PATH=f"{directory}:{os.environ['PATH']}")
            environment.pop("AuditLog__ChainVerification__Key", None)
            result = subprocess.run(["bash", "-c", setup + "\ncompose ps\ncompose ps\n"],
                                    env=environment, capture_output=True, text=True, check=True)
            keys = result.stdout.splitlines()
            self.assertEqual(len(keys), 2)
            self.assertEqual(keys[0], keys[1])
            self.assertEqual(len(base64.b64decode(keys[0], validate=True)), 32)


if __name__ == "__main__":
    unittest.main()
