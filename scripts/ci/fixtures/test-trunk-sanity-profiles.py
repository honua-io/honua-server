#!/usr/bin/env python3
"""Trunk Sanity compiles reduced protocol profiles for every compiled change."""

import os
from pathlib import Path
import subprocess
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

    def test_range_of_workflows_and_prose_skips_profiles(self):
        changed = "docs/guide.md\nREADME.md\n.github/workflows/ci.yml\n.github/ci-shards.json\n"
        build, reason, written, calls = self.select(BASE, changed)
        self.assertFalse(build)
        self.assertEqual((written, calls), ("", []))
        self.assertIn(BASE[:12], reason)

    def test_compiled_inputs_compile_every_profile_over_the_whole_range(self):
        for path in ("src/Honua.Server/Features/Admin/Scene/A.cs", "eng/Honua.ProtocolProfiles.props",
                     "Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.config",
                     ".editorconfig", "scripts/ci/analyzer-baseline/BannedSymbols.txt",
                     "docs/developer/api-specs/admin-api.json", "skills/a/SKILL.md",
                     "src/Honua.Server/Resources/Notes.md"):
            build, _, written, calls = self.select(BASE, f"docs/a.md\n{path}\n")
            self.assertTrue(build, path)
            self.assertEqual(written, SELECTION)
            self.assertEqual(calls[0][0][-1], "--all")

    def test_real_selection_builds_every_reduced_profile(self):
        grpc = "src/Honua.Server/Features/Protocols/Grpc/GrpcServiceCollectionExtensions.cs\n"
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(profiles, "compiled_changes", return_value=[grpc]):
            selection = Path(directory) / "selection.json"
            self.assertTrue(profiles.select(BASE, HEAD, selection)[0])
            built = profiles.subprocess.check_output(
                [sys.executable, str(ROOT / "scripts/ci/build-selected-protocol-profiles.py"),
                 "--dry-run", "--selection", str(selection)], text=True)
        for profile in ("gsr", "ogc-api", "ogc-api-classic"):
            self.assertIn(f"-p:HonuaProtocolProfile={profile} ", built)


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
        with tempfile.TemporaryDirectory() as directory:
            def commit(message):
                subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit",
                                "-q", "--allow-empty", "-m", message], cwd=directory, check=True)
                return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=directory, text=True).strip()
            subprocess.run(["git", "init", "-q", directory], check=True)
            older = commit("before the profile job")
            script = Path(directory) / profiles.SCRIPT
            script.parent.mkdir(parents=True)
            script.write_text("")
            subprocess.run(["git", "add", profiles.SCRIPT], cwd=directory, check=True)
            newer = commit("adds the profile job")
            previous = os.getcwd()
            os.chdir(directory)
            try:
                self.assertEqual([profiles.compiled_profiles({"head_sha": sha}) for sha in (older, newer)],
                                 [False, True])
            finally:
                os.chdir(previous)


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
