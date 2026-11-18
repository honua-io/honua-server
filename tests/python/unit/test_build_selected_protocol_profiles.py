"""The profile builder compiles the selector's choices and skips the default profile."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
BUILDER = ROOT / "scripts/ci/build-selected-protocol-profiles.py"


def run_builder(payload, producer_analyzers=None):
    env = {key: value for key, value in os.environ.items() if key != "PRODUCER_RUN_ANALYZERS"}
    if producer_analyzers is not None:
        env["PRODUCER_RUN_ANALYZERS"] = producer_analyzers
    with tempfile.TemporaryDirectory() as temp:
        selection = Path(temp) / "selection.json"
        selection.write_text(json.dumps(payload), encoding="utf-8")
        return subprocess.run(
            ["python3", str(BUILDER), "--selection", str(selection), "--dry-run"],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
            env=env,
        )


class BuildSelectedProtocolProfilesTests(unittest.TestCase):
    def test_full_selection_builds_every_profile_except_all(self):
        completed = run_builder({"profiles": ["gsr", "ogc-api", "ogc-api-classic", "all"]})
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("-p:HonuaProtocolProfile=gsr", completed.stdout)
        self.assertIn("-p:HonuaProtocolProfile=ogc-api", completed.stdout)
        self.assertIn("-p:HonuaProtocolProfile=ogc-api-classic", completed.stdout)
        self.assertNotIn("-p:HonuaProtocolProfile=all", completed.stdout)

    def test_analyzer_producer_builds_each_profile_once_with_analyzers(self):
        completed = run_builder({"profiles": ["gsr"]})
        self.assertEqual(completed.returncode, 0, completed.stderr)
        commands = [line for line in completed.stdout.splitlines() if line.startswith("dotnet ")]
        self.assertEqual(len(commands), 1)
        self.assertIn("-p:RunAnalyzers=true", commands[0])
        self.assertNotIn("BuildProjectReferences", commands[0])

    def test_no_analyzer_producer_reuses_references_and_analyzes_server(self):
        completed = run_builder({"profiles": ["gsr"]}, producer_analyzers="false")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        references, server = [line for line in completed.stdout.splitlines() if line.startswith("dotnet ")]
        self.assertIn("-p:RunAnalyzers=false", references)
        self.assertIn("-t:ResolveProjectReferences", references)
        self.assertIn("-p:RunAnalyzers=true", server)
        self.assertIn("-p:BuildProjectReferences=false", server)
        self.assertIn("--no-restore", server)
        for command in (references, server):
            self.assertIn("-p:HonuaProtocolProfile=gsr", command)
            self.assertIn("-p:TreatWarningsAsErrors=true", command)

    def test_default_only_selection_builds_nothing_else(self):
        completed = run_builder({"profiles": ["all"]})
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("no extra protocol profiles", completed.stdout)
        self.assertNotIn("dotnet", completed.stdout)

    def test_unknown_profile_fails(self):
        completed = run_builder({"profiles": ["nope"]})
        self.assertNotEqual(completed.returncode, 0)
        self.assertIn("unsupported protocol profile", completed.stderr)
