#!/usr/bin/env python3
"""Exercise draft admission and cancellation in the actual PR workflows."""

import json
import os
from pathlib import Path
import subprocess
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github/workflows"
HEAVY = (
    "codeql.yml", "compose-release-lifecycle.yml", "geoarrow-interop-fixture.yml",
    "geoparquet-interop.yml", "ogc-api-building-block-conformance.yml",
    "serving-image-boundary.yml", "worker-gdal-image.yml",
)
DRAFT_GUARD = "github.event_name != 'pull_request' || !github.event.pull_request.draft"


def workflow(name):
    return yaml.load((WORKFLOWS / name).read_text(), Loader=yaml.BaseLoader)


def workflow_jobs(names):
    for name in names:
        for key, job in workflow(name)["jobs"].items():
            yield name, key, job


def job_scripts(job):
    return "\n".join(step.get("with", {}).get("script", "") for step in job["steps"])


def pr_admissions():
    for path in sorted(WORKFLOWS.glob("*.y*ml")):
        definition = workflow(path.name)
        triggers = definition.get("on", {})
        if "pull_request" not in triggers and "pull_request_target" not in triggers:
            continue
        for job in definition["jobs"].values():
            if not job.get("needs"):
                yield path.name, definition.get("concurrency", job.get("concurrency", {}))


def evaluate(condition, event, context=None):
    context = context or {}
    expression = condition.removeprefix("${{").removesuffix("}}").strip()
    expression = expression.replace("needs.runner-select", 'needs["runner-select"]')
    for step_id in ("docs-only", "generated-only"):
        expression = expression.replace(f"steps.{step_id}", f'steps["{step_id}"]')
    probe = """const {expression, github, needs, cancelled, env, steps} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
process.stdout.write(JSON.stringify(new Function('github', 'needs', 'always', 'cancelled', 'inputs', 'vars', 'fromJSON', 'env', 'steps', `return (${expression});`)(github, needs, () => true, () => cancelled, {server_image: '', existing_image: ''}, {GP_CANARY_URL: ''}, JSON.parse, env, steps)));
"""
    result = subprocess.run(["node", "-e", probe], input=json.dumps({
        "expression": expression, "github": event, "needs": context.get("needs", {}),
        "cancelled": context.get("cancelled", False),
        "env": context.get("env", {}), "steps": context.get("steps", {}),
    }), text=True, capture_output=True, check=True, timeout=10)
    return json.loads(result.stdout)


class PullRequestLoadTests(unittest.TestCase):
    def test_heavy_jobs_refuse_drafts_and_admit_ready_prs(self):
        for name, key, job in workflow_jobs(HEAVY):
            with self.subTest(workflow=name, job=key):
                condition = job.get("if", "true")
                self.assertIn(DRAFT_GUARD, condition)
                if job.get("needs"):
                    continue
                event = {"event_name": "pull_request", "event": {
                    "pull_request": {"draft": True, "user": {"login": "contributor"}},
                }}
                self.assertFalse(evaluate(condition, event))
                event["event"]["pull_request"]["draft"] = False
                self.assertTrue(evaluate(condition, event))
                self.assertTrue(evaluate(condition, {"event_name": "workflow_dispatch", "event": {}}))

    def test_heavy_workflows_wake_on_readiness_and_cancel_on_draft_conversion(self):
        expected = {"opened", "reopened", "synchronize", "ready_for_review", "converted_to_draft"}
        for name in HEAVY:
            with self.subTest(workflow=name):
                types = workflow(name)["on"]["pull_request"].get("types", [])
                self.assertTrue(expected <= set(types), types)

    def test_each_pr_workflow_scopes_admission_by_pr_number(self):
        for name, concurrency in pr_admissions():
            with self.subTest(workflow=name):
                self.assertIn("github.event.pull_request.number", concurrency.get("group", ""))
                cancellation = concurrency.get("cancel-in-progress")
                if name in ("review-gate.yml", "review-event-bridge.yml", "pr-review-doctor.lock.yml", "normalize-derived-artifacts.yml", "auto-merge-ready.yml"):
                    self.assertEqual(cancellation, "false")
                    self.assertEqual(concurrency.get("queue"), "max")
                else:
                    self.assertIn(cancellation, ("true", "${{ github.event_name == 'pull_request' }}"))

    def test_doctor_submitted_reviews_do_not_cancel_paid_work(self):
        triage = workflow("pr-review-doctor-triage.lock.yml")
        self.assertEqual(triage["on"]["pull_request_review"]["types"], ["submitted"])
        self.assertNotIn("pull_request_review_comment", triage["on"])
        self.assertEqual(triage["concurrency"]["cancel-in-progress"], "false")

    def test_failed_trunk_ci_notifies_doctor_without_full_ci_dispatch(self):
        callback = workflow("ci.yml")["jobs"]["doctor-notify"]
        self.assertEqual(callback["needs"], "ci-gate")
        self.assertEqual(callback["permissions"], {"actions": "write"})
        condition = callback["if"]
        self.assertIn("needs.ci-gate.result == 'failure'", condition)
        self.assertIn("github.ref == 'refs/heads/trunk'", condition)
        script = job_scripts(callback)
        self.assertIn("workflow_id: 'ci-doctor-triage.lock.yml'", script)
        self.assertIn("source_run_id: String(context.runId)", script)

    def test_required_checks_keep_draft_admission_and_names(self):
        gate = workflow("pr-gate.yml")
        self.assertIn("ready_for_review", gate["on"]["pull_request"].get("types", []))
        self.assertIn("converted_to_draft", gate["on"]["pull_request"].get("types", []))
        self.assertEqual(gate["jobs"]["required"]["name"], "PR Gate")
        self.assertEqual(gate["jobs"]["affected-shards"]["name"], "PR Gate / Affected shards")
        required = ("clean-code.yml", "repo-hygiene.yml")
        for name, key, job in workflow_jobs(required):
            with self.subTest(workflow=name, job=key):
                self.assertNotIn("draft", job.get("if", ""))

    def test_gate_heavy_jobs_refuse_drafts(self):
        jobs = workflow("pr-gate.yml")["jobs"]
        heavy = ["pr-gate", "fast-tests", "ci-router-validation", "n1-reader-compatibility"]
        if "server-test-producer" in jobs:
            heavy.append("server-test-producer")
        for key in heavy:
            with self.subTest(job=key):
                self.assertEqual(jobs[key]["if"], DRAFT_GUARD)
                self.assertFalse(evaluate(jobs[key]["if"], {
                    "event_name": "pull_request", "event": {"pull_request": {"draft": True}},
                }))
        for key in ("affected-shards-select", "affected-shard", "affected-shards"):
            self.assertIn("!github.event.pull_request.draft", jobs[key]["if"])
        self.assertEqual(jobs["required"]["if"], "always()")
        self.assertIn("!github.event.pull_request.draft", jobs["runner-select"].get("if", ""))

    def test_required_aggregate_rejects_draft_and_missing_heavy_results(self):
        jobs = workflow("pr-gate.yml")["jobs"]
        required = jobs["required"]
        dependencies = {"pr-gate", "fast-tests", "format", "ci-router-validation", "affected-shards"}
        if "server-test-producer" in jobs:
            dependencies.add("server-test-producer")
        self.assertEqual(set(required["needs"]), dependencies)
        verdict = next(step for step in required["steps"] if "run" in step)

        def run_verdict(results):
            environment = dict(os.environ, GITHUB_EVENT_NAME="pull_request")
            for name, expression in verdict["env"].items():
                dependency = expression.removeprefix("${{ needs.").removesuffix(".result }}")
                self.assertIn(dependency, required["needs"])
                environment[name] = results[dependency]
            return subprocess.run(
                ["bash"], input=verdict["run"], env=environment,
                text=True, capture_output=True, timeout=10,
            ).returncode

        ready = dict.fromkeys(required["needs"], "success")
        self.assertEqual(run_verdict(ready), 0)
        draft = dict.fromkeys(required["needs"], "skipped")
        draft["format"] = "success"
        self.assertNotEqual(run_verdict(draft), 0)
        for dependency in required["needs"]:
            for result in ("skipped", "failure", "cancelled"):
                with self.subTest(dependency=dependency, result=result):
                    self.assertNotEqual(run_verdict(dict(ready, **{dependency: result})), 0)

    def test_format_runs_for_drafts_despite_skipped_runner_selection(self):
        condition = workflow("pr-gate.yml")["jobs"]["format"]["if"]
        event = {"event_name": "pull_request", "event": {"pull_request": {"draft": True}}}
        needs = {"runner-select": {"result": "skipped"}}
        self.assertTrue(evaluate(condition, event, {"needs": needs}))
        self.assertFalse(evaluate(condition, event, {"needs": needs, "cancelled": True}))
        event["event"]["pull_request"]["draft"] = False
        self.assertFalse(evaluate(condition, event, {"needs": needs}))
        needs["runner-select"]["result"] = "success"
        self.assertTrue(evaluate(condition, event, {"needs": needs}))

    def test_draft_format_matrix_excludes_analyzers(self):
        include = workflow("pr-gate.yml")["jobs"]["format"]["strategy"]["matrix"]["include"]
        for draft in (True, False):
            event = {"event_name": "pull_request", "event": {"pull_request": {"draft": draft}}}
            rows = evaluate(include, event) if isinstance(include, str) else include
            with self.subTest(draft=draft):
                if draft:
                    self.assertEqual([row["check"] for row in rows], ["Format"])
                    self.assertEqual(rows[0]["format"], "true")
                    self.assertEqual(rows[0]["analyzer-build"], "false")
                else:
                    self.assertIn("Format", [row["check"] for row in rows])
                    self.assertEqual(
                        {row["analyzer-partition"] for row in rows if row["analyzer-build"] == "true"},
                        {"source", "tests"},
                    )

    def test_draft_format_steps_run_before_review_release(self):
        format_steps = workflow("pr-gate.yml")["jobs"]["format"]["steps"]
        env = {"REVIEW_FIRST_MODE": "enforce"}
        steps = {key: {"outputs": {field: "false"}} for key, field in (
            ("docs-only", "docs_only"), ("generated-only", "generated_only"),
        )}
        event = {"event_name": "pull_request", "run_attempt": 1,
                 "event": {"pull_request": {"draft": True}}}
        for draft, attempt in ((True, 1), (False, 1), (False, 2)):
            event["event"]["pull_request"]["draft"] = draft
            event["run_attempt"] = attempt
            for step in format_steps:
                name = step.get("name", "")
                is_checkout = step.get("uses", "").startswith("actions/checkout@")
                if name not in ("Await exact-head review", "Setup .NET", "Restore", "Format Check") and not is_checkout:
                    continue
                with self.subTest(step=name or "checkout", draft=draft, attempt=attempt):
                    review_wait = not draft and attempt == 1
                    self.assertEqual(
                        evaluate(step.get("if", "true"), event, {"env": env, "steps": steps}),
                        review_wait if name == "Await exact-head review" else not review_wait,
                    )

    def test_optional_pr_jobs_refuse_drafts(self):
        for path in sorted(WORKFLOWS.glob("*.y*ml")):
            definition = workflow(path.name)
            if "pull_request" not in definition.get("on", {}):
                continue
            if path.name in ("pr-gate.yml", "clean-code.yml", "repo-hygiene.yml", "auto-merge-ready.yml"):
                continue
            with self.subTest(workflow=path.name):
                types = definition["on"]["pull_request"].get("types", [])
                self.assertIn("ready_for_review", types)
                if definition.get("concurrency", {}).get("cancel-in-progress") != "false":
                    self.assertIn("converted_to_draft", types)
            for key, job in definition["jobs"].items():
                with self.subTest(workflow=path.name, job=key):
                    condition = job.get("if", "true")
                    if job.get("needs") and "always()" not in condition and "!cancelled()" not in condition:
                        continue
                    event = {"event_name": "pull_request", "ref": "refs/pull/1/merge", "event": {
                        "pull_request": {"draft": True}, "inputs": {},
                    }}
                    self.assertFalse(evaluate(condition, event), condition)

    def test_auto_merge_control_admits_draft_cleanup_only(self):
        condition = workflow("auto-merge-ready.yml")["jobs"]["enable"]["if"]
        event = {"repository": "owner/repo", "event": {
            "action": "synchronize", "pull_request": {
                "draft": True, "head": {"repo": {"full_name": "owner/repo"}},
                "base": {"ref": "trunk"}, "user": {"login": "contributor"},
            },
        }}
        self.assertFalse(evaluate(condition, event))
        event["event"]["action"] = "converted_to_draft"
        self.assertTrue(evaluate(condition, event))
        event["event"]["pull_request"]["head"]["repo"]["full_name"] = "fork/repo"
        self.assertFalse(evaluate(condition, event))

    def test_trusted_observers_keep_current_draft_refusal(self):
        observer = workflow("server-test-prebuild-observe.yml")
        parity = workflow("server-test-prebuild-parity.yml")
        claude = workflow("claude-review.yml")
        self.assertIn("if (resolved.pullRequest.draft)", job_scripts(observer["jobs"]["plan"]))
        self.assertIn("if (pr.draft)", job_scripts(parity["jobs"]["plan"]))
        self.assertIn("if (pr.draft)", job_scripts(claude["jobs"]["resolve"]))
        self.assertEqual(observer["jobs"]["producer"]["concurrency"]["cancel-in-progress"], "true")
        review = claude["jobs"]["review"]["concurrency"]
        self.assertEqual(review["cancel-in-progress"], "false")
        self.assertEqual(review["group"], "claude-review-${{ needs.resolve.outputs.pr }}-${{ needs.resolve.outputs.head }}")
        admission = claude["jobs"]["resolve"]["concurrency"]
        self.assertEqual(admission["cancel-in-progress"], "true")
        self.assertIn("github.event.issue.number", admission["group"])

    def test_new_concurrency_preserves_independent_non_pr_runs(self):
        names = ("availability-evidence-contract.yml", "capacity-evidence-contract.yml",
                 "close-external-prs.yml", "gp-customcode-runtime.yml", "gp-lifecycle-qualification.yml",
                 "gp-qualification-harness-checks.yml", "mcp-inspector-pagination.yml", "release-bundle-tooling.yml")
        for name in names:
            with self.subTest(workflow=name):
                group = workflow(name)["concurrency"]["group"]
                self.assertIn("github.event.pull_request.number || github.run_id", group)
                expression = group[group.index("${{"):]
                manual = {"run_id": 123, "event": {"pull_request": {}}}
                push = {"run_id": 124, "event": {"pull_request": {}}}
                self.assertNotEqual(evaluate(expression, manual), evaluate(expression, push))
                manual["event"]["pull_request"]["number"] = 42
                push["event"]["pull_request"]["number"] = 42
                self.assertEqual(evaluate(expression, manual), evaluate(expression, push))


if __name__ == "__main__":
    unittest.main()
