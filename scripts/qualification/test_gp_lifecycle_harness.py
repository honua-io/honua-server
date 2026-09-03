"""Executable tests for the GP qualification receipt boundary."""

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"
STREAK = ROOT / "scripts/qualification/gp-canary-streak.sh"


class GpQualificationHarnessTests(unittest.TestCase):
    def run_harness(self, lane, **overrides):
        with tempfile.TemporaryDirectory(prefix="gp-qualification-") as directory:
            environment = os.environ.copy()
            environment.update(
                {
                    "HONUA_GP_LANE": lane,
                    "HONUA_GP_RECEIPT_DIR": directory,
                    "GITHUB_SERVER_URL": "https://github.com",
                    "GITHUB_REPOSITORY": "honua-io/honua-server",
                    "GITHUB_RUN_ID": "3848",
                    "GITHUB_RUN_ATTEMPT": "1",
                    **overrides,
                }
            )
            completed = subprocess.run(
                [str(HARNESS)],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            receipts = {
                path.stem: json.loads(path.read_text(encoding="utf-8"))
                for path in Path(directory).glob("*.json")
                if path.name != "summary.json" and not path.name.startswith(".")
            }
            summary = json.loads((Path(directory) / "summary.json").read_text(encoding="utf-8"))
            return completed, receipts, summary

    def test_executed_assertion_failure_isolated_from_follow_up(self):
        completed, receipts, summary = self.run_harness("self-test")

        self.assertEqual(1, completed.returncode, completed.stderr)
        self.assertEqual(["assertion-failure", "follow-up", "cleanup"], summary["declared_scenarios"])
        self.assertEqual(3, summary["declared_scenario_count"])
        self.assertEqual(3, summary["receipt_count"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual([], summary["duplicate_receipts"])
        self.assertEqual("fail", receipts["assertion-failure"]["outcome"])
        self.assertEqual("intentional assertion failure", receipts["assertion-failure"]["finding"])
        self.assertEqual("pass", receipts["follow-up"]["outcome"])
        self.assertIsNone(receipts["follow-up"]["finding"])
        for receipt in receipts.values():
            self.assertLessEqual(receipt["started_at"], receipt["completed_at"])
            self.assertIn("attempt_count", receipt)
            self.assertIn("state_transitions", receipt)
            self.assertIn("disruptions", receipt)
            self.assertIn("bytes", receipt["output"])
            self.assertIn("sha256", receipt["output"])
            self.assertEqual(
                "https://github.com/honua-io/honua-server/actions/runs/3848",
                receipt["github"]["run_url"],
            )

    def test_resilience_preflight_emits_topology_and_unrun_receipts(self):
        completed, receipts, summary = self.run_harness(
            "resilience",
            HONUA_SERVER_IMAGE="ghcr.io/honua/server@sha256:" + "a" * 64,
            HONUA_WORKER_IMAGE="ghcr.io/honua/worker@sha256:" + "b" * 64,
            HONUA_GP_SOURCE_SHA="c" * 40,
            HONUA_GP_SKIP_PULL="true",
        )

        self.assertNotEqual(0, completed.returncode)
        self.assertEqual("fail", receipts["topology"]["outcome"])
        self.assertIn("required for resilience qualification", receipts["topology"]["finding"])
        self.assertEqual(summary["declared_scenario_count"], summary["receipt_count"])
        self.assertEqual([], summary["missing_scenarios"])
        self.assertEqual([], summary["duplicate_receipts"])
        self.assertTrue(all(item["outcome"] == "fail" for item in receipts.values() if item["scenario"] != "cleanup"))

    def test_missing_scheduled_canary_receipt_is_a_failed_current_streak_entry(self):
        with tempfile.TemporaryDirectory(prefix="gp-streak-") as directory:
            fake_bin = Path(directory) / "bin"
            fake_bin.mkdir()
            fake_gh = fake_bin / "gh"
            fake_gh.write_text("#!/bin/sh\nprintf '%s\\n' '[]'\n", encoding="utf-8")
            fake_gh.chmod(0o755)
            streak = Path(directory) / "streak.json"
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "GITHUB_EVENT_NAME": "schedule",
                    "GITHUB_SERVER_URL": "https://github.com",
                    "GITHUB_REPOSITORY": "honua-io/honua-server",
                    "GITHUB_RUN_ID": "9876",
                    "GITHUB_SHA": "d" * 40,
                    "HONUA_GP_CANARY_STREAK_RECEIPT": str(streak),
                    "HONUA_GP_CANARY_RECEIPT": str(Path(directory) / "missing-receipt.json"),
                }
            )
            completed = subprocess.run(
                [str(STREAK)], cwd=ROOT, env=environment, capture_output=True, text=True, check=False
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            result = json.loads(streak.read_text(encoding="utf-8"))
            self.assertEqual(1, result["observed_runs"])
            self.assertEqual(0, result["consecutive_green"])
            self.assertFalse(result["ready"])
            self.assertEqual("failure", result["runs"][0]["conclusion"])
            self.assertTrue(result["runs"][0]["missing_receipt"])
            self.assertEqual(
                "https://github.com/honua-io/honua-server/actions/runs/9876",
                result["runs"][0]["url"],
            )


if __name__ == "__main__":
    unittest.main()
