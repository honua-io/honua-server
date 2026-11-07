#!/usr/bin/env python3
"""Prove selected-shard enforcement from measured execution receipts."""

from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "affected_shard_verdict", ROOT / "scripts/ci/affected-shard-verdict.py"
)
verdict = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(verdict)

AUDIT_SPEC = importlib.util.spec_from_file_location(
    "audit_affected_shards", ROOT / "scripts/ci/audit-affected-shards.py"
)
audit = importlib.util.module_from_spec(AUDIT_SPEC)
assert AUDIT_SPEC and AUDIT_SPEC.loader
AUDIT_SPEC.loader.exec_module(audit)


def timing(shard: str, status: str = "passed", ratio: float | None = 0.1234) -> dict:
    return {
        "shard": shard,
        "status": status,
        "duration_seconds": 4,
        "headroom_ratio": ratio,
        "capacity_status": "ok",
    }


def decide(**overrides):
    fields = {
        "mode": "report",
        "select_result": "success",
        "select_skip": "false",
        "select_reason": "targeted",
        "select_count": "1",
        "shards_result": "success",
        "timings": [timing("Reporting")],
    }
    fields.update(overrides)
    selection = verdict.Selection(
        result=fields.pop("select_result"), skip=fields.pop("select_skip"),
        reason=fields.pop("select_reason"), count=fields.pop("select_count"),
        shards=fields.pop("selected_shards", ["Reporting"]),
    )
    return verdict.decide(selection=selection, **fields)


def messages(outcome) -> list[str]:
    return [message for _, message in outcome["annotations"]]


def write_passing_receipt(root: Path) -> tuple[Path, dict]:
    path = root / "control.trx"
    path.write_text('<TestRun><Results><UnitTestResult testName="control" outcome="Passed" />'
                    '</Results><ResultSummary><Counters total="1" executed="1" passed="1" '
                    'failed="0" /></ResultSummary></TestRun>')
    measured = verdict.observe_execution(path, "Reporting", "control.csproj")
    receipt = {**timing("Reporting"), "log_name": "control", "execution": measured}
    (root / "control.timing.json").write_text(json.dumps(receipt))
    return path, receipt


class AffectedShardVerdictTests(unittest.TestCase):
    def test_report_red_exits_green_and_is_not_a_test_pass(self) -> None:
        outcome = decide(
            select_count="2",
            selected_shards=["Reporting", "Ai Units"],
            shards_result="success",
            timings=[timing("Reporting", "passed"), timing("Ai Units", "failed", None)],
        )
        self.assertEqual(outcome["exit_code"], 0)
        receipt = outcome["receipt"]
        self.assertFalse(receipt["test_pass"])
        self.assertFalse(receipt["shards_passed"])
        self.assertEqual(receipt["verdict"], "red")
        self.assertEqual(receipt["mode"], "report")
        self.assertEqual(receipt["red"], 1)
        text = messages(outcome)
        self.assertTrue(any(audit.RED.match(message) for message in text))
        self.assertFalse(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in text))
        self.assertTrue(any(message.startswith("HONUA_AFFECTED_SHARD_REPORT_ONLY ") for message in text))
        self.assertIn("test_pass=false", text[-1])
        self.assertIn("not a test pass", outcome["summary"])
        self.assertIn("n/a", outcome["summary"])
        self.assertIn("12.3%", outcome["summary"])

    def test_report_green_shards_are_still_not_a_test_pass(self) -> None:
        outcome = decide(select_count="1", timings=[timing("Reporting")])
        self.assertEqual(outcome["exit_code"], 0)
        self.assertFalse(outcome["receipt"]["test_pass"])
        self.assertTrue(outcome["receipt"]["shards_passed"])
        self.assertEqual(outcome["receipt"]["verdict"], "green")
        text = messages(outcome)
        self.assertTrue(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in text))
        self.assertIn("test_pass=false verdict=green shards_passed=true", " ".join(text))
        self.assertFalse(audit.RED.match(text[-1]))

    def test_report_missing_receipt_is_not_a_pass(self) -> None:
        outcome = decide(select_count="2", selected_shards=["Reporting", "Ai Units"],
                         shards_result="failure", timings=[])
        self.assertEqual(outcome["exit_code"], 0)
        self.assertEqual(outcome["receipt"]["verdict"], "no_receipts")
        self.assertFalse(outcome["receipt"]["test_pass"])
        self.assertFalse(outcome["receipt"]["shards_passed"])
        text = messages(outcome)
        self.assertTrue(any(message.startswith("HONUA_AFFECTED_SHARD_UNAVAILABLE ") for message in text))
        self.assertFalse(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in text))

    def test_report_partial_green_receipts_are_incomplete(self) -> None:
        outcome = decide(select_count="2", selected_shards=["Reporting", "Ai Units"],
                         timings=[timing("Reporting")])
        self.assertEqual(outcome["exit_code"], 0)
        self.assertEqual(outcome["receipt"]["verdict"], "incomplete_receipts")
        self.assertFalse(outcome["receipt"]["test_pass"])
        self.assertFalse(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in messages(outcome)))

    def test_confirmed_red_is_not_reclassified_as_incomplete(self) -> None:
        outcome = decide(
            select_count="2",
            selected_shards=["Reporting", "Ai Units"],
            timings=[timing("Reporting", "failed")],
        )
        self.assertEqual(outcome["receipt"]["verdict"], "red")
        self.assertEqual(outcome["exit_code"], 0)
        self.assertFalse(outcome["receipt"]["test_pass"])

    def test_enforce_fails_closed_on_red_and_missing_receipts(self) -> None:
        red = decide(mode="enforce", timings=[timing("Reporting", "failed")])
        self.assertEqual(red["exit_code"], 1)
        self.assertFalse(red["receipt"]["test_pass"])
        self.assertEqual(red["receipt"]["mode"], "enforce")
        self.assertTrue(any(message.startswith("PR Gate / Affected shards:") for message in messages(red)))
        missing = decide(mode="enforce", select_count="1", shards_result="failure", timings=[])
        self.assertEqual(missing["exit_code"], 1)
        self.assertFalse(missing["receipt"]["test_pass"])
        self.assertTrue(any(
            message.startswith("HONUA_AFFECTED_SHARD_UNAVAILABLE ") for message in messages(missing)
        ))
        green = decide(mode="enforce", timings=[timing("Reporting")])
        self.assertEqual(green["exit_code"], 0)
        self.assertTrue(green["receipt"]["test_pass"])
        self.assertTrue(green["receipt"]["shards_passed"])
        self.assertFalse(any(
            message.startswith("HONUA_AFFECTED_SHARD_REPORT_ONLY ") for message in messages(green)
        ))

    def test_green_receipts_cannot_override_a_failed_or_cancelled_job(self) -> None:
        for result in ("failure", "cancelled", "skipped", ""):
            with self.subTest(result=result):
                outcome = decide(mode="enforce", shards_result=result)
                self.assertEqual(outcome["exit_code"], 1)
                self.assertFalse(outcome["receipt"]["test_pass"])

    def test_summary_reports_measured_test_counts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_passing_receipt(root)
            outcome = decide(mode="enforce", timings=verdict._load_timings(root))
            self.assertIn("| Passed | Failed | Skipped | Executed |", outcome["summary"])
            self.assertIn("| 1 | 0 | 0 | 1 |", outcome["summary"])

    def test_every_shard_job_preserves_test_failure(self) -> None:
        import yaml

        for filename, job_id in (("pr-gate.yml", "affected-shard"), ("ci.yml", "server-tests")):
            with self.subTest(workflow=filename):
                workflow = yaml.safe_load((ROOT / ".github/workflows" / filename).read_text())
                job = workflow["jobs"][job_id]
                self.assertFalse(job.get("continue-on-error", False))
                test_step = next(step for step in job["steps"]
                                 if "scripts/ci/run-server-test-shard.sh" in step.get("run", ""))
                self.assertFalse(test_step.get("continue-on-error", False))

    def test_full_ci_rechecks_selected_shard_receipts_before_ci_gate(self) -> None:
        import yaml

        workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        summary = workflow["jobs"]["test-all"]
        self.assertIn("targeted-shards", summary["needs"])
        check = next(step for step in summary["steps"]
                     if "affected-shard-verdict.py" in step.get("run", ""))
        self.assertEqual(check["env"]["MODE"], "enforce")
        self.assertIn("needs.targeted-shards.outputs.matrix_include", check["env"]["SELECT_MATRIX"])
        self.assertIn("needs.server-tests.result", check["env"]["SHARDS_RESULT"])
        download = next(step for step in summary["steps"]
                        if step.get("with", {}).get("pattern") == "server-test-results-*")
        self.assertEqual(download["with"]["path"], "artifacts/server-shard-results")
        self.assertIn("test-all", workflow["jobs"]["ci-gate"]["needs"])

    def test_foundation_fan_in_rejects_failed_trx_independently_of_job_conclusions(self) -> None:
        import yaml

        workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        job = workflow["jobs"]["dotnet-foundation-tests"]
        step = next(step for step in job["steps"] if step.get("name") == "Verify foundation TRX outcomes")
        self.assertEqual(step["if"], "always()")
        self.assertFalse(step.get("continue-on-error", False))
        command = step["run"].replace("scripts/ci/assert-trx-executed.py", str(ROOT / "scripts/ci/assert-trx-executed.py"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            results = root / "tests/TestResults"
            results.mkdir(parents=True)
            path, _ = write_passing_receipt(results)
            env = {**os.environ, "GITHUB_STEP_SUMMARY": str(root / "summary.md")}
            for outcome in ("Passed", "Failed"):
                path.write_text(f'<TestRun><Results><UnitTestResult testName="control" outcome="{outcome}" /></Results></TestRun>')
                run = subprocess.run(["bash", "-e", "-o", "pipefail", "-c", command],
                                     cwd=root, env=env, capture_output=True, text=True)
                self.assertEqual(run.returncode == 0, outcome == "Passed", run.stdout + run.stderr)

    def test_skipped_selection_is_not_a_test_pass_in_either_mode(self) -> None:
        for mode in ("report", "enforce"):
            for reason in ("docs_only", "validated_generated_only", "no_product_code", "no_shards_selected"):
                outcome = decide(mode=mode, select_skip="true", select_count="0",
                                 selected_shards=[], select_reason=reason, timings=[])
                self.assertEqual(outcome["exit_code"], 0, (mode, reason))
                self.assertEqual(outcome["receipt"]["verdict"], "skipped")
                self.assertFalse(outcome["receipt"]["test_pass"], mode)
                self.assertFalse(any(
                    message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in messages(outcome)
                ))

    def test_enforcement_rejects_untrustworthy_or_unknown_skips(self) -> None:
        for reason in ("untrustworthy_base", "targeted", ""):
            for mode, exit_code in (("report", 0), ("enforce", 1)):
                outcome = decide(mode=mode, select_skip="true", select_count="0",
                                 selected_shards=[], select_reason=reason, timings=[])
                self.assertEqual(outcome["exit_code"], exit_code, (mode, reason))
                self.assertFalse(outcome["receipt"]["test_pass"])

    def test_selected_families_cannot_be_hidden_by_a_safe_skip_reason(self) -> None:
        outcome = decide(mode="enforce", select_skip="true", select_reason="docs_only", timings=[])
        self.assertEqual(outcome["exit_code"], 1)
        self.assertEqual(outcome["receipt"]["verdict"], "untrusted_skip")

    def test_cli_writes_the_report_mode_receipt(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            timings = root / "timings"
            timings.mkdir()
            (timings / "reporting.timing.json").write_text(
                json.dumps(timing("Reporting", "failed")),
                encoding="utf-8",
            )
            receipt = root / "verdict.json"
            summary = root / "summary.md"
            completed = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "scripts/ci/affected-shard-verdict.py"),
                    "--timings",
                    str(timings),
                    "--receipt",
                    str(receipt),
                ],
                check=False,
                capture_output=True,
                text=True,
                env={
                    **os.environ,
                    "MODE": "report",
                    "SELECT_RESULT": "success",
                    "SELECT_SKIP": "false",
                    "SELECT_REASON": "targeted",
                    "SELECT_COUNT": "1",
                    "SELECT_MATRIX": json.dumps([{"shard_name": "Reporting"}]),
                    "SHARDS_RESULT": "success",
                    "GITHUB_STEP_SUMMARY": str(summary),
                },
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)
            body = json.loads(receipt.read_text(encoding="utf-8"))
            self.assertFalse(body["test_pass"])
            self.assertEqual(body["verdict"], "red")
            self.assertIn("HONUA_AFFECTED_SHARD_RED", completed.stdout)
            self.assertNotIn("HONUA_AFFECTED_SHARD_GREEN", completed.stdout)
            self.assertIn("not a test pass", summary.read_text(encoding="utf-8"))

    def test_workflow_enforces_receipts_in_the_required_gate(self) -> None:
        import yaml

        workflow = yaml.safe_load((ROOT / ".github/workflows/pr-gate.yml").read_text())
        self.assertEqual(workflow["env"]["AFFECTED_SHARDS_MODE"], "enforce")
        self.assertEqual(workflow["env"]["AFFECTED_SHARDS_CAP"], "6")
        required = workflow["jobs"]["required"]
        self.assertIn("affected-shards", required["needs"])
        step = required["steps"][0]
        self.assertIn("needs.affected-shards.result", step["env"]["AFFECTED_SHARDS_RESULT"])
        self.assertIn('"${AFFECTED_SHARDS_RESULT}" != success', step["run"])
        self.assertIn('"${GITHUB_EVENT_NAME}" == pull_request', step["run"])

    def test_duplicate_extra_and_wrong_family_receipts_fail_closed(self) -> None:
        for shards in (["Reporting", "Reporting"], ["Reporting", "Ai Units"], ["Other"]):
            with self.subTest(shards=shards):
                result = decide(mode="enforce", selected_shards=["Reporting"],
                                timings=[timing(shard) for shard in shards])
                self.assertEqual(result["exit_code"], 1)
                self.assertFalse(result["receipt"]["test_pass"])

    def test_selected_count_must_match_the_selected_families(self) -> None:
        result = decide(mode="enforce", select_count="2", timings=[timing("Reporting")])
        self.assertEqual(result["exit_code"], 1)
        self.assertEqual(result["receipt"]["verdict"], "incomplete_receipts")

    def test_historical_report_disposition_remains_unpromoted(self) -> None:
        record = json.loads((ROOT / "docs/ci/affected-shards-disposition-20261027.json").read_text())
        self.assertEqual(record["approved_mode"], "report")
        self.assertFalse(record["promotion_ready"])
        self.assertFalse(record["configuration"]["protection_was_edited"])
        self.assertFalse(record["configuration"]["mode_was_flipped"])
        self.assertIn("not a test pass", record["disposition"])

    def test_passed_timing_requires_unchanged_passing_trx(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, _ = write_passing_receipt(root)
            self.assertEqual(verdict._load_timings(root)[0]["status"], "passed")
            for content in ('<bad', '', path.read_text().replace('Passed', 'Failed')):
                with self.subTest(content=content):
                    path.write_text(content)
                    self.assertEqual(verdict._load_timings(root)[0]["status"], "invalid_execution")
            path.unlink()
            self.assertEqual(verdict._load_timings(root)[0]["status"], "invalid_execution")

    def test_passing_trx_cannot_substitute_for_the_receipted_execution(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, receipt = write_passing_receipt(root)
            original = path.read_text()
            path.write_text(original + "\n")
            self.assertEqual(verdict.observe_execution(path, "Reporting", "control.csproj")["status"], "passed")
            self.assertEqual(verdict._load_timings(root)[0]["status"], "invalid_execution")
            path.write_text(original)
            measured = receipt["execution"]
            mutations = {
                "counts": {**measured["counts"], "passed": 0},
                "status": "failed",
                "trxFile": "different.trx",
            }
            for field, value in mutations.items():
                with self.subTest(field=field):
                    altered = {**receipt, "execution": {**measured, field: value}}
                    (root / "control.timing.json").write_text(json.dumps(altered))
                    self.assertEqual(verdict._load_timings(root)[0]["status"], "invalid_execution")

    def test_passed_timing_without_execution_evidence_is_red(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "control.timing.json").write_text(json.dumps(timing("Reporting")))
            self.assertEqual(verdict._load_timings(root)[0]["status"], "invalid_execution")

    def test_required_gate_propagates_aggregate_failure(self) -> None:
        import yaml

        workflow = yaml.safe_load((ROOT / ".github/workflows/pr-gate.yml").read_text())
        command = workflow["jobs"]["required"]["steps"][0]["run"]
        for result in ("success", "failure", "cancelled", "skipped", ""):
            with self.subTest(result=result):
                env = {**os.environ, "GITHUB_EVENT_NAME": "pull_request",
                       "BUILD_AND_TEST_RESULT": "success", "TEST_RESULT": "success",
                       "FORMAT_RESULT": "success", "CI_ROUTER_RESULT": "success",
                       "AFFECTED_SHARDS_RESULT": result}
                run = subprocess.run(["bash", "-c", command], env=env, capture_output=True)
                self.assertEqual(run.returncode == 0, result == "success")
        env.update(GITHUB_EVENT_NAME="workflow_dispatch", AFFECTED_SHARDS_RESULT="skipped")
        self.assertEqual(subprocess.run(["bash", "-c", command], env=env).returncode, 0)


if __name__ == "__main__":
    unittest.main()
