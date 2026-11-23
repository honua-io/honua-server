#!/usr/bin/env python3
"""Trunk Sanity compiles reduced protocol profiles for every compiled change."""

import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import yaml

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/ci"))
import trunk_sanity_profiles as profiles

BASE, HEAD = "b" * 40, "c" * 40
SELECTION = '{"profiles": ["gsr", "all"]}'


def fake_check_output(changed):
    calls = []

    def run(command, **options):
        calls.append((command, options))
        return changed if command[0] == "git" else SELECTION
    return calls, run


def selector_calls(calls):
    return [(command, options) for command, options in calls if command[0] != "git"]


class SelectionTests(unittest.TestCase):
    def select(self, base, changed):
        calls, run = fake_check_output(changed)
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(profiles.subprocess, "check_output", side_effect=run):
            selection = Path(directory) / "selection.json"
            build, reason = profiles.select(base, HEAD, selection)
            written = selection.read_text() if selection.exists() else ""
        return build, reason, written, selector_calls(calls)

    def test_without_successful_ancestor_every_profile_compiles(self):
        build, _, written, calls = self.select("", "")
        self.assertTrue(build)
        self.assertEqual(written, SELECTION)
        self.assertEqual(calls[0][0][-1], "--all")

    def test_range_without_compiled_inputs_skips_profiles(self):
        changed = "docs/guide.md\ntests/Honua.Server.Tests/A.cs\nscripts/ci/x.py\n.github/workflows/ci.yml\n"
        build, reason, written, calls = self.select(BASE, changed)
        self.assertFalse(build)
        self.assertEqual((written, calls), ("", []))
        self.assertIn(BASE[:12], reason)

    def test_compiled_inputs_select_profiles_over_the_whole_range(self):
        for path in ("src/Honua.Server/Features/Admin/Scene/A.cs", "eng/Honua.ProtocolProfiles.props",
                     "Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.config"):
            build, _, written, calls = self.select(BASE, f"docs/a.md\n{path}\n")
            self.assertTrue(build, path)
            self.assertEqual(written, SELECTION)
            command, options = calls[0]
            self.assertEqual(command[-1], "--changed-from-git")
            self.assertEqual((options["env"]["BASE_REF"], options["env"]["HEAD_REF"]), (BASE, HEAD))


class EntryPointTests(unittest.TestCase):
    def test_base_is_newest_successful_sanity_ancestor(self):
        older = "a" * 40
        runs = [{"head_sha": sha, "conclusion": "success"} for sha in ("d" * 40, older, BASE)]
        with tempfile.TemporaryDirectory() as directory:
            output, summary = Path(directory) / "output", Path(directory) / "summary"
            environment = {"GITHUB_OUTPUT": str(output), "GITHUB_STEP_SUMMARY": str(summary)}
            with patch.dict(os.environ, environment), \
                    patch.object(sys, "argv", ["profiles", HEAD, f"{directory}/selection.json"]), \
                    patch.object(profiles.completed_ci_base, "runs", return_value=runs) as listing, \
                    patch.object(profiles.completed_ci_base, "ancestor", side_effect=lambda a, _: a != "d" * 40), \
                    patch.object(profiles, "compiled_profiles", side_effect=lambda run: run["head_sha"] != older), \
                    patch.object(profiles, "select", return_value=(False, "skipped")) as select:
                profiles.main()
            self.assertEqual(output.read_text(), "build=false\n")
            self.assertEqual(summary.read_text(), "skipped\n")
        listing.assert_called_once_with("trunk-sanity.yml", "success")
        self.assertEqual(select.call_args.args[:2], (BASE, HEAD))


    def test_runs_before_the_profile_job_existed_do_not_count(self):
        script_commit = profiles.subprocess.check_output(
            ["git", "log", "-1", "--format=%H", "--", profiles.SCRIPT], cwd=ROOT, text=True).strip()
        if script_commit:
            self.assertTrue(profiles.compiled_profiles({"head_sha": script_commit}))
        old = profiles.subprocess.check_output(
            ["git", "rev-list", "--max-parents=0", "HEAD"], cwd=ROOT, text=True).split()[0]
        self.assertFalse(profiles.compiled_profiles({"head_sha": old}))


class WorkflowTests(unittest.TestCase):
    workflow = yaml.safe_load((ROOT / ".github/workflows/trunk-sanity.yml").read_text())

    def test_jobs_queue_the_newest_push_instead_of_cancelling(self):
        self.assertNotIn("concurrency", self.workflow)
        groups = set()
        for job in self.workflow["jobs"].values():
            self.assertEqual(job["concurrency"], {**job["concurrency"], "cancel-in-progress": False})
            groups.add(job["concurrency"]["group"])
        self.assertEqual(len(groups), len(self.workflow["jobs"]))

    def test_profile_build_runs_only_on_the_selected_range(self):
        steps = self.workflow["jobs"]["protocol-profiles"]["steps"]
        select = next(step for step in steps if step.get("id") == "select")
        self.assertIn("scripts/ci/trunk_sanity_profiles.py", select["run"])
        build = next(step for step in steps if step.get("name") == "Build selected protocol profiles")
        self.assertEqual(build["if"], "steps.select.outputs.build == 'true'")
        self.assertIn("build-selected-protocol-profiles.py", build["run"])


if __name__ == "__main__":
    unittest.main()
