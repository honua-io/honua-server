#!/usr/bin/env python3
"""Keep the post-merge affected run event-driven, queued, least-privilege and visible."""

from pathlib import Path
import json
import os
import subprocess
import tempfile
import sys
import unittest
from unittest.mock import patch

import importlib.util

import yaml


ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github/workflows"
SUSPECT_RANGE = ROOT / "scripts/ci/trunk-suspect-range.sh"
CERTIFIED_SELECTOR = ROOT / "scripts/ci/select-certified-nightly-sha.py"


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

    def test_dispatches_ci_from_completed_head_and_reports_green_range(self):
        scripts = step_scripts(self.job)
        self.assertIn("actions/workflows/ci.yml/runs?branch=trunk&status=success", scripts)
        self.assertIn("git merge-base --is-ancestor", scripts)
        self.assertIn("actions/workflows/ci.yml/dispatches", scripts)
        self.assertIn("scripts/ci/completed_ci_base.py", scripts)
        self.assertIn("inputs[selective_base]=${COMPLETED_BASE}", scripts)
        self.assertIn("inputs[investigation_base]=${LAST_GREEN}", scripts)
        self.assertIn("scripts/ci/trunk-suspect-range.sh", scripts)

    def test_only_selective_runs_carry_the_title_certification_ignores(self):
        sys.path.insert(0, str(ROOT / "scripts/ci"))
        spec = importlib.util.spec_from_file_location("certified_selector", CERTIFIED_SELECTOR)
        selector = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(selector)
        self.assertEqual(workflow("ci.yml")["run-name"], "CI ${{ inputs.merge_train_nonce }}")
        scripts = step_scripts(self.job)
        selective_title = 'run_title="affected ${COMPLETED_BASE:0:12}..${HEAD_SHA:0:12}"'
        self.assertIn(selective_title, scripts)
        self.assertEqual(selector.AFFECTED_RUN_TITLE_PREFIX, "CI affected ")
        titles = [line.strip() for line in scripts.splitlines() if line.strip().startswith("run_title=")]
        self.assertEqual([t for t in titles if t.startswith('run_title="affected ')], [selective_title])

    def test_selective_ci_runs_never_replace_a_pending_full_run(self):
        ci = workflow("ci.yml")
        self.assertIn("selective_base", ci["on"]["workflow_dispatch"]["inputs"])
        self.assertIn("inputs.selective_base && '-affected'", ci["concurrency"]["group"])
        range_steps = [step for step in ci["jobs"]["changes"]["steps"]
                       if "trunk-suspect-range.sh" in step.get("run", "")]
        self.assertEqual(len(range_steps), 1)
        self.assertEqual(range_steps[0]["if"], "steps.diff.outputs.trailing_selective == 'true'")


class CompletedBaseExecutionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.clone = Path(self.directory.name)
        self.env = dict(os.environ, GITHUB_REPOSITORY="honua-io/honua-server")
        self.env.update(GITHUB_OUTPUT=str(self.clone / "output"),
                        GITHUB_STEP_SUMMARY=str(self.clone / "summary"))
        self.git("init", "-q", "-b", "trunk")
        self.heads = []
        for index in range(3):
            self.git("commit", "-q", "--allow-empty", "-m", f"change {index}")
            self.heads.append(self.git("rev-parse", "HEAD"))
        gh = self.clone / "gh"
        gh.write_text('#!/bin/bash\n'
                      'if [[ "$2" == *"status=success"* ]]; then\n'
                      '  jq -r "$4" green.json\n'
                      'else\n'
                      '  cat completed.json\n'
                      'fi\n')
        gh.chmod(0o755)
        self.env["PATH"] = f"{self.clone}:{os.environ['PATH']}"
        helper = self.clone / "scripts/ci/completed_ci_base.py"
        helper.parent.mkdir(parents=True)
        helper.write_bytes((ROOT / "scripts/ci/completed_ci_base.py").read_bytes())
        self.env["PUSHED_HEAD"] = self.heads[-1]
        self.script = workflow("trunk-affected.yml")["jobs"]["dispatch"]["steps"][1]["run"]

    def git(self, *args):
        return subprocess.run(["git", "-c", "user.name=ci", "-c", "user.email=ci@example.invalid", *args],
                              cwd=self.clone, check=True, capture_output=True, text=True).stdout.strip()

    def resolve(self, completed, green):
        for name, runs in (("completed", completed), ("green", green)):
            (self.clone / f"{name}.json").write_text(json.dumps({"workflow_runs": runs}))
        subprocess.run(["bash", "-e", "-c", self.script], cwd=self.clone, env=self.env,
                       check=True, capture_output=True, text=True)
        return dict(line.split("=", 1) for line in (self.clone / "output").read_text().splitlines())

    def run_record(self, index, conclusion, updated):
        return {"head_sha": self.heads[index], "conclusion": conclusion, "updated_at": updated}

    def test_failed_completed_head_bounds_execution_without_moving_green(self):
        green = self.run_record(0, "success", "2026-11-10T12:00:00Z")
        failed = self.run_record(1, "failure", "2026-11-10T13:00:00Z")
        cancelled = self.run_record(2, "cancelled", "2026-11-10T14:00:00Z")
        outputs = self.resolve([green, cancelled, failed], [green])
        self.assertEqual(outputs["completed_base"], self.heads[1])
        self.assertEqual(outputs["last_green"], self.heads[0])
        self.assertEqual(outputs["head"], self.heads[2])

    def test_successful_selective_run_cannot_hide_an_earlier_failure(self):
        full_green = self.run_record(0, "success", "2026-11-10T12:00:00Z")
        selective_green = self.run_record(1, "success", "2026-11-10T14:00:00Z")
        selective_green["display_title"] = "CI affected failed..unrelated"
        outputs = self.resolve([selective_green], [selective_green, full_green])
        self.assertEqual(outputs["completed_base"], self.heads[1])
        self.assertEqual(outputs["last_green"], self.heads[0])

    def test_newest_completion_time_wins_over_api_creation_order(self):
        outputs = self.resolve([self.run_record(0, "success", "2026-11-10T12:00:00Z"),
                                self.run_record(1, "success", "2026-11-10T13:00:00Z")], [])
        self.assertEqual(outputs["completed_base"], self.heads[1])
        self.assertEqual(outputs["last_green"], "")

    def test_missing_completed_history_requests_full_fallback(self):
        outputs = self.resolve([], [])
        self.assertEqual(outputs["completed_base"], "")
        self.assertEqual(outputs["last_green"], "")

    def test_nonancestor_and_malformed_heads_do_not_advance_base(self):
        ancestor = self.run_record(0, "success", "2026-11-10T12:00:00Z")
        self.git("checkout", "-q", "-b", "side", self.heads[0])
        self.git("commit", "-q", "--allow-empty", "-m", "side")
        foreign = self.run_record(1, "failure", "2026-11-10T13:00:00Z")
        foreign["head_sha"] = self.git("rev-parse", "HEAD")
        self.git("checkout", "-q", "trunk")
        malformed = dict(foreign, head_sha="invalid", updated_at="2026-11-10T14:00:00Z")
        outputs = self.resolve([malformed, foreign, ancestor], [ancestor])
        self.assertEqual(outputs["completed_base"], self.heads[0])

    def test_superseded_push_does_not_dispatch(self):
        self.env["PUSHED_HEAD"] = self.heads[0]
        self.assertEqual(self.resolve([], []), {"superseded": "true"})

    def run_investigation_summary(self, base):
        helper = self.clone / "scripts/ci/trunk-suspect-range.sh"
        helper.parent.mkdir(parents=True, exist_ok=True)
        helper.write_bytes(SUSPECT_RANGE.read_bytes())
        helper.chmod(0o755)
        self.env.update(INVESTIGATION_BASE=base, GITHUB_SHA=self.heads[-1])
        steps = workflow("ci.yml")["jobs"]["changes"]["steps"]
        step = next(step for step in steps if step.get("name") == "Record suspect merge range")
        self.assertEqual(step["env"]["INVESTIGATION_BASE"], "${{ inputs.investigation_base }}")
        subprocess.run(["bash", "-e", "-c", step["run"]], cwd=self.clone, env=self.env,
                       check=True, capture_output=True, text=True)
        return (self.clone / "summary").read_text()

    def test_ci_doctor_summary_keeps_failed_merge_in_green_range(self):
        summary = self.run_investigation_summary(self.heads[0])
        self.assertIn(f"`{self.heads[0]}`..`{self.heads[2]}` (2 merges", summary)
        self.assertIn("change 1", summary)
        self.assertIn("change 2", summary)

    def test_missing_green_does_not_mislabel_execution_base_as_green(self):
        self.assertIn("ancestor unknown", self.run_investigation_summary(""))

    def test_both_daily_full_schedules_remain_enabled(self):
        self.assertEqual(workflow("ci.yml")["on"]["schedule"],
                         [{"cron": "23 9 * * *"}, {"cron": "23 21 * * *"}])


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


class RuntimeBaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "scripts/ci"))
        spec = importlib.util.spec_from_file_location(
            "runtime_base", ROOT / "scripts/ci/resolve-trailing-base.py")
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)
        cls.selector = sys.modules["completed_ci_base"]

    def resolve(self, records, ancestors):
        with patch.dict(os.environ, GITHUB_REPOSITORY="honua-io/honua-server"), \
                patch.object(self.module.subprocess, "check_output", return_value=json.dumps(
                    {"workflow_runs": records})), \
                patch.object(self.selector, "ancestor", side_effect=lambda a, b: (a, b) in ancestors):
            return self.selector.completed_base("d" * 40, "a" * 40)

    def record(self, letter, conclusion="failure", updated="2"):
        return dict(head_sha=letter * 40, conclusion=conclusion, updated_at=updated)

    def test_completion_after_dispatch_advances_execution_base(self):
        self.assertEqual(self.resolve([self.record("b")],
                                     {("a" * 40, "b" * 40), ("b" * 40, "d" * 40)}), "b" * 40)

    def test_old_newer_foreign_and_cancelled_runs_do_not_regress_base(self):
        records = [self.record("b", "cancelled"), self.record("c"), self.record("e")]
        self.assertEqual(self.resolve(records, {("a" * 40, "b" * 40),
                                              ("b" * 40, "d" * 40),
                                              ("a" * 40, "e" * 40)}), "a" * 40)

    def test_history_outage_retains_dispatched_scope(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch.dict(os.environ, GITHUB_OUTPUT=f"{directory}/output",
                           GITHUB_STEP_SUMMARY=f"{directory}/summary"), \
                patch.object(sys, "argv", ["helper", "base", "head"]), \
                patch.object(self.module, "completed_base",
                             side_effect=subprocess.CalledProcessError(1, "gh")), \
                patch.object(self.module, "execution_base", return_value=("base", "unchanged")) as resolve:
            self.module.main()
            resolve.assert_called_once_with("base", "head")
            self.assertIn("base=base", Path(f"{directory}/output").read_text())

    def test_small_selection_keeps_accumulated_range(self):
        with patch.object(self.module, "selected_count", return_value=30):
            base, reason = self.module.execution_base("base", "head")
        self.assertEqual(base, "base")
        self.assertIn("30 shards", reason)

    def test_large_selection_uses_entire_newest_merge_and_records_deferral(self):
        with patch.object(self.module, "selected_count", side_effect=[160, 4]), \
                patch.object(self.module, "git", return_value="first-parent") as git:
            base, reason = self.module.execution_base("base", "head")
        self.assertEqual(base, "first-parent")
        git.assert_called_once_with("rev-parse", "head^1")
        self.assertIn("deferred to scheduled full CI", reason)

    def test_broad_newest_merge_is_never_arbitrarily_truncated(self):
        with patch.object(self.module, "selected_count", side_effect=[160, 150]), \
                patch.object(self.module, "git", return_value="parent"):
            base, reason = self.module.execution_base("base", "head")
        self.assertEqual(base, "parent")
        self.assertIn("retain all of its shards", reason)

    def test_router_failure_fails_selection_without_silently_skipping_tests(self):
        with patch.object(self.module, "selected_count",
                          side_effect=subprocess.CalledProcessError(1, "router")):
            with self.assertRaises(subprocess.CalledProcessError):
                self.module.execution_base("base", "head")

    def test_effective_base_reaches_routing_and_build_with_least_privilege(self):
        jobs = workflow("ci.yml")["jobs"]
        changes = jobs["changes"]
        self.assertEqual(changes["permissions"], {"contents": "read", "actions": "read"})
        self.assertEqual(changes["outputs"]["selective_base"], "${{ steps.diff.outputs.base }}")
        self.assertIn("resolve-trailing-base.py", step_scripts(changes))
        for job_name in ("targeted-shards", "build"):
            self.assertIn("needs.changes.outputs.selective_base", json.dumps(jobs[job_name]))
        self.assertEqual(jobs["server-tests"]["strategy"]["max-parallel"],
                         "${{ inputs.selective_base && "
                         "fromJson(needs.targeted-shards.outputs.affected_max_parallel) || 24 }}")


FULL_HEAD, PRIOR_HEAD, NEWER_HEAD = "c" * 40, "b" * 40, "d" * 40


def trunk_run(run_id, head, title):
    event = "schedule" if title == "CI " else "workflow_dispatch"
    return {"id": run_id, "head_sha": head, "display_title": title, "status": "in_progress",
            "event": event}


class SupersedeTrailingRunsTests(unittest.TestCase):
    """Never let one trunk head hold a full and a selective matrix of hosted runners."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "scripts/ci"))
        spec = importlib.util.spec_from_file_location(
            "supersede_trailing_runs", ROOT / "scripts/ci/supersede_trailing_runs.py")
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def contains(self, covering_head, head):
        order = [PRIOR_HEAD, FULL_HEAD, NEWER_HEAD]
        return order.index(head) <= order.index(covering_head)

    def decide(self, strategy, runs, own_id, own_head):
        with patch.object(self.module, "contains", side_effect=self.contains):
            return strategy(runs, own_id, own_head)

    def test_full_run_cancels_selective_runs_at_or_before_its_head_only(self):
        runs = [trunk_run(1, FULL_HEAD, "CI "),
                trunk_run(2, FULL_HEAD, "CI affected aaaa..cccc"),
                {**trunk_run(3, PRIOR_HEAD, "CI affected aaaa..bbbb"), "status": "pending"},
                trunk_run(4, NEWER_HEAD, "CI affected cccc..dddd"),
                trunk_run(5, PRIOR_HEAD, "CI ")]
        message, cancellations = self.decide(self.module.supersede_from_full_run, runs, 1, FULL_HEAD)
        self.assertEqual(cancellations, [2, 3])
        self.assertIn("(2)", message)

    def test_full_run_without_covered_selective_runs_cancels_nothing(self):
        runs = [trunk_run(1, FULL_HEAD, "CI "), trunk_run(4, NEWER_HEAD, "CI affected cccc..dddd")]
        _, cancellations = self.decide(self.module.supersede_from_full_run, runs, 1, FULL_HEAD)
        self.assertEqual(cancellations, [])

    def test_selective_run_cancels_itself_under_a_started_covering_full_run(self):
        for status in ("in_progress", "queued"):
            runs = [{**trunk_run(9, NEWER_HEAD, "CI "), "status": status},
                    trunk_run(2, FULL_HEAD, "CI affected aaaa..cccc")]
            message, cancellations = self.decide(
                self.module.supersede_own_selective_run, runs, 2, FULL_HEAD)
            self.assertEqual(cancellations, [2], status)
            self.assertIn("covers this head", message)

    def test_selective_run_continues_without_a_started_covering_full_run(self):
        cases = {"full run held by concurrency": [{**trunk_run(9, NEWER_HEAD, "CI "), "status": "pending"}],
                 "full run at an older head": [trunk_run(9, PRIOR_HEAD, "CI ")],
                 "only other selective runs": [trunk_run(4, NEWER_HEAD, "CI affected cccc..dddd")],
                 "titled dispatch of unknown scope": [trunk_run(9, NEWER_HEAD, "CI train-42")],
                 "manual dispatch without a title": [
                     {**trunk_run(9, NEWER_HEAD, "CI "), "event": "workflow_dispatch"}]}
        for case, runs in cases.items():
            own = trunk_run(2, FULL_HEAD, "CI affected aaaa..cccc")
            _, cancellations = self.decide(
                self.module.supersede_own_selective_run, runs + [own], 2, FULL_HEAD)
            self.assertEqual(cancellations, [], case)

    def test_compare_status_decides_containment(self):
        responses = {"ahead": True, "identical": True, "behind": False, "diverged": False}
        for status, expected in responses.items():
            with patch.dict(os.environ, {"GITHUB_REPOSITORY": "honua-io/honua-server"}), \
                    patch.object(self.module, "gh_api", return_value={"status": status}) as api:
                self.assertEqual(self.module.contains(FULL_HEAD, PRIOR_HEAD), expected, status)
            api.assert_called_once_with(
                f"repos/honua-io/honua-server/compare/{PRIOR_HEAD}...{FULL_HEAD}")

    def run_main(self, scope, gh_api):
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "summary.md"
            environment = {"GITHUB_RUN_ID": "2", "GITHUB_SHA": FULL_HEAD,
                           "GITHUB_REPOSITORY": "honua-io/honua-server",
                           "GITHUB_STEP_SUMMARY": str(summary)}
            with patch.dict(os.environ, environment), \
                    patch.object(sys, "argv", ["supersede_trailing_runs.py", scope]), \
                    patch.object(self.module, "gh_api", side_effect=gh_api), \
                    patch("sys.stdout"):
                self.module.main()
            return summary.read_text()

    def test_summary_is_written_before_a_self_cancellation(self):
        observed = []

        def gh_api(endpoint, method="GET"):
            if method == "POST":
                observed.append((endpoint, Path(os.environ["GITHUB_STEP_SUMMARY"]).read_text()))
                return {}
            if "status=in_progress" in endpoint:
                return {"workflow_runs": [trunk_run(9, FULL_HEAD, "CI ")]}
            return {"workflow_runs": []}

        self.run_main("selective", gh_api)
        self.assertEqual(len(observed), 1)
        endpoint, summary_at_cancel = observed[0]
        self.assertTrue(endpoint.endswith("actions/runs/2/cancel"))
        self.assertIn("covers this head", summary_at_cancel)

    def test_unavailable_history_cancels_nothing_and_warns(self):
        def gh_api(endpoint, method="GET"):
            if method == "POST":
                raise AssertionError("no run may be cancelled without history")
            raise subprocess.CalledProcessError(1, "gh")

        self.assertIn("history unavailable", self.run_main("full", gh_api))

    def test_failed_cancellation_warns_without_failing(self):
        def gh_api(endpoint, method="GET"):
            if method == "POST":
                raise subprocess.CalledProcessError(1, "gh")
            if "status=in_progress" in endpoint:
                return {"workflow_runs": [trunk_run(3, FULL_HEAD, "CI affected aaaa..cccc")]}
            return {"workflow_runs": []}

        self.assertIn("Cancelling selective runs", self.run_main("full", gh_api))

    def test_job_runs_on_trunk_full_and_selective_runs_with_cancel_permission(self):
        job = workflow("ci.yml")["jobs"]["supersede-trailing-runs"]
        self.assertEqual(job["permissions"], {"actions": "write", "contents": "read"})
        self.assertIn("github.ref == 'refs/heads/trunk'", job["if"])
        self.assertNotIn("pull_request", job["if"])
        step = job["steps"][-1]
        self.assertEqual(step["env"]["RUN_SCOPE"],
                         "${{ (inputs.selective_base && !inputs.full_ci) && 'selective' || 'full' }}")
        self.assertIn('supersede_trailing_runs.py "${RUN_SCOPE}"', step["run"])


if __name__ == "__main__":
    unittest.main()
