#!/usr/bin/env python3
"""Fail-closed validation for the alerting enabled-candidate receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = "honua.alerting-enabled-candidate-receipt/v1"
SHA256 = re.compile(r"^sha256:[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
REQUIRED_SCENARIOS = {
    "condition-to-signed-webhook-audit",
    "database-outage-recovery",
    "leader-election",
    "restart-persistence",
    "alert-storm",
    "tenant-isolation",
    "secret-rotation-no-leakage",
    "delivery-latency",
    "backlog-self-monitoring",
}
EXTERNAL_KINDS = {"hosted-api", "external-webhook", "process-restart", "multi-replica", "database-outage"}
MAX_AGE_SECONDS = 6 * 60 * 60


def digest(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def fail(message: str) -> None:
    raise ValueError(message)


def validate(receipt: dict, *, candidate_sha: str, artifact: Path, trx: Path,
             workflow: Path, evidence_root: Path, now: datetime) -> None:
    if set(receipt) != {"schema", "candidate", "workflow", "testRun", "scenarios", "createdAt"}:
        fail("receipt has missing or unknown top-level fields")
    if receipt["schema"] != SCHEMA:
        fail("receipt schema is not the enabled-candidate v1 schema")
    if not COMMIT.fullmatch(candidate_sha):
        fail("candidate SHA must be an exact lowercase 40-character commit")

    candidate = receipt["candidate"]
    if candidate != {"sourceSha": candidate_sha, "artifactDigest": digest(artifact)}:
        fail("receipt is not bound to the exact candidate source and packaged artifact")
    workflow_binding = receipt["workflow"]
    if workflow_binding != {"path": str(workflow), "revisionDigest": digest(workflow)}:
        fail("receipt is not bound to the current workflow revision")
    test_run = receipt["testRun"]
    if test_run.get("resultDigest") != digest(trx):
        fail("receipt test result digest does not match the retained TRX")
    if not isinstance(test_run.get("selectedTests"), int) or test_run["selectedTests"] <= 0:
        fail("qualification selected zero tests")

    try:
        created = datetime.fromisoformat(receipt["createdAt"].replace("Z", "+00:00"))
    except (AttributeError, ValueError) as error:
        raise ValueError("createdAt must be an ISO-8601 UTC timestamp") from error
    age = (now - created.astimezone(timezone.utc)).total_seconds()
    if age < -300 or age > MAX_AGE_SECONDS:
        fail("qualification receipt is stale or from the future")

    scenarios = receipt["scenarios"]
    if not isinstance(scenarios, list):
        fail("scenarios must be an array")
    names = [item.get("name") for item in scenarios if isinstance(item, dict)]
    if len(names) != len(scenarios) or len(names) != len(set(names)):
        fail("scenario names must be present and unique")
    if set(names) != REQUIRED_SCENARIOS:
        missing = sorted(REQUIRED_SCENARIOS - set(names))
        extra = sorted(set(names) - REQUIRED_SCENARIOS)
        fail(f"required section 7.3 scenario set mismatch; missing={missing}, extra={extra}")

    for scenario in scenarios:
        name = scenario["name"]
        if set(scenario) != {"name", "status", "evidenceKind", "evidencePath", "evidenceDigest"}:
            fail(f"scenario {name} has missing or unknown fields")
        if scenario["status"] != "green":
            fail(f"scenario {name} is not green")
        if scenario["evidenceKind"] not in EXTERNAL_KINDS:
            fail(f"scenario {name} uses unit-only or unknown evidence")
        evidence_path = evidence_root / scenario["evidencePath"]
        if not evidence_path.is_file():
            fail(f"scenario {name} evidence path is absent")
        if not SHA256.fullmatch(scenario["evidenceDigest"] or ""):
            fail(f"scenario {name} evidence digest is malformed")
        if scenario["evidenceDigest"] != digest(evidence_path):
            fail(f"scenario {name} evidence digest does not match")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--trx", type=Path, required=True)
    parser.add_argument("--workflow", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--now", help="test-only ISO-8601 clock override")
    args = parser.parse_args()
    now = datetime.fromisoformat(args.now.replace("Z", "+00:00")) if args.now else datetime.now(timezone.utc)
    try:
        validate(json.loads(args.receipt.read_text()), candidate_sha=args.candidate_sha,
                 artifact=args.artifact, trx=args.trx, workflow=args.workflow,
                 evidence_root=args.evidence_root, now=now)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(f"alerting candidate rejected: {error}", file=sys.stderr)
        return 1
    print(f"alerting candidate accepted: {args.candidate_sha}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
