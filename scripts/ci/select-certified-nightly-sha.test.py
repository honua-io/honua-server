#!/usr/bin/env python3
"""Offline tests for select-certified-nightly-sha.py.

No network: every GitHub read is answered from an in-memory recording shaped like the REST
payloads the resolver reads (commits, actions/runs?head_sha=, commits/<sha>/check-runs). The
live-repository cases read the real .github/workflows/ci.yml, so a lane renamed or dropped
there fails here as well as in the nightly self-test step.
"""

from __future__ import annotations

import importlib.util
import io
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).with_name("select-certified-nightly-sha.py")
SPEC = importlib.util.spec_from_file_location("select_certified_nightly_sha", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

REPO_ROOT = Path(__file__).resolve().parents[2]
CI = ".github/workflows/ci.yml"
REPOSITORY = "honua-io/honua-server"
# honua-release certification/full-matrix-checks.yaml requires exactly these check-run names.
RESOLVER_LANES = ["Build & Format Check", ".NET Foundation Tests", "Python Integration Tests",
                  "Test Suite Summary", "CI Gate"]


def sha(seed: str) -> str:
    return (seed * 40)[:40]


class Recording:
    """A fake `gh api` answering the selector's reads from per-sha fixtures."""

    def __init__(self, trunk: list[str]) -> None:
        self.trunk = trunk
        self.runs: dict[str, list[dict]] = {s: [] for s in trunk}
        self.checks: dict[str, list[dict]] = {s: [] for s in trunk}
        self.calls: list[str] = []
        self._ids = iter(range(1000, 100000))

    def run(self, commit: str, *, event="workflow_dispatch", status="completed", conclusion="success",
            updated="2026-10-04T05:00:00Z", path=CI, lanes=None, skipped=(), failed=()) -> int:
        run_id = next(self._ids)
        self.runs[commit].append({"id": run_id, "path": path, "event": event, "status": status,
                                  "conclusion": conclusion if status == "completed" else None,
                                  "head_sha": commit, "updated_at": updated,
                                  "html_url": f"https://github.com/{REPOSITORY}/actions/runs/{run_id}"})
        for lane in RESOLVER_LANES if lanes is None else lanes:
            result = "skipped" if lane in skipped else "failure" if lane in failed else "success"
            self.check(commit, run_id, lane, result)
        return run_id

    def check(self, commit: str, run_id: int, name: str, conclusion: str, status="completed") -> None:
        self.checks[commit].append({
            "id": next(self._ids), "name": name, "status": status, "conclusion": conclusion,
            "started_at": "2026-10-04T05:00:00Z",
            "details_url": f"https://github.com/{REPOSITORY}/actions/runs/{run_id}/job/{next(self._ids)}"})

    def __call__(self, path: str) -> object:
        self.calls.append(path)
        base, _, query = path.partition("?")
        params = dict(part.split("=", 1) for part in query.split("&") if part)
        page = int(params.get("page", "1"))
        if base == f"repos/{REPOSITORY}/commits":
            return [{"sha": s} for s in self.trunk[(page - 1) * 100:page * 100]]
        if base == f"repos/{REPOSITORY}/actions/runs":
            rows = self.runs.get(params["head_sha"], [])
            return {"total_count": len(rows), "workflow_runs": rows[(page - 1) * 100:page * 100]}
        if base.startswith(f"repos/{REPOSITORY}/commits/") and base.endswith("/check-runs"):
            rows = self.checks.get(base.split("/")[4], [])
            return {"total_count": len(rows), "check_runs": rows[(page - 1) * 100:page * 100]}
        if base.startswith(f"repos/{REPOSITORY}/compare/"):
            commit = base.rsplit("/", 1)[1].split("...")[0]
            return {"status": "ahead" if commit in self.trunk else "diverged"}
        raise AssertionError(f"unexpected read {path}")


def choose(recording: Recording, **kwargs):
    log: list[str] = []
    github = MODULE.GitHub(REPOSITORY, fetch=recording)
    chosen, run = MODULE.select(github, branch="trunk", limit=kwargs.pop("limit", 100), workflow=CI,
                                lanes=RESOLVER_LANES, log=log.append, **kwargs)
    return chosen, run, log


class LaneListTests(unittest.TestCase):
    def test_live_ci_yml_declares_exactly_the_resolver_lanes(self) -> None:
        workflow = (REPO_ROOT / CI).read_text(encoding="utf-8")
        self.assertEqual(MODULE.certification_lanes(workflow), RESOLVER_LANES)

    def test_unknown_job_id_refuses(self) -> None:
        workflow = "env:\n  CERTIFICATION_LANE_JOBS: 'build gone'\njobs:\n  build:\n    name: Build & Format Check\n"
        with self.assertRaisesRegex(MODULE.SelectionError, "'gone'"):
            MODULE.certification_lanes(workflow)

    def test_expression_name_refuses(self) -> None:
        workflow = ("env:\n  CERTIFICATION_LANE_JOBS: 'shards'\njobs:\n  shards:\n"
                    "    name: Server Tests (${{ matrix.shard_name }})\n")
        with self.assertRaisesRegex(MODULE.SelectionError, "expression name"):
            MODULE.certification_lanes(workflow)

    def test_missing_declaration_refuses(self) -> None:
        with self.assertRaisesRegex(MODULE.SelectionError, "CERTIFICATION_LANE_JOBS"):
            MODULE.certification_lanes("env:\n  OTHER: x\njobs:\n  build:\n    name: Build\n")


class SelectionTests(unittest.TestCase):
    def test_head_without_a_completed_matrix_is_not_imaged(self) -> None:
        head, certified = sha("a"), sha("b")
        recording = Recording([head, certified])
        recording.run(head, status="in_progress")
        run_id = recording.run(certified)
        chosen, run, log = choose(recording)
        self.assertEqual(chosen, certified)
        self.assertEqual(run["id"], run_id)
        self.assertTrue(any("1 still running" in line for line in log), log)

    def test_newest_certified_sha_wins_and_the_walk_stops_there(self) -> None:
        newest, older = sha("c"), sha("d")
        recording = Recording([newest, older])
        recording.run(newest)
        recording.run(older)
        chosen, _, _ = choose(recording)
        self.assertEqual(chosen, newest)
        self.assertFalse(any(older in call for call in recording.calls), "an older sha must not be read")

    def test_newer_skipped_lane_run_supersedes_an_older_full_matrix(self) -> None:
        # 7aad613 on 2026-10-04: full run 37181115405 green, then an empty-range re-dispatch
        # 37183672893 skipped Build/Foundation/Python and concluded success.
        reverified, certified = sha("e"), sha("f")
        recording = Recording([reverified, certified])
        recording.run(reverified, updated="2026-10-04T06:00:00Z")
        recording.run(reverified, updated="2026-10-04T07:00:00Z",
                      skipped={"Build & Format Check", ".NET Foundation Tests", "Python Integration Tests"})
        recording.run(certified)
        chosen, _, log = choose(recording)
        self.assertEqual(chosen, certified)
        skip = next(line for line in log if line.strip().startswith(reverified[:7]))
        self.assertIn("lanes not successful", skip)
        self.assertIn("Build & Format Check=skipped", skip)

    def test_selective_run_missing_lanes_is_named(self) -> None:
        selective, certified = sha("1"), sha("2")
        recording = Recording([selective, certified])
        recording.run(selective, lanes=["Test Suite Summary", "CI Gate"])
        recording.run(certified)
        _, _, log = choose(recording)
        skip = next(line for line in log if line.strip().startswith(selective[:7]))
        self.assertIn("missing lanes ['Build & Format Check', '.NET Foundation Tests', "
                      "'Python Integration Tests']", skip)

    def test_only_schedule_and_dispatch_runs_of_ci_yml_certify(self) -> None:
        commit = sha("3")
        recording = Recording([commit])
        recording.run(commit, event="merge_group")
        recording.run(commit, path=".github/workflows/pr-gate.yml")
        with self.assertRaisesRegex(MODULE.SelectionError, "no completed"):
            choose(recording)
        recording.run(commit, event="schedule")
        self.assertEqual(choose(recording)[0], commit)

    def test_failed_run_conclusion_does_not_certify_even_with_green_lanes(self) -> None:
        commit = sha("4")
        recording = Recording([commit])
        recording.run(commit, conclusion="failure")
        with self.assertRaisesRegex(MODULE.SelectionError, "run conclusion=failure"):
            choose(recording)

    def test_latest_attempt_of_a_rerun_lane_decides(self) -> None:
        commit = sha("5")
        recording = Recording([commit])
        run_id = recording.run(commit, failed={"Python Integration Tests"})
        recording.check(commit, run_id, "Python Integration Tests", "success")
        self.assertEqual(choose(recording)[0], commit)
        recording.check(commit, run_id, "Python Integration Tests", "failure")
        with self.assertRaisesRegex(MODULE.SelectionError, "Python Integration Tests=failure"):
            choose(recording)

    def test_lanes_from_another_run_on_the_same_sha_do_not_count(self) -> None:
        commit = sha("6")
        recording = Recording([commit])
        old = recording.run(commit, updated="2026-10-04T01:00:00Z")
        recording.run(commit, updated="2026-10-04T02:00:00Z", lanes=["Test Suite Summary", "CI Gate"])
        self.assertTrue(old)
        with self.assertRaisesRegex(MODULE.SelectionError, "missing lanes"):
            choose(recording)

    def test_no_qualifying_commit_fails_loudly_naming_lanes(self) -> None:
        commits = [sha(c) for c in "789"]
        recording = Recording(commits)
        for commit in commits:
            recording.run(commit, skipped={"Python Integration Tests"})
        with self.assertRaises(MODULE.SelectionError) as raised:
            choose(recording, limit=2)
        message = str(raised.exception)
        self.assertIn("no trunk commit in the newest 2", message)
        self.assertIn("Python Integration Tests=skipped", message)
        self.assertIn(commits[0][:7], message)
        self.assertNotIn(commits[2][:7], message, "the walk is bounded by --limit")

    def test_requested_sha_must_be_on_trunk_and_certified(self) -> None:
        head, certified, uncertified = sha("a"), sha("b"), sha("c")
        recording = Recording([head, certified, uncertified])
        recording.run(head)
        recording.run(certified)
        recording.run(uncertified, skipped={"CI Gate"})
        self.assertEqual(choose(recording, sha=certified)[0], certified)
        with self.assertRaisesRegex(MODULE.SelectionError, "is not certified"):
            choose(recording, sha=uncertified)
        with self.assertRaisesRegex(MODULE.SelectionError, "not on trunk"):
            choose(recording, sha=sha("f"))
        with self.assertRaisesRegex(MODULE.SelectionError, "40-hex"):
            choose(recording, sha="ff5f567")

    def test_main_writes_outputs_and_names_the_certifying_run(self) -> None:
        commit = sha("d")
        recording = Recording([commit])
        run_id = recording.run(commit)
        original = MODULE.GitHub.__init__

        def recorded(self, repository, fetch=None):
            original(self, repository, fetch=recording)

        import os
        import tempfile
        with tempfile.TemporaryDirectory() as scratch:
            output = Path(scratch) / "output"
            previous = os.environ.get("GITHUB_OUTPUT")
            os.environ["GITHUB_OUTPUT"] = str(output)
            MODULE.GitHub.__init__ = recorded
            cwd = os.getcwd()
            os.chdir(REPO_ROOT)  # --workflow is repo-relative: it is also the runs' `path`
            stdout = io.StringIO()
            try:
                with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
                    code = MODULE.main(["--repository", REPOSITORY, "--workflow", CI])
            finally:
                os.chdir(cwd)
                MODULE.GitHub.__init__ = original
                if previous is None:
                    os.environ.pop("GITHUB_OUTPUT", None)
                else:
                    os.environ["GITHUB_OUTPUT"] = previous
            self.assertEqual(code, 0)
            self.assertIn(f"Selected {commit} (nightly-{commit[:7]}), certified by", stdout.getvalue())
            self.assertIn(str(run_id), stdout.getvalue())
            written = dict(line.split("=", 1) for line in output.read_text().splitlines())
            self.assertEqual(written["sha"], commit)
            self.assertEqual(written["sha7"], commit[:7])
            self.assertEqual(written["run_id"], str(run_id))


if __name__ == "__main__":
    unittest.main(verbosity=2)
