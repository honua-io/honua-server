#!/usr/bin/env python3
"""Keep the post-merge affected run event-driven, queued, least-privilege and visible."""

from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github/workflows"
SUSPECT_RANGE = ROOT / "scripts/ci/trunk-suspect-range.sh"


def workflow(name):
    return yaml.load((WORKFLOWS / name).read_text(), Loader=yaml.BaseLoader)


def step_scripts(job):
    return "\n".join(step.get("run", "") for step in job["steps"])


class TrunkAffectedWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.definition = workflow("trunk-affected.yml")
        cls.job = cls.definition["jobs"]["dispatch"]

    def test_every_trunk_push_triggers_without_path_filters(self):
        triggers = self.definition["on"]
        self.assertEqual(set(triggers), {"push", "workflow_dispatch"})
        self.assertEqual(triggers["push"], {"branches": ["trunk"]})

    def test_rapid_merges_queue_instead_of_cancelling(self):
        concurrency = self.definition["concurrency"]
        self.assertEqual(concurrency["group"], "trunk-affected")
        self.assertEqual(concurrency["cancel-in-progress"], "false")

    def test_permissions_are_least_privilege(self):
        self.assertEqual(self.definition["permissions"], {})
        self.assertEqual(self.job["permissions"], {"actions": "write", "contents": "read"})
        self.assertEqual(self.job["if"], "github.repository == 'honua-io/honua-server'")

    def test_dispatches_ci_from_the_last_green_trunk_head(self):
        scripts = step_scripts(self.job)
        self.assertIn("actions/workflows/ci.yml/runs?branch=trunk&status=success", scripts)
        self.assertIn("git merge-base --is-ancestor", scripts)
        self.assertIn("actions/workflows/ci.yml/dispatches", scripts)
        self.assertIn("inputs[selective_base]=${LAST_GREEN}", scripts)
        self.assertIn("scripts/ci/trunk-suspect-range.sh", scripts)

    def test_selective_ci_runs_never_replace_a_pending_full_run(self):
        ci = workflow("ci.yml")
        self.assertIn("selective_base", ci["on"]["workflow_dispatch"]["inputs"])
        self.assertIn("inputs.selective_base && '-affected'", ci["concurrency"]["group"])
        range_steps = [step for step in ci["jobs"]["changes"]["steps"]
                       if "trunk-suspect-range.sh" in step.get("run", "")]
        self.assertEqual(len(range_steps), 1)
        self.assertEqual(range_steps[0]["if"], "steps.diff.outputs.trailing_selective == 'true'")


class SuspectRangeTests(unittest.TestCase):
    def test_lists_each_merge_and_names_its_pull_request(self):
        with tempfile.TemporaryDirectory() as clone:
            def git(*args):
                return subprocess.run(["git", "-C", clone, "-c", "user.name=ci", "-c", "user.email=ci@example.invalid",
                                       *args], check=True, capture_output=True, text=True).stdout.strip()

            git("init", "-q", "-b", "trunk")
            git("commit", "-q", "--allow-empty", "-m", "chore: baseline (#1)")
            base = git("rev-parse", "HEAD")
            git("commit", "-q", "--allow-empty", "-m", "fix(tiles): keep bounds usable (#7199)")
            git("commit", "-q", "--allow-empty", "-m", "docs: direct push without a pull request")
            head = git("rev-parse", "HEAD")

            report = subprocess.run([str(SUSPECT_RANGE), base, head], cwd=clone, check=True,
                                    capture_output=True, text=True,
                                    env={"PATH": "/usr/bin:/bin", "GITHUB_REPOSITORY": "honua-io/honua-server"}).stdout

        self.assertIn(f"`{base}`..`{head}` (2 merges", report)
        self.assertIn("fix(tiles): keep bounds usable (#7199)", report)
        self.assertIn("docs: direct push without a pull request", report)
        self.assertIn("Merged pull requests: [#7199](https://github.com/honua-io/honua-server/pull/7199)", report)
        self.assertNotIn("#1]", report)


if __name__ == "__main__":
    unittest.main()
