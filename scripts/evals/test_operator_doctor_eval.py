#!/usr/bin/env python3
"""Unit tests for the Zero-DevOps Operator Doctor evaluation suite."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

EVALS_DIR = Path(__file__).resolve().parent
REPO_ROOT = EVALS_DIR.parent.parent
SCENARIOS_PATH = REPO_ROOT / "tests" / "evals" / "operator-doctor" / "scenarios.json"

if str(EVALS_DIR) not in sys.path:
    sys.path.insert(0, str(EVALS_DIR))

import run_operator_doctor_eval as doctor_eval

EXPECTED_SCENARIO_IDS = [
    "wrong_db_password",
    "missing_postgis_extension",
    "forwarded_headers_misconfigured",
    "expired_license_grace_warning",
    "temp_storage_disk_full",
    "invalid_mtls_trust_anchor",
    "port_already_in_use",
    "locked_startup_migration",
    "cors_origin_blocked",
    "object_store_unreachable",
]


class OperatorDoctorEvalTests(unittest.TestCase):
    """Covers all 10 scenarios, secret-leak gating, precision gating, and scorecard output."""

    def test_all_ten_scenarios_present_and_pass_deterministic_eval(self) -> None:
        """All 10 broken-deployment scenarios must pass precision, actionability, and redaction."""
        scenarios = doctor_eval.load_scenarios(SCENARIOS_PATH)
        self.assertEqual([s.scenario_id for s in scenarios], EXPECTED_SCENARIO_IDS)

        scores = [doctor_eval.evaluate_scenario(scenario) for scenario in scenarios]
        scorecard = doctor_eval.build_scorecard("deterministic", scores)
        summary = scorecard["summary"]

        self.assertTrue(summary["passed"])
        self.assertEqual(summary["total_scenarios"], 10)
        self.assertEqual(summary["passed_scenarios"], 10)
        self.assertGreaterEqual(summary["diagnostic_precision"], 0.90)
        self.assertGreaterEqual(summary["actionability"], 0.90)
        self.assertEqual(summary["secret_redaction_rate"], 1.0)
        self.assertEqual(summary["secret_leak_count"], 0)

    def test_each_scenario_matches_its_own_diagnostic_rule(self) -> None:
        """Every scenario must classify to its exact intended diagnostic rule."""
        scenarios = doctor_eval.load_scenarios(SCENARIOS_PATH)
        for scenario in scenarios:
            with self.subTest(scenario=scenario.scenario_id):
                score = doctor_eval.evaluate_scenario(scenario)
                self.assertEqual(score.matched_rule, scenario.scenario_id)
                self.assertEqual(score.diagnostic_precision, 1.0)
                self.assertEqual(score.actionability, 1.0)
                self.assertTrue(score.secret_redaction_passed)
                self.assertEqual(score.leaked_patterns, [])

    def test_secret_leak_in_diagnosis_fails_scenario_and_entire_eval(self) -> None:
        """Any leaked password, key, or stack trace must hard-fail the entire scorecard."""
        scenarios = doctor_eval.load_scenarios(SCENARIOS_PATH)
        leaky_diagnosis = doctor_eval.DoctorDiagnosis(
            matched_rule="wrong_db_password",
            category="database",
            root_cause="PostgreSQL password authentication 28P01 failed: Password=SuperSecretPgPass99!",
            remediation=["Update ConnectionStrings__DefaultConnection, verify credentials, rotate."],
        )

        scores = [doctor_eval.evaluate_scenario(scenarios[0], diagnosis=leaky_diagnosis)]
        scores.extend(doctor_eval.evaluate_scenario(s) for s in scenarios[1:])
        scorecard = doctor_eval.build_scorecard("deterministic", scores)

        self.assertFalse(scores[0].secret_redaction_passed)
        self.assertFalse(scores[0].passed)
        self.assertGreater(len(scores[0].leaked_patterns), 0)
        self.assertFalse(scorecard["summary"]["passed"])
        self.assertLess(scorecard["summary"]["secret_redaction_rate"], 1.0)
        self.assertGreater(scorecard["summary"]["secret_leak_count"], 0)

    def test_private_key_and_stack_trace_leaks_are_detected(self) -> None:
        """Global secret redaction detector must catch PEM keys and .NET stack frames."""
        diagnosis = doctor_eval.DoctorDiagnosis(
            matched_rule="invalid_mtls_trust_anchor",
            category="security",
            root_cause=(
                "mTLS certificate trust UntrustedRoot failure "
                "-----BEGIN PRIVATE KEY-----abc-----END PRIVATE KEY-----"
            ),
            remediation=["at System.Net.Security.SslStream.Auth() in /src/Tls.cs:line 99"],
        )

        leaks = doctor_eval.find_secret_leaks(diagnosis, [])
        self.assertGreaterEqual(len(leaks), 3)

    def test_precision_below_release_threshold_fails_gate(self) -> None:
        """Mean diagnostic precision below 0.90 must fail the release scorecard."""
        scenarios = doctor_eval.load_scenarios(SCENARIOS_PATH)
        weak_diagnosis = doctor_eval.DoctorDiagnosis(
            matched_rule="unknown",
            category="unknown",
            root_cause="Generic unspecified startup issue.",
            remediation=["Check logs."],
        )

        scores = [doctor_eval.evaluate_scenario(s, diagnosis=weak_diagnosis) for s in scenarios[:2]]
        scores.extend(doctor_eval.evaluate_scenario(s) for s in scenarios[2:])
        scorecard = doctor_eval.build_scorecard("deterministic", scores)

        self.assertLess(scorecard["summary"]["diagnostic_precision"], 0.90)
        self.assertEqual(scorecard["summary"]["secret_redaction_rate"], 1.0)
        self.assertFalse(scorecard["summary"]["passed"])

    def test_cli_writes_valid_v1_scorecard_json(self) -> None:
        """CLI deterministic execution writes a valid honua.eval.operator-doctor.v1 file."""
        with tempfile.TemporaryDirectory() as tmp_dir:
            output_path = Path(tmp_dir) / "scorecard.json"
            exit_code = doctor_eval.main(
                [
                    "--scenarios",
                    str(SCENARIOS_PATH),
                    "--mode",
                    "deterministic",
                    "--scorecard",
                    str(output_path),
                ]
            )
            self.assertEqual(exit_code, 0)
            payload = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual(payload["schema"], "honua.eval.operator-doctor.v1")
        self.assertEqual(payload["mode"], "deterministic")
        self.assertEqual(payload["thresholds"]["min_diagnostic_precision"], 0.90)
        self.assertEqual(payload["thresholds"]["required_secret_redaction_rate"], 1.0)
        self.assertEqual(len(payload["scenarios"]), 10)
        self.assertTrue(payload["summary"]["passed"])

    def test_live_mode_diagnoses_degraded_probe_without_network(self) -> None:
        """Live mode classifies mocked HTTP 503 readiness responses cleanly."""
        degraded = doctor_eval.SignalInput(
            healthz_status=503,
            healthz_body="Not Ready",
            headers={"X-Honua-Readiness-Reason": "migrations-in-progress"},
            log_lines=["X-Honua-Readiness-Reason: migrations-in-progress"],
        )

        with mock.patch.object(doctor_eval, "probe_live_server", return_value=degraded):
            scorecard = doctor_eval.run_live_eval("http://127.0.0.1:8080")

        self.assertEqual(scorecard["schema"], "honua.eval.operator-doctor.v1")
        self.assertEqual(scorecard["mode"], "live")
        self.assertTrue(scorecard["summary"]["passed"])
        self.assertEqual(scorecard["scenarios"][0]["matched_rule"], "locked_startup_migration")


if __name__ == "__main__":
    unittest.main()
