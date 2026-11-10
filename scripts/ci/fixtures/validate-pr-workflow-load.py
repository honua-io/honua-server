#!/usr/bin/env python3
"""Exercise draft admission and cancellation in the actual PR workflows."""

import json
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
        if not any(str(event).startswith("pull_request") for event in triggers):
            continue
        for job in definition["jobs"].values():
            if not job.get("needs"):
                yield path.name, definition.get("concurrency", job.get("concurrency", {}))


def evaluate(condition, event):
    expression = condition.removeprefix("${{").removesuffix("}}").strip()
    probe = """const {expression, github} = JSON.parse(require('fs').readFileSync(0, 'utf8'));
process.stdout.write(JSON.stringify(new Function('github', `return (${expression});`)(github)));
"""
    result = subprocess.run(["node", "-e", probe], input=json.dumps({
        "expression": expression, "github": event,
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

    def test_each_pr_workflow_cancels_admission_by_pr_number(self):
        for name, concurrency in pr_admissions():
            with self.subTest(workflow=name):
                self.assertIn("github.event.pull_request.number", concurrency.get("group", ""))
                cancellation = concurrency.get("cancel-in-progress")
                self.assertIn(cancellation, ("true", "${{ github.event_name == 'pull_request' }}"))

    def test_required_checks_keep_draft_admission_and_names(self):
        gate = workflow("pr-gate.yml")
        self.assertIn("ready_for_review", gate["on"]["pull_request"].get("types", []))
        self.assertEqual(gate["jobs"]["required"]["name"], "PR Gate")
        self.assertEqual(gate["jobs"]["affected-shards"]["name"], "PR Gate / Affected shards")
        required = ("pr-gate.yml", "clean-code.yml", "repo-hygiene.yml")
        for name, key, job in workflow_jobs(required):
            with self.subTest(workflow=name, job=key):
                self.assertNotIn("draft", job.get("if", ""))

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
