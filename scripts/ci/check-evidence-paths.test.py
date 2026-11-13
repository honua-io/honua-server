#!/usr/bin/env python3
"""Exercise the evidence guard against a real Git diff, including rename evasion."""

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("check-evidence-paths.py")
SPEC = importlib.util.spec_from_file_location("evidence_paths", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class EvidencePathsTests(unittest.TestCase):
    def test_policy(self):
        for path in ["docs/evidence/run.json", "tests/EVIDENCE/helper.py", "tests/server.log.gz", "build.LOG"]:
            with self.subTest(path=path):
                self.assertTrue(MODULE.forbidden(path))
        for path in ["eng/ci/fixtures/shard-capacity/wms.json", "scripts/ci/check-evidence-paths.py", "docs/evidence-posture.md", "src/Server.Logging.cs", "docs/logging.md", "tests/catalog.logical.json"]:
            with self.subTest(path=path):
                self.assertFalse(MODULE.forbidden(path))

    def test_added_paths_and_renames(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args):
                return subprocess.check_output(["git", *args], cwd=root).decode().strip()

            def commit():
                git("add", ".")
                git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-qm", "fixture")

            def check(base):
                return subprocess.run(["python3", str(SCRIPT), "--base", base], cwd=root, capture_output=True, text=True)

            git("init", "-q")
            (root / "old.log").write_text("historical log")
            (root / "input.json").write_text("fixture")
            commit()
            base = git("rev-parse", "HEAD")
            (root / "old.log").write_text("existing log changed")
            (root / "fixture.json").write_text("fixture")
            commit()
            self.assertEqual(check(base).returncode, 0)
            git("mv", "input.json", "new.log.gz")
            commit()
            result = check(base)
            self.assertEqual(result.returncode, 1)
            self.assertIn("new.log.gz", result.stdout)
            self.assertNotEqual(check("missing-ref").returncode, 0)


if __name__ == "__main__":
    unittest.main()
