#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

SCRIPT = Path(__file__).with_name("audit-server-test-prebuild-evidence.py")
SPEC = importlib.util.spec_from_file_location("prebuild_evidence_audit", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
POLICY_DIGEST = "d" * 64
TWO_SHARD = "exact-head-shadow:two-shard"
MULTI_SHARD = "exact-head-shadow:multi-shard"


def run(run_id: int, head: str, attempt: int = 1) -> dict:
    return {
        "id": run_id,
        "name": "Prebuild parity for PR",
        "display_title": "Prebuild parity for PR",
        "path": MODULE.WORKFLOW_PATH,
        "status": "completed",
        "conclusion": "success",
        "head_branch": "trunk",
        "head_sha": head,
        "event": "workflow_run",
        "run_attempt": attempt,
        "created_at": f"2026-08-14T00:{run_id % 60:02d}:00Z",
    }


def artifact(run_id: int, pr: int, head: str, attempt: int = 1) -> dict:
    return {
        "id": run_id + 10_000,
        "name": f"server-test-prebuild-parity-receipt-{pr}-{head}-attempt-{attempt}",
        "expired": False,
        "created_at": "2026-08-14T00:00:00Z",
    }


def receipt(run_id: int, pr: int, head: str, profile: str, *, countable: bool = True) -> dict:
    return {
        "contract": MODULE.OBSERVATION_CONTRACT,
        "pull_request": pr,
        "head_sha": head,
        "measurement_policy_digest": POLICY_DIGEST,
        "producer_run_id": run_id + 1_000,
        "verifier_run_id": run_id,
        "countable": countable,
        "summary": {
            "contract": MODULE.SUMMARY_CONTRACT,
            "profile": profile,
            "head_sha": head,
            "baseline": {
                "rounded_runner_minutes": 10,
                "p90_test_start_ms": 120_000,
                "wall_clock_ms": 180_000,
            },
            "candidate": {
                "rounded_runner_minutes_including_prebuild": 3,
                "head_to_first_test_ms": 90_000,
                "p90_test_start_ms": 90_000,
                "wall_clock_ms": 150_000,
            },
            "parity_failures": [],
            "reuse_failures": [],
            "producer_evidence_ok": True,
            "producer_ready_before_candidate": True,
        },
    }


def policy() -> dict:
    return {
        "contract": MODULE.POLICY_CONTRACT,
        "receipt_retention_days": 30,
        "minimum_countable_heads": 2,
        "minimum_cost_heads": 3,
        "required_profiles": [TWO_SHARD, MULTI_SHARD],
        "minimum_countable_heads_per_profile": 1,
        "minimum_cost_heads_per_profile": 1,
        "minimum_runner_minute_savings_percent": 60,
        "require_p90_test_start_improvement": True,
        "max_wall_clock_regression_percent": 5,
    }


days, created_filter = MODULE.retention_window(
    policy(), datetime(2026, 8, 15, 0, 0, tzinfo=timezone.utc)
)
assert days == 30
assert created_filter == ">=2026-07-16T00:00:00Z"
invalid_retention_policy = policy()
invalid_retention_policy["receipt_retention_days"] = 91
try:
    MODULE.retention_window(
        invalid_retention_policy, datetime(2026, 8, 15, 0, 0, tzinfo=timezone.utc)
    )
except ValueError as error:
    assert "policy bound" in str(error)
else:
    raise AssertionError("oversized receipt retention window must fail closed")


def write_receipt_archive(
    root: Path,
    artifact_id: int,
    value: dict,
    name: str = "parity-observation.json",
) -> None:
    root.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(root / f"{artifact_id}.zip", "w") as archive:
        archive.writestr(name, json.dumps(value))


with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    catalog = root / "catalog"
    catalog.mkdir()
    head_a = "a" * 40
    head_b = "b" * 40
    runs = {"workflow_runs": [run(1, head_a), run(2, head_b)]}
    (catalog / "1.json").write_text(json.dumps({"artifacts": []}), encoding="utf-8")
    (catalog / "2.json").write_text(
        json.dumps({"artifacts": [artifact(2, 42, head_b)]}), encoding="utf-8"
    )
    index = MODULE.discover(runs, catalog, "trunk")
    assert [item["run_id"] for item in index["artifacts"]] == [2]
    assert index["exclusions"] == [{"run_id": 1, "reason": "evidence-artifact-missing"}]

    (catalog / "3.json").write_text(
        json.dumps(
            {
                "artifacts": [
                    artifact(3, 43, head_b, attempt=1),
                    {**artifact(3, 43, head_b, attempt=2), "id": 30_002},
                ]
            }
        ),
        encoding="utf-8",
    )
    retry_index = MODULE.discover({"workflow_runs": [run(3, head_b, attempt=2)]}, catalog, "trunk")
    assert retry_index["artifacts"] == [
        {
            "artifact_id": 30_002,
            "artifact_name": f"server-test-prebuild-parity-receipt-43-{head_b}-attempt-2",
            "created_at": "2026-08-14T00:00:00Z",
            "head_sha": head_b,
            "pull_request": 43,
            "run_attempt": 2,
            "run_id": 3,
            "verifier_policy_sha": head_b,
        }
    ]

with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    receipts = root / "receipts"
    entries = []
    for offset, profile in enumerate([TWO_SHARD, MULTI_SHARD, TWO_SHARD], start=1):
        head = f"{offset:x}" * 40
        entry = {
            "artifact_id": offset + 10_000,
            "artifact_name": f"server-test-prebuild-parity-receipt-{offset}-{head}-attempt-1",
            "head_sha": head,
            "pull_request": offset,
            "run_attempt": 1,
            "run_id": offset,
            "verifier_policy_sha": "f" * 40,
        }
        entries.append(entry)
        write_receipt_archive(
            receipts,
            entry["artifact_id"],
            receipt(offset, offset, head, profile),
        )
    index = {
        "contract": MODULE.INDEX_CONTRACT,
        "workflow": {"name": MODULE.WORKFLOW_NAME, "path": MODULE.WORKFLOW_PATH},
        "artifacts": entries,
        "exclusions": [],
    }
    ledger = MODULE.summarize(index, receipts, policy(), POLICY_DIGEST)
    assert ledger["recommendation"] == "eligible-for-human-promotion-review"
    assert ledger["counts"]["distinct_countable_heads"] == 3
    assert ledger["cost"]["runner_minute_savings_percent"] == 70
    assert ledger["gates"] == {
        "parity_sample_ready": True,
        "cost_sample_ready": True,
        "profile_sample_ready": True,
        "runner_minute_target_met": True,
        "p90_test_start_improved": True,
        "p90_wall_clock_within_budget": True,
        "integrity_clean": True,
    }

    duplicate = dict(entries[0])
    duplicate["run_id"] = 99
    duplicate["artifact_id"] = 10_099
    index["artifacts"].append(duplicate)
    write_receipt_archive(
        receipts,
        duplicate["artifact_id"],
        receipt(99, 1, entries[0]["head_sha"], TWO_SHARD),
    )
    duplicate_ledger = MODULE.summarize(index, receipts, policy(), POLICY_DIGEST)
    assert duplicate_ledger["recommendation"] == "insufficient-evidence"
    assert duplicate_ledger["duplicate_heads"] == [entries[0]["head_sha"]]

    bad = receipt(2, 2, entries[1]["head_sha"], MULTI_SHARD)
    bad["summary"]["parity_failures"] = ["server-a"]
    write_receipt_archive(receipts, entries[1]["artifact_id"], bad)
    invalid = MODULE.summarize(
        {
            "contract": MODULE.INDEX_CONTRACT,
            "workflow": {"name": MODULE.WORKFLOW_NAME, "path": MODULE.WORKFLOW_PATH},
            "artifacts": entries[:3],
            "exclusions": [],
        },
        receipts,
        policy(),
        POLICY_DIGEST,
    )
    assert invalid["recommendation"] == "insufficient-evidence"
    assert invalid["integrity_failures"][0]["run_id"] == 2

    slow = receipt(2, 2, entries[1]["head_sha"], MULTI_SHARD)
    slow["summary"]["candidate"]["p90_test_start_ms"] = 130_000
    write_receipt_archive(receipts, entries[1]["artifact_id"], slow)
    slow_ledger = MODULE.summarize(
        {
            "contract": MODULE.INDEX_CONTRACT,
            "workflow": {"name": MODULE.WORKFLOW_NAME, "path": MODULE.WORKFLOW_PATH},
            "artifacts": entries[:3],
            "exclusions": [],
        },
        receipts,
        policy(),
        POLICY_DIGEST,
    )
    assert slow_ledger["recommendation"] == "insufficient-evidence"
    assert not slow_ledger["gates"]["p90_test_start_improved"]

    unsafe_entry = dict(entries[2])
    unsafe_entry["artifact_id"] = 20_003
    write_receipt_archive(
        receipts,
        unsafe_entry["artifact_id"],
        receipt(3, 3, entries[2]["head_sha"], TWO_SHARD),
        "../parity-observation.json",
    )
    unsafe = MODULE.summarize(
        {
            "contract": MODULE.INDEX_CONTRACT,
            "workflow": {"name": MODULE.WORKFLOW_NAME, "path": MODULE.WORKFLOW_PATH},
            "artifacts": [unsafe_entry],
            "exclusions": [],
        },
        receipts,
        policy(),
        POLICY_DIGEST,
    )
    assert unsafe["integrity_failures"][0]["reason"] == "receipt archive contains an unsafe member"

    old_policy = receipt(3, 3, entries[2]["head_sha"], TWO_SHARD)
    old_policy["measurement_policy_digest"] = "e" * 64
    write_receipt_archive(receipts, entries[2]["artifact_id"], old_policy)
    separated = MODULE.summarize(
        {
            "contract": MODULE.INDEX_CONTRACT,
            "workflow": {"name": MODULE.WORKFLOW_NAME, "path": MODULE.WORKFLOW_PATH},
            "artifacts": entries[:3],
            "exclusions": [],
        },
        receipts,
        policy(),
        POLICY_DIGEST,
    )
    assert separated["counts"]["noncurrent_policy_receipts"] == 1
    assert separated["recommendation"] == "insufficient-evidence"

print("server-test-prebuild-evidence-audit=ok")
