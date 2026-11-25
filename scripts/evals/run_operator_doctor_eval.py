#!/usr/bin/env python3
"""Zero-DevOps Operator Doctor evaluation harness for Honua Server."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

EVALS_DIR = Path(__file__).resolve().parent
if str(EVALS_DIR) not in sys.path:
    sys.path.insert(0, str(EVALS_DIR))

from operator_doctor_rules import (
    DoctorDiagnosis,
    SignalInput,
    diagnose_signal,
    find_secret_leaks,
)

SCORECARD_SCHEMA = "honua.eval.operator-doctor.v1"
DEFAULT_SCENARIOS_PATH = Path("tests/evals/operator-doctor/scenarios.json")


@dataclass(frozen=True)
class EvalScenario:
    """Structured broken-deployment evaluation scenario."""

    scenario_id: str
    category: str
    signal: SignalInput
    expected_root_cause_keywords: list[str]
    expected_remediation_hints: list[str]
    forbidden_secret_patterns: list[str]


@dataclass(frozen=True)
class ScenarioScore:
    """Evaluation scores for a single scenario."""

    scenario_id: str
    category: str
    matched_rule: str
    diagnostic_precision: float
    actionability: float
    secret_redaction_passed: bool
    leaked_patterns: list[str]
    diagnosis: str
    remediation: list[str]
    passed: bool


@dataclass(frozen=True)
class EvalThresholds:
    """Release-gating thresholds for the Operator Doctor evaluation."""

    min_diagnostic_precision: float = 0.90
    required_secret_redaction_rate: float = 1.0


DEFAULT_THRESHOLDS = EvalThresholds()


def keyword_coverage(text: str, keywords: list[str]) -> float:
    """Compute case-insensitive coverage fraction of expected terms."""
    if not keywords:
        return 1.0
    lowered = text.lower()
    matched = sum(1 for keyword in keywords if keyword.lower() in lowered)
    return round(matched / len(keywords), 4)


def evaluate_scenario(
    scenario: EvalScenario,
    diagnosis: DoctorDiagnosis | None = None,
    thresholds: EvalThresholds = DEFAULT_THRESHOLDS,
) -> ScenarioScore:
    """Score diagnostic precision, actionability, and secret-redaction safety."""
    result = diagnosis if diagnosis is not None else diagnose_signal(scenario.signal)
    precision = keyword_coverage(result.root_cause, scenario.expected_root_cause_keywords)
    actionability = keyword_coverage("\n".join(result.remediation), scenario.expected_remediation_hints)
    leaked = find_secret_leaks(result, scenario.forbidden_secret_patterns)
    redaction_ok = len(leaked) == 0
    passed = redaction_ok and precision >= thresholds.min_diagnostic_precision
    return ScenarioScore(
        scenario_id=scenario.scenario_id,
        category=scenario.category,
        matched_rule=result.matched_rule,
        diagnostic_precision=precision,
        actionability=actionability,
        secret_redaction_passed=redaction_ok,
        leaked_patterns=leaked,
        diagnosis=result.root_cause,
        remediation=result.remediation,
        passed=passed,
    )


def _score_to_dict(score: ScenarioScore) -> dict[str, object]:
    """Serialize a ScenarioScore dataclass into scorecard JSON shape."""
    return {
        "id": score.scenario_id,
        "category": score.category,
        "matched_rule": score.matched_rule,
        "diagnostic_precision": score.diagnostic_precision,
        "actionability": score.actionability,
        "secret_redaction_passed": score.secret_redaction_passed,
        "leaked_patterns": score.leaked_patterns,
        "diagnosis": score.diagnosis,
        "remediation": score.remediation,
        "passed": score.passed,
    }


def build_scorecard(
    mode: str,
    scores: list[ScenarioScore],
    thresholds: EvalThresholds = DEFAULT_THRESHOLDS,
) -> dict[str, object]:
    """Assemble a honua.eval.operator-doctor.v1 JSON scorecard and enforce gates."""
    total = len(scores)
    mean_precision = round(sum(s.diagnostic_precision for s in scores) / total, 4) if total else 0.0
    mean_actionability = round(sum(s.actionability for s in scores) / total, 4) if total else 0.0
    safe_count = sum(1 for s in scores if s.secret_redaction_passed)
    redaction_rate = round(safe_count / total, 4) if total else 0.0
    leak_count = sum(len(s.leaked_patterns) for s in scores)
    overall_passed = (
        total > 0
        and leak_count == 0
        and redaction_rate >= thresholds.required_secret_redaction_rate
        and mean_precision >= thresholds.min_diagnostic_precision
    )
    return {
        "schema": SCORECARD_SCHEMA,
        "mode": mode,
        "generated_at": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "thresholds": {
            "min_diagnostic_precision": thresholds.min_diagnostic_precision,
            "required_secret_redaction_rate": thresholds.required_secret_redaction_rate,
        },
        "summary": {
            "total_scenarios": total,
            "passed_scenarios": sum(1 for s in scores if s.passed),
            "diagnostic_precision": mean_precision,
            "actionability": mean_actionability,
            "secret_redaction_rate": redaction_rate,
            "secret_leak_count": leak_count,
            "passed": overall_passed,
        },
        "scenarios": [_score_to_dict(score) for score in scores],
    }


def load_scenarios(path: Path) -> list[EvalScenario]:
    """Parse scenarios.json into typed EvalScenario instances."""
    raw = json.loads(path.read_text(encoding="utf-8"))
    scenarios: list[EvalScenario] = []
    for item in raw["scenarios"]:
        sig = item["simulated_signal"]
        signal = SignalInput(
            healthz_status=int(sig["healthz_status"]),
            healthz_body=str(sig["healthz_body"]),
            headers={str(k): str(v) for k, v in sig.get("headers", {}).items()},
            log_lines=[str(line) for line in sig.get("log_lines", [])],
        )
        scenarios.append(
            EvalScenario(
                scenario_id=str(item["id"]),
                category=str(item["category"]),
                signal=signal,
                expected_root_cause_keywords=[str(k) for k in item["expected_root_cause_keywords"]],
                expected_remediation_hints=[str(h) for h in item["expected_remediation_hints"]],
                forbidden_secret_patterns=[str(p) for p in item["forbidden_secret_patterns"]],
            )
        )
    return scenarios


def probe_live_server(base_url: str, timeout_seconds: float = 5.0) -> SignalInput:
    """Probe a live Honua Server readiness endpoint and collect safe diagnostics."""
    ready_url = f"{base_url.rstrip('/')}/healthz/ready"
    request = urllib.request.Request(ready_url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            headers = dict(response.headers.items())
            body = response.read().decode("utf-8", errors="replace")
            return SignalInput(response.status, body, headers, [])
    except urllib.error.HTTPError as err:
        headers = dict(err.headers.items())
        body = err.read().decode("utf-8", errors="replace")
        reason = headers.get("X-Honua-Readiness-Reason", "unknown")
        return SignalInput(err.code, body, headers, [f"X-Honua-Readiness-Reason: {reason}"])
    except urllib.error.URLError as err:
        return SignalInput(0, "", {}, [f"Probe connection error: {err.reason}"])


def run_live_eval(base_url: str, thresholds: EvalThresholds = DEFAULT_THRESHOLDS) -> dict[str, object]:
    """Run Operator Doctor evaluation against a live Honua Server instance."""
    signal = probe_live_server(base_url)
    diagnosis = diagnose_signal(signal)
    leaked = find_secret_leaks(diagnosis, [])
    redaction_ok = len(leaked) == 0
    is_healthy = signal.healthz_status == 200 and signal.healthz_body.strip() == "Ready"
    precision = 1.0 if (is_healthy or diagnosis.matched_rule != "unknown") else 0.0
    score = ScenarioScore(
        scenario_id="live_healthz_probe",
        category="live" if is_healthy else diagnosis.category,
        matched_rule="healthy" if is_healthy else diagnosis.matched_rule,
        diagnostic_precision=precision,
        actionability=1.0 if diagnosis.remediation else 0.0,
        secret_redaction_passed=redaction_ok,
        leaked_patterns=leaked,
        diagnosis="Server readiness probe returned 200 Ready." if is_healthy else diagnosis.root_cause,
        remediation=[] if is_healthy else diagnosis.remediation,
        passed=redaction_ok and precision >= thresholds.min_diagnostic_precision,
    )
    return build_scorecard("live", [score], thresholds)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    """Parse CLI arguments for the Operator Doctor evaluation runner."""
    parser = argparse.ArgumentParser(description="Honua Zero-DevOps Operator Doctor eval suite.")
    parser.add_argument("--scenarios", type=Path, default=DEFAULT_SCENARIOS_PATH)
    parser.add_argument("--mode", choices=("deterministic", "live"), default="deterministic")
    parser.add_argument("--base-url", default="")
    parser.add_argument("--scorecard", type=Path, default=None)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    """Execute the Operator Doctor eval CLI and return process exit code."""
    args = parse_args(argv)
    if args.mode == "live":
        if not args.base_url:
            print("error: --base-url is required in live mode", file=sys.stderr)
            return 2
        scorecard = run_live_eval(args.base_url)
    else:
        scenarios = load_scenarios(args.scenarios)
        scores = [evaluate_scenario(scenario) for scenario in scenarios]
        scorecard = build_scorecard("deterministic", scores)

    if args.scorecard is not None:
        args.scorecard.parent.mkdir(parents=True, exist_ok=True)
        args.scorecard.write_text(json.dumps(scorecard, indent=2) + "\n", encoding="utf-8")

    summary = scorecard["summary"]
    print(json.dumps(summary))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
