"""The profile builder compiles the selector's choices and skips the default profile."""
import json
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
BUILDER = ROOT / "scripts/ci/build-selected-protocol-profiles.py"


def run_builder(payload):
    with tempfile.TemporaryDirectory() as temp:
        selection = Path(temp) / "selection.json"
        selection.write_text(json.dumps(payload), encoding="utf-8")
        return subprocess.run(
            ["python3", str(BUILDER), "--selection", str(selection), "--dry-run"],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
        )


class BuildSelectedProtocolProfilesTests(unittest.TestCase):
    def test_full_selection_builds_every_profile_except_all(self):
        completed = run_builder({"profiles": ["gsr", "ogc-api", "ogc-api-classic", "all"]})
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("-p:HonuaProtocolProfile=gsr", completed.stdout)
        self.assertIn("-p:HonuaProtocolProfile=ogc-api", completed.stdout)
        self.assertIn("-p:HonuaProtocolProfile=ogc-api-classic", completed.stdout)
        self.assertNotIn("-p:HonuaProtocolProfile=all", completed.stdout)

    def test_default_only_selection_builds_nothing_else(self):
        completed = run_builder({"profiles": ["all"]})
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("no extra protocol profiles", completed.stdout)
        self.assertNotIn("dotnet", completed.stdout)

    def test_unknown_profile_fails(self):
        completed = run_builder({"profiles": ["nope"]})
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("unsupported protocol profile", completed.stderr)
