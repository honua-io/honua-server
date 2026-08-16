#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import hashlib
import json
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path


SCRIPT = Path(__file__).with_name("audit-impact-routing-evidence.py")
SPEC = importlib.util.spec_from_file_location("impact_routing_evidence", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
REPOSITORY_ROOT = SCRIPT.parents[2]
BASE = "a" * 40
HEAD_A = "b" * 40
HEAD_B = "c" * 40
HEAD_C = "d" * 40
HEAD_D = "e" * 40


def policy(**overrides: object) -> dict:
    value = {
        "contract": MODULE.POLICY_CONTRACT,
        "observation_started_at": "2026-08-15T00:00:00Z",
        "receipt_retention_days": 30,
        "image_outcome_lookback_hours": 24,
        "maximum_pages_per_query": 3,
        "maximum_receipt_downloads": 20,
        "minimum_docs_only_heads": 1,
        "minimum_native_heads": 2,
        "minimum_serving_impacted_heads": 1,
        "minimum_worker_impacted_heads": 1,
        "minimum_serving_narrowed_heads": 1,
        "minimum_worker_avoided_heads": 1,
        "require_zero_integrity_failures": True,
        "require_zero_docs_only_gate_failures": True,
        "require_successful_authoritative_image_outcomes": True,
    }
    value.update(overrides)
    return value


def run(
    run_id: int,
    workflow: str,
    head: str = BASE,
    conclusion: str = "success",
    attempt: int = 1,
) -> dict:
    return {
        "id": run_id,
        "run_attempt": attempt,
        "event": "workflow_run",
        "status": "completed",
        "conclusion": conclusion,
        "path": workflow,
        "head_branch": MODULE.DEFAULT_BRANCH,
        "head_sha": head,
        "created_at": "2026-08-16T00:00:00Z",
        "updated_at": "2026-08-16T00:01:00Z",
    }


def artifact(artifact_id: int, run_value: dict, name: str) -> dict:
    return {
        "id": artifact_id,
        "name": name,
        "expired": False,
        "size_in_bytes": 1024,
        "created_at": "2026-08-16T00:00:30Z",
        "workflow_run": {"id": run_value["id"], "head_sha": run_value["head_sha"]},
    }


def artifact_name(stream: str, attempt: int = 1) -> str:
    if stream == MODULE.PR_GATE_STREAM:
        return f"pr-gate-impact-docs-only-v3-attempt-{attempt}"
    return f"native-image-impact-observation-v2-attempt-{attempt}"


def pages(root: Path, collection: str, values: list[dict]) -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / "001.json").write_text(
        json.dumps({"total_count": len(values), collection: values}),
        encoding="utf-8",
    )


def artifact_catalog(root: Path, run_value: dict, values: list[dict]) -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / f"{run_value['id']}.json").write_text(
        json.dumps({"total_count": len(values), "artifacts": values}),
        encoding="utf-8",
    )


def entry(stream: str, artifact_id: int, producer_id: int) -> dict:
    return {
        "stream": stream,
        "artifact_id": artifact_id,
        "artifact_name": artifact_name(stream),
        "artifact_created_at": "2026-08-16T00:00:30Z",
        "artifact_size_bytes": 1024,
        "producer_run_id": producer_id,
        "producer_run_attempt": 1,
        "producer_event": "workflow_run",
        "producer_head_sha": BASE,
        "producer_created_at": "2026-08-16T00:00:00Z",
        "producer_updated_at": "2026-08-16T00:01:00Z",
        "producer_workflow": (
            MODULE.PR_GATE_WORKFLOW if stream == MODULE.PR_GATE_STREAM
            else MODULE.NATIVE_WORKFLOW
        ),
    }


def pr_gate_receipt(blobs: dict[str, str], head: str = HEAD_A, conclusion: str = "success") -> dict:
    return {
        "contract": MODULE.PR_GATE_CONTRACT,
        "rollout": "observe",
        "mode": "docs-only",
        "reason": "internal-markdown-only",
        "changed_file_count": 1,
        "files_sha256": "1" * 64,
        "authoritative_gate": "full",
        "repository": MODULE.REPOSITORY,
        "pull_request": 10,
        "base_sha": BASE,
        "head_sha": head,
        "policy_sha": BASE,
        "policy_blob_sha": blobs["pr_gate_classifier"],
        "gate_workflow_blob_sha": blobs["pr_gate_workflow"],
        "resolver_blob_sha": blobs["trusted_run_resolver"],
        "observer_workflow_blob_sha": blobs["pr_gate_observer"],
        "trusted_execution": "default-branch-workflow-run/v1",
        "gate_workflow_path": ".github/workflows/pr-gate.yml",
        "gate_run_id": 900,
        "gate_run_attempt": 1,
        "gate_run_head_sha": head,
        "gate_run_conclusion": conclusion,
    }


def native_receipt(
    blobs: dict[str, str],
    *,
    pr: int,
    head: str,
    worker: bool,
    serving: dict[str, bool] | None = None,
    legacy_serving: dict[str, bool] | None = None,
    legacy_worker: bool = True,
) -> dict:
    if serving is None:
        serving = {"generic": True, "lambda": False, "functions": False}
    if legacy_serving is None:
        legacy_serving = {
            "generic": True,
            "lambda": True,
            "functions": True,
        }
    changed_paths = ["src/Honua.Core/Models/Resource.cs"]
    return {
        "schema": MODULE.NATIVE_CONTRACT,
        "repository": MODULE.REPOSITORY,
        "pull_request": pr,
        "base_sha": BASE,
        "head_sha": head,
        "policy_sha": BASE,
        "policy_blob_sha": blobs["native_classifier"],
        "routing_policy_blob_sha": blobs["native_routing_policy"],
        "gate_workflow_blob_sha": blobs["pr_gate_workflow"],
        "serving_workflow_blob_sha": blobs["serving_workflow"],
        "worker_workflow_blob_sha": blobs["worker_workflow"],
        "resolver_blob_sha": blobs["trusted_run_resolver"],
        "observer_workflow_blob_sha": blobs["native_observer"],
        "policy_inputs_sha256": blobs["native_policy_inputs_sha256"],
        "trusted_execution": "default-branch-workflow-run/v1",
        "gate_workflow_path": ".github/workflows/pr-gate.yml",
        "gate_run_id": 901 + pr,
        "gate_run_attempt": 1,
        "gate_run_head_sha": head,
        "gate_run_conclusion": "success",
        "changed_paths": changed_paths,
        "changed_paths_sha256": hashlib.sha256(
            json.dumps(changed_paths, separators=(",", ":")).encode("utf-8")
        ).hexdigest(),
        "mode": "observe",
        "mutation": "none",
        "legacy": {
            "serving_trigger": any(legacy_serving.values()),
            "serving_variants": legacy_serving,
            "worker_trigger": legacy_worker,
        },
        "candidate": {"serving_variants": serving, "worker_build": worker},
        "comparison": {
            "serving_candidate_only": False,
            "serving_legacy_only": False,
            "worker_candidate_only": False,
            "worker_legacy_only": legacy_worker and not worker,
        },
    }


def archive(root: Path, artifact_id: int, stream: str, receipt: dict, *, unsafe: bool = False) -> None:
    root.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(root / f"{artifact_id}.zip", "w") as value:
        if unsafe:
            value.writestr("../escape.json", "{}")
            return
        if stream == MODULE.PR_GATE_STREAM:
            value.writestr(MODULE.PR_GATE_RECEIPT, json.dumps(receipt))
        else:
            value.writestr(MODULE.NATIVE_RECEIPT, json.dumps(receipt))
            value.writestr(MODULE.NATIVE_SUMMARY, "# observation\n")


def image_run(
    run_id: int,
    workflow: str,
    head: str,
    pr: int,
    conclusion: str = "success",
    base: str = BASE,
) -> dict:
    return {
        "id": run_id,
        "run_attempt": 1,
        "event": "pull_request",
        "status": "completed",
        "conclusion": conclusion,
        "path": workflow,
        "head_sha": head,
        "created_at": "2026-08-16T00:00:00Z",
        "updated_at": "2026-08-16T00:02:00Z",
        "pull_requests": [
            {
                "number": pr,
                "base": {"sha": base},
                "head": {"sha": head},
            }
        ],
    }


def test_policy_and_discovery() -> None:
    MODULE.load_policy(policy())
    for invalid in (
        policy(receipt_retention_days=91),
        policy(maximum_pages_per_query=11),
        policy(image_outcome_lookback_hours=49),
        policy(require_zero_integrity_failures=False),
    ):
        try:
            MODULE.load_policy(invalid)
        except ValueError:
            pass
        else:
            raise AssertionError("unsafe policy must fail closed")

    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        pr_run = run(1, MODULE.PR_GATE_WORKFLOW)
        native_run = run(2, MODULE.NATIVE_WORKFLOW)
        pages(root / "pr-runs", "workflow_runs", [pr_run])
        pages(root / "native-runs", "workflow_runs", [native_run])
        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, artifact_name(MODULE.PR_GATE_STREAM))
        ])
        artifact_catalog(root / "native-artifacts", native_run, [
            artifact(12, native_run, artifact_name(MODULE.NATIVE_STREAM))
        ])
        result = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, tzinfo=timezone.utc),
        )
        assert [item["artifact_id"] for item in result["artifacts"]] == [12, 11]
        assert result["integrity_failures"] == []

        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, "pr-gate-impact-observation-10-old-dynamic-name")
        ])
        old_name = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, tzinfo=timezone.utc),
        )
        assert old_name["integrity_failures"] == []
        assert any(
            item["reason"] == "observation-receipt-not-emitted"
            for item in old_name["exclusions"]
        )

        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, artifact_name(MODULE.PR_GATE_STREAM)),
            artifact(13, pr_run, artifact_name(MODULE.PR_GATE_STREAM)),
        ])
        ambiguous = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, tzinfo=timezone.utc),
        )
        assert any(
            item["reason"] == "observation-artifact-ambiguous"
            for item in ambiguous["integrity_failures"]
        )

        rerun = run(1, MODULE.PR_GATE_WORKFLOW, attempt=2)
        pages(root / "pr-runs", "workflow_runs", [rerun])
        artifact_catalog(root / "pr-artifacts", rerun, [
            artifact(11, rerun, artifact_name(MODULE.PR_GATE_STREAM, attempt=1)),
            artifact(13, rerun, artifact_name(MODULE.PR_GATE_STREAM, attempt=2)),
        ])
        current_attempt = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, tzinfo=timezone.utc),
        )
        assert [
            item["artifact_id"] for item in current_attempt["artifacts"]
            if item["stream"] == MODULE.PR_GATE_STREAM
        ] == [13]
        assert current_attempt["integrity_failures"] == []


def test_summary_requires_real_candidate_and_image_evidence() -> None:
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        archives = root / "archives"
        entries = [
            entry(MODULE.PR_GATE_STREAM, 101, 1),
            entry(MODULE.NATIVE_STREAM, 102, 2),
            entry(MODULE.NATIVE_STREAM, 103, 3),
            entry(MODULE.NATIVE_STREAM, 104, 4),
        ]
        entries[1]["producer_head_sha"] = BASE
        entries[2]["producer_head_sha"] = BASE
        entries[3]["producer_head_sha"] = BASE
        archive(archives, 101, MODULE.PR_GATE_STREAM, pr_gate_receipt(blobs))
        archive(archives, 102, MODULE.NATIVE_STREAM, native_receipt(blobs, pr=11, head=HEAD_B, worker=True))
        archive(archives, 103, MODULE.NATIVE_STREAM, native_receipt(blobs, pr=12, head=HEAD_C, worker=False))
        lambda_only = {"generic": False, "lambda": True, "functions": False}
        archive(
            archives,
            104,
            MODULE.NATIVE_STREAM,
            native_receipt(
                blobs,
                pr=13,
                head=HEAD_D,
                worker=False,
                serving=lambda_only,
                legacy_serving=lambda_only,
                legacy_worker=False,
            ),
        )
        serving = [
            image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11),
            image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 12),
            image_run(205, MODULE.SERVING_WORKFLOW, HEAD_D, 13),
        ]
        worker = [
            image_run(203, MODULE.WORKER_WORKFLOW, HEAD_B, 11),
            image_run(204, MODULE.WORKER_WORKFLOW, HEAD_C, 12),
        ]
        pages(root / "serving", "workflow_runs", serving)
        pages(root / "worker", "workflow_runs", worker)
        index = {
            "contract": MODULE.INDEX_CONTRACT,
            "repository": MODULE.REPOSITORY,
            "cutoff": "2026-08-15T00:00:00Z",
            "artifacts": entries,
            "exclusions": [],
            "integrity_failures": [],
        }
        ledger = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert ledger["recommendation"] == "eligible-for-human-promotion-review"
        assert ledger["counts"]["docs_only_success_heads"] == 1
        assert ledger["counts"]["native_countable_heads"] == 3
        assert ledger["counts"]["serving_narrowed_heads"] == 2
        assert ledger["counts"]["worker_impacted_heads"] == 1
        assert ledger["counts"]["worker_avoided_heads"] == 1
        assert all(ledger["gates"].values())

        stale_serving = [
            image_run(207, MODULE.SERVING_WORKFLOW, HEAD_B, 11, base="f" * 40),
            image_run(208, MODULE.SERVING_WORKFLOW, HEAD_B, 99),
            serving[1],
            serving[2],
        ]
        pages(root / "serving", "workflow_runs", stale_serving)
        stale = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert stale["recommendation"] == "observe-more"
        assert stale["counts"]["authoritative_image_outcome_failures"] == 1
        assert stale["image_outcome_failures"][0]["serving"][
            "identity_mismatch_run_ids"
        ] == [207, 208]

        pages(root / "serving", "workflow_runs", serving)
        pages(root / "worker", "workflow_runs", [worker[0]])
        missing = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert missing["recommendation"] == "observe-more"
        assert missing["counts"]["authoritative_image_outcome_failures"] == 1
        assert missing["gates"]["authoritative_image_outcomes_clean"] is False


def test_integrity_failures_do_not_count() -> None:
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        archives = root / "archives"
        index = {
            "contract": MODULE.INDEX_CONTRACT,
            "artifacts": [entry(MODULE.PR_GATE_STREAM, 301, 1)],
            "exclusions": [],
            "integrity_failures": [],
        }
        pages(root / "serving", "workflow_runs", [])
        pages(root / "worker", "workflow_runs", [])
        for field in (
            "observer_workflow_blob_sha",
            "resolver_blob_sha",
            "gate_workflow_blob_sha",
        ):
            bad = pr_gate_receipt(blobs)
            bad[field] = "f" * 40
            archive(archives, 301, MODULE.PR_GATE_STREAM, bad)
            ledger = MODULE.summarize(
                index,
                archives,
                root / "serving",
                root / "worker",
                policy(),
                REPOSITORY_ROOT,
            )
            assert ledger["counts"]["validated_pr_gate_receipts"] == 0
            assert ledger["counts"]["integrity_failures"] == 1
            assert ledger["gates"]["integrity_clean"] is False

        archive(archives, 301, MODULE.PR_GATE_STREAM, {}, unsafe=True)
        unsafe = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert "member set" in unsafe["integrity_failures"][0]["reason"]


def test_workflows_are_read_only_and_attempt_bound() -> None:
    ledger = (REPOSITORY_ROOT / ".github/workflows/impact-routing-evidence-ledger.yml").read_text(
        encoding="utf-8"
    )
    pr_gate = (REPOSITORY_ROOT / MODULE.PR_GATE_WORKFLOW).read_text(encoding="utf-8")
    native = (REPOSITORY_ROOT / MODULE.NATIVE_WORKFLOW).read_text(encoding="utf-8")
    assert "permissions:\n  actions: read\n  contents: read\n" in ledger
    assert ledger.count("permissions:") == 1
    assert "ref: ${{ github.workflow_sha }}" in ledger
    assert "actions/runs/${run_id}/artifacts?per_page=100" in ledger
    assert "producer_count > MAXIMUM_CATALOGS" in ledger
    assert "serving-image-boundary.yml/runs" in ledger
    assert "worker-gdal-image.yml/runs" in ledger
    assert "actions: write" not in ledger
    assert "contents: write" not in ledger
    assert "pull_request_target" not in ledger
    assert (
        "name: pr-gate-impact-${{ steps.classify.outputs.mode }}-v3-attempt-"
        "${{ github.run_attempt }}" in pr_gate
    )
    assert (
        "name: native-image-impact-observation-v2-attempt-${{ github.run_attempt }}"
        in native
    )


test_policy_and_discovery()
test_summary_requires_real_candidate_and_image_evidence()
test_integrity_failures_do_not_count()
test_workflows_are_read_only_and_attempt_bound()
print("impact-routing-evidence-ledger=ok mode=report-only")
