#!/usr/bin/env python3
"""Offline proof that report-mode affected-shard success is not a test pass."""

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
    return verdict.decide(**fields)


def messages(outcome) -> list[str]:
    return [message for _, message in outcome["annotations"]]


class AffectedShardVerdictTests(unittest.TestCase):
    def test_report_red_exits_green_and_is_not_a_test_pass(self) -> None:
        outcome = decide(
            select_count="2",
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
        outcome = decide(select_count="2", shards_result="failure", timings=[])
        self.assertEqual(outcome["exit_code"], 0)
        self.assertEqual(outcome["receipt"]["verdict"], "no_receipts")
        self.assertFalse(outcome["receipt"]["test_pass"])
        self.assertFalse(outcome["receipt"]["shards_passed"])
        text = messages(outcome)
        self.assertTrue(any(message.startswith("HONUA_AFFECTED_SHARD_UNAVAILABLE ") for message in text))
        self.assertFalse(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in text))

    def test_report_partial_green_receipts_are_incomplete(self) -> None:
        outcome = decide(select_count="2", timings=[timing("Reporting")])
        self.assertEqual(outcome["exit_code"], 0)
        self.assertEqual(outcome["receipt"]["verdict"], "incomplete_receipts")
        self.assertFalse(outcome["receipt"]["test_pass"])
        self.assertFalse(any(message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in messages(outcome)))

    def test_confirmed_red_is_not_reclassified_as_incomplete(self) -> None:
        outcome = decide(
            select_count="2",
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

    def test_skipped_selection_is_not_a_test_pass_in_either_mode(self) -> None:
        for mode in ("report", "enforce"):
            outcome = decide(mode=mode, select_skip="true", select_count="0", timings=[])
            self.assertEqual(outcome["exit_code"], 0, mode)
            self.assertEqual(outcome["receipt"]["verdict"], "skipped")
            self.assertFalse(outcome["receipt"]["test_pass"], mode)
            self.assertFalse(any(
                message.startswith("HONUA_AFFECTED_SHARD_GREEN ") for message in messages(outcome)
            ))

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

    def test_workflow_stays_in_report_and_keeps_the_promotion_bar(self) -> None:
        workflow = (ROOT / ".github/workflows/pr-gate.yml").read_text(encoding="utf-8")
        self.assertIn("AFFECTED_SHARDS_MODE: report\n", workflow)
        self.assertNotIn("AFFECTED_SHARDS_MODE: enforce", workflow)
        self.assertIn("name: PR Gate / Affected shards\n", workflow)
        self.assertIn("scripts/ci/affected-shard-verdict.py", workflow)
        self.assertIn("60 executed samples minimum", workflow)
        self.assertIn("Below 2% is the bar", workflow)
        record = json.loads(
            (ROOT / "docs/ci/affected-shards-disposition-20261027.json").read_text(encoding="utf-8")
        )
        self.assertEqual(record["approved_mode"], "report")
        self.assertFalse(record["promotion_ready"])
        self.assertEqual(record["branch_protection"]["required_contexts"][1], "PR Gate / Affected shards")
        self.assertFalse(record["configuration"]["protection_was_edited"])
        self.assertFalse(record["configuration"]["mode_was_flipped"])
        self.assertIn("not a test pass", record["disposition"])
        promotion = (ROOT / "docs/ci/affected-shards-promotion.md").read_text(encoding="utf-8")
        self.assertIn("below 2%", promotion)
        self.assertIn("at least 60 executed samples", promotion)
        self.assertIn("#6663", promotion)
        self.assertIn("40423291836", promotion)


if __name__ == "__main__":
    unittest.main()
