#!/usr/bin/env python3
"""Prove the installed actionlint rejects loader errors and permits queue."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
ACTIONLINT = str(Path(sys.argv.pop(1)).resolve())
CONFIG = ROOT / ".github/actionlint.yaml"
WORKFLOW = """name: lint fixture
on: push
concurrency:
  group: fixture
  cancel-in-progress: false
  queue: max
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
"""


class WorkflowLintTests(unittest.TestCase):
    def lint(self, text, filename="fixture.yml"):
        # Preserve the path patterns used by the real repository config.
        with tempfile.TemporaryDirectory() as temporary:
            workflow = Path(temporary) / ".github/workflows" / filename
            workflow.parent.mkdir(parents=True)
            workflow.write_text(text)
            return subprocess.run(
                [ACTIONLINT, "-shellcheck=", "-pyflakes=", "-config-file", str(CONFIG),
                 str(workflow.relative_to(temporary))],
                cwd=temporary, text=True, capture_output=True, timeout=15,
            )

    def test_queue_is_accepted(self):
        result = self.lint(WORKFLOW)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_job_level_runner_temp_is_rejected(self):
        result = self.lint(WORKFLOW.replace("    steps:", "    env:\n      TEMP: ${{ runner.temp }}\n    steps:"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('context "runner" is not allowed here', result.stdout)

    def test_unrecognized_syntax_is_rejected(self):
        result = self.lint(WORKFLOW.replace("queue: max", "typo: max"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unexpected key "typo"', result.stdout)

    def test_missing_dependencies_are_rejected(self):
        result = self.lint(WORKFLOW.replace("    steps:", "    needs: missing\n    steps:"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('missing', result.stdout)

    def test_unknown_property_is_rejected(self):
        invalid = WORKFLOW.replace("echo ok", "echo '${{ job.workflow_ref }}'")
        result = self.lint(invalid)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('property "workflow_ref"', result.stdout)


if __name__ == "__main__":
    unittest.main()
