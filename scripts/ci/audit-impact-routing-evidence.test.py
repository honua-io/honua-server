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
        "receipt_index_grace_minutes": 90,
        "maximum_receipt_loss_ratio": 0.05,
        "promotion_green_days": 7,
        "maximum_pages_per_query": 3,
        "maximum_producer_run_catalogs": 40,
        "maximum_receipt_downloads": 20,
        "minimum_docs_only_heads": 1,
        "minimum_native_heads": 2,
        "minimum_serving_impacted_heads": 1,
        "minimum_worker_impacted_heads": 1,
        "minimum_serving_narrowed_heads": 1,
        "minimum_worker_avoided_heads": 1,
        "minimum_serving_reuse_heads": 1,
        "minimum_worker_reuse_heads": 1,
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
    return f"native-image-impact-observation-v3-attempt-{attempt}"


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


def emission(indexed: int = 1, missing: int = 0, pending: int = 0, skipped: int = 0) -> dict:
    return MODULE.receipt_emission({
        MODULE.NATIVE_STREAM: {
            "observer_runs_successful": indexed + missing + pending + skipped,
            "receipts_indexed": indexed,
            "receipts_skipped": skipped,
            "receipts_pending_index": pending,
            "receipts_missing": missing,
        },
    })


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
    image_inputs: dict[str, str] | None = None,
    tree: str = "merge",
    gate_run_id: int | None = None,
) -> dict:
    if serving is None:
        serving = {"generic": True, "lambda": False, "functions": False}
    if legacy_serving is None:
        legacy_serving = {
            "generic": True,
            "lambda": True,
            "functions": True,
        }
    if image_inputs is None:
        image_inputs = {
            name: hashlib.sha256(f"{name}:{head}".encode("utf-8")).hexdigest()
            for name in MODULE.IMAGE_INPUT_CLASSES
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
        "gate_run_id": gate_run_id if gate_run_id is not None else 901 + pr,
        "gate_run_attempt": 1,
        "gate_run_head_sha": head,
        "gate_run_conclusion": "success",
        "changed_paths": changed_paths,
        "changed_paths_sha256": hashlib.sha256(
            json.dumps(changed_paths, separators=(",", ":")).encode("utf-8")
        ).hexdigest(),
        "mode": "observe",
        "mutation": "none",
        "image_input_digests": image_inputs,
        "image_input_tree": tree,
        "image_input_tree_sha": head if tree == "head" else ("9" * 39 + head[-1]),
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
    started: str = "2026-08-16T00:00:00Z",
    completed: str = "2026-08-16T00:02:00Z",
    live_head: str | None = None,
    associations: list[dict] | None = None,
) -> dict:
    if associations is None:
        associations = [
            {
                "number": pr,
                "base": {"sha": base},
                "head": {"sha": live_head if live_head is not None else head},
            }
        ]
    return {
        "id": run_id,
        "run_attempt": 1,
        "event": "pull_request",
        "status": "completed",
        "conclusion": conclusion,
        "path": workflow,
        "head_sha": head,
        "created_at": started,
        "run_started_at": started,
        "updated_at": completed,
        "pull_requests": associations,
    }


def test_policy_and_discovery() -> None:
    MODULE.load_policy(policy())
    for invalid in (
        policy(receipt_retention_days=91),
        policy(maximum_pages_per_query=11),
        policy(maximum_producer_run_catalogs=1601),
        policy(maximum_receipt_downloads=1001, maximum_producer_run_catalogs=1500),
        # The catalog bound must never be tighter than the download bound, or
        # it silently becomes the binding cap on window size again.
        policy(maximum_producer_run_catalogs=19),
        policy(image_outcome_lookback_hours=49),
        policy(require_zero_integrity_failures=False),
        # The promotion gate is "<5% loss"; a policy may not redefine it by
        # loosening its own budget, and an unbounded indexing grace would let
        # every missing receipt hide as "pending".
        policy(maximum_receipt_loss_ratio=0.06),
        policy(maximum_receipt_loss_ratio=0),
        policy(receipt_index_grace_minutes=361),
        policy(promotion_green_days=31),
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
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
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
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert old_name["integrity_failures"] == []
        assert any(
            item["reason"] == "observation-receipt-missing"
            for item in old_name["exclusions"]
        )

        # A recorded skip of a superseded source must NOT be counted as a
        # receipt-emission regression: that count is the only signal for a real
        # producer break.
        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, "pr-gate-impact-skipped-pull-request-moved-attempt-1")
        ])
        skipped = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert skipped["integrity_failures"] == []
        reasons = [item["reason"] for item in skipped["exclusions"]]
        assert "observation-skipped:pull-request-moved" in reasons
        assert "observation-receipt-missing" not in reasons
        assert MODULE.skipped_by_code(skipped["exclusions"]) == {"pull-request-moved": 1}

        # Two different skip codes on one attempt is producer ambiguity.
        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, "pr-gate-impact-skipped-pull-request-moved-attempt-1"),
            artifact(13, pr_run, "pr-gate-impact-skipped-pull-request-draft-attempt-1"),
        ])
        ambiguous_skip = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert any(
            item["reason"] == "observation-skip-marker-ambiguous"
            for item in ambiguous_skip["integrity_failures"]
        )

        # A marker for a different attempt must not mask a missing receipt.
        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, "pr-gate-impact-skipped-pull-request-moved-attempt-2")
        ])
        stale_skip = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert any(
            item["reason"] == "observation-receipt-missing"
            for item in stale_skip["exclusions"]
        )

        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, artifact_name(MODULE.PR_GATE_STREAM))
        ])

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
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
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
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
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
            "receipt_emission": emission(),
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
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 0
        assert ledger["counts"]["worker_reuse_eligible_heads"] == 0
        assert ledger["gates"]["serving_savings_sample_ready"]
        assert ledger["gates"]["worker_savings_sample_ready"]
        assert ledger["counts"]["worker_impacted_heads"] == 1
        assert ledger["counts"]["worker_avoided_heads"] == 1
        assert all(ledger["gates"].values())

        # #3343: `pull_requests` on a workflow run is a LIVE view of the pull
        # request. Once a later push moves the PR, every earlier head's run
        # reports the PR's CURRENT base/head — and GitHub often reports no
        # association at all. Neither says anything about the run, whose own
        # head_sha is the authoritative binding, so neither may reject it.
        # Rejecting them was the sole cause of all 19 authoritative-outcome
        # failures in run 32038145537.
        for label, moved in (
            ("moved live pointer", image_run(
                207, MODULE.SERVING_WORKFLOW, HEAD_B, 11, base="f" * 40, live_head="f" * 40,
            )),
            ("absent association", image_run(
                207, MODULE.SERVING_WORKFLOW, HEAD_B, 11, associations=[],
            )),
        ):
            pages(root / "serving", "workflow_runs", [moved, serving[1], serving[2]])
            tolerant = MODULE.summarize(
                index,
                archives,
                root / "serving",
                root / "worker",
                policy(),
                REPOSITORY_ROOT,
            )
            assert tolerant["counts"]["authoritative_image_outcome_failures"] == 0, label
            assert tolerant["recommendation"] == "eligible-for-human-promotion-review", label

        # An association naming a DIFFERENT pull request is the one thing the
        # field can still soundly prove, so it still rejects the run.
        pages(root / "serving", "workflow_runs", [
            image_run(208, MODULE.SERVING_WORKFLOW, HEAD_B, 99),
            serving[1],
            serving[2],
        ])
        foreign = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert foreign["recommendation"] == "observe-more"
        assert foreign["counts"]["authoritative_image_outcome_failures"] == 1
        assert foreign["image_outcome_failures"][0]["reason"] == "no-exact-head-image-run"
        assert foreign["image_outcome_failures"][0]["serving"][
            "foreign_pull_request_run_ids"
        ] == [208]

        # A head whose image work was CANCELLED was superseded by a later push.
        # It can never acquire a successful outcome, so it is an excluded head
        # rather than a permanent failure.
        pages(root / "serving", "workflow_runs", [
            image_run(209, MODULE.SERVING_WORKFLOW, HEAD_B, 11, conclusion="cancelled"),
            serving[1],
            serving[2],
        ])
        superseded = MODULE.summarize(
            index,
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert superseded["counts"]["authoritative_image_outcome_failures"] == 0
        assert superseded["counts"]["image_outcome_superseded_heads"] == 1
        assert superseded["gates"]["authoritative_image_outcomes_clean"]

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
            "receipt_emission": emission(),
            "integrity_failures": [],
        }
        pages(root / "serving", "workflow_runs", [])
        pages(root / "worker", "workflow_runs", [])
        # #3343: a stale policy input is COHORT DRIFT, not an integrity
        # violation. Any commit touching one of the nine pinned inputs — the
        # `actions/checkout` bump 741f0d7b5 did exactly this — moves the policy
        # generation, and every receipt already in the retention window then
        # describes the previous one. Treating that as an integrity failure is
        # what made all 209 native receipts fail at once and left the ledger
        # permanently red on routine repository maintenance.
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
            assert ledger["counts"]["validated_pr_gate_receipts"] == 0, field
            assert ledger["counts"]["integrity_failures"] == 0, field
            assert ledger["counts"]["receipts_superseded_policy_generation"] == 1, field
            assert ledger["gates"]["integrity_clean"] is True, field

            # ...but when the receipt's OWN declared policy head is the head the
            # ledger checked out, the two are directly comparable and a mismatch
            # is a real contradiction. Fail closed there.
            contradiction = MODULE.summarize(
                index,
                archives,
                root / "serving",
                root / "worker",
                policy(),
                REPOSITORY_ROOT,
                policy_head_sha=bad["policy_sha"],
            )
            assert contradiction["counts"]["integrity_failures"] == 1, field
            assert "contradicts its own declared policy head" in (
                contradiction["integrity_failures"][0]["reason"]
            ), field

        # A trust-boundary violation is never drift, whatever its policy head.
        for field, value in (
            ("trusted_execution", "unverified"),
            ("rollout", "enforce"),
            ("gate_run_head_sha", "f" * 40),
        ):
            tampered = pr_gate_receipt(blobs)
            tampered[field] = value
            archive(archives, 301, MODULE.PR_GATE_STREAM, tampered)
            strict = MODULE.summarize(
                index,
                archives,
                root / "serving",
                root / "worker",
                policy(),
                REPOSITORY_ROOT,
            )
            assert strict["counts"]["integrity_failures"] == 1, field
            assert strict["gates"]["integrity_clean"] is False, field

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
        "name: native-image-impact-observation-v3-attempt-${{ github.run_attempt }}"
        in native
    )


ALL_VARIANTS = {"generic": True, "lambda": True, "functions": True}


def shared_digests(tag: str = "shared") -> dict:
    return {
        name: hashlib.sha256(f"{name}:{tag}".encode("utf-8")).hexdigest()
        for name in MODULE.IMAGE_INPUT_CLASSES
    }


def reuse_ledger(
    receipts: list[dict],
    serving_runs: list[dict],
    worker_runs: list[dict],
    root: Path,
) -> dict:
    archives = root / "archives"
    entries = []
    for offset, receipt in enumerate(receipts):
        item = entry(MODULE.NATIVE_STREAM, 102 + offset, 2 + offset)
        item["producer_head_sha"] = BASE
        entries.append(item)
        archive(archives, 102 + offset, MODULE.NATIVE_STREAM, receipt)
    pages(root / "serving", "workflow_runs", serving_runs)
    pages(root / "worker", "workflow_runs", worker_runs)
    index = {
        "contract": MODULE.INDEX_CONTRACT,
        "repository": MODULE.REPOSITORY,
        "cutoff": "2026-08-15T00:00:00Z",
        "artifacts": entries,
        "exclusions": [],
        "receipt_emission": emission(),
        "integrity_failures": [],
    }
    return MODULE.summarize(
        index, archives, root / "serving", root / "worker", policy(), REPOSITORY_ROOT
    )


def test_exact_input_reuse_counts_when_routing_never_narrows() -> None:
    """#3204: identical merge-tree inputs on a later push are reuse-eligible."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    shared = shared_digests()
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        ledger = reuse_ledger(
            [
                native_receipt(
                    blobs, pr=11, head=head, worker=True, serving=ALL_VARIANTS,
                    legacy_serving=ALL_VARIANTS, image_inputs=shared, gate_run_id=gate,
                )
                for head, gate in ((HEAD_B, 1000), (HEAD_C, 1001))
            ],
            [
                image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T00:30:00Z"),
                image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T01:00:00Z", completed="2026-08-16T01:30:00Z"),
            ],
            [
                image_run(203, MODULE.WORKER_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T00:10:00Z"),
                image_run(204, MODULE.WORKER_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T01:00:00Z", completed="2026-08-16T01:10:00Z"),
            ],
            root,
        )
        assert ledger["counts"]["serving_narrowed_heads"] == 0
        assert ledger["counts"]["worker_avoided_heads"] == 0
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 1
        assert ledger["counts"]["worker_reuse_eligible_heads"] == 1
        assert ledger["reuse_eligible"]["serving"][0]["head_sha"] == HEAD_C
        assert ledger["reuse_eligible"]["worker"][0]["reused_from"]["worker"] == HEAD_B
        assert ledger["gates"]["serving_savings_sample_ready"]
        assert ledger["signals"]["serving_reuse_ready"]
        assert not ledger["signals"]["serving_narrowing_ready"]
        assert ledger["savings_mechanism"]["serving"] == ["exact-input-build-reuse"]
        assert ledger["savings_mechanism"]["worker"] == ["exact-input-build-reuse"]


def test_reuse_requires_the_attestation_to_exist_when_the_head_starts() -> None:
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    shared = shared_digests()
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        ledger = reuse_ledger(
            [
                native_receipt(
                    blobs, pr=11, head=head, worker=False, serving=ALL_VARIANTS,
                    legacy_serving=ALL_VARIANTS, legacy_worker=False,
                    image_inputs=shared, gate_run_id=gate,
                )
                for head, gate in ((HEAD_B, 1000), (HEAD_C, 1001))
            ],
            [
                # Overlapping builds: HEAD_C started before HEAD_B's evidence existed.
                image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T02:20:00Z"),
                image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T00:10:00Z", completed="2026-08-16T02:30:00Z"),
            ],
            [],
            root,
        )
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 0


def test_reuse_ordering_is_independent_of_observer_run_id() -> None:
    """A re-observation of an older head must not reorder the cohort."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    shared = shared_digests()
    counts = set()
    for gate_a, gate_b in ((1000, 1001), (5000, 1001)):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            ledger = reuse_ledger(
                [
                    native_receipt(
                        blobs, pr=11, head=HEAD_B, worker=False, serving=ALL_VARIANTS,
                        legacy_serving=ALL_VARIANTS, legacy_worker=False,
                        image_inputs=shared, gate_run_id=gate_a,
                    ),
                    native_receipt(
                        blobs, pr=11, head=HEAD_C, worker=False, serving=ALL_VARIANTS,
                        legacy_serving=ALL_VARIANTS, legacy_worker=False,
                        image_inputs=shared, gate_run_id=gate_b,
                    ),
                ],
                [
                    image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                              started="2026-08-16T00:00:00Z", completed="2026-08-16T00:30:00Z"),
                    image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                              started="2026-08-16T01:00:00Z", completed="2026-08-16T01:30:00Z"),
                ],
                [],
                root,
            )
            counts.add(ledger["counts"]["serving_reuse_eligible_heads"])
            if ledger["reuse_eligible"]["serving"]:
                assert ledger["reuse_eligible"]["serving"][0]["head_sha"] == HEAD_C
    assert counts == {1}


def test_reuse_only_credits_variants_the_workflow_actually_built() -> None:
    """Legacy case arms gate each variant, so an unbuilt variant is not evidence."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    shared = shared_digests()
    generic_only = {"generic": True, "lambda": False, "functions": False}
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        ledger = reuse_ledger(
            [
                # Legacy built generic only, even though the candidate wanted all three.
                native_receipt(
                    blobs, pr=11, head=HEAD_B, worker=False, serving=ALL_VARIANTS,
                    legacy_serving=generic_only, legacy_worker=False,
                    image_inputs=shared, gate_run_id=1000,
                ),
                native_receipt(
                    blobs, pr=11, head=HEAD_C, worker=False, serving=ALL_VARIANTS,
                    legacy_serving=ALL_VARIANTS, legacy_worker=False,
                    image_inputs=shared, gate_run_id=1001,
                ),
            ],
            [
                image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T00:30:00Z"),
                image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T01:00:00Z", completed="2026-08-16T01:30:00Z"),
            ],
            [],
            root,
        )
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 0


def test_reuse_keys_are_scoped_per_variant() -> None:
    """A collapsed variant digest must not credit one variant's build to another."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    collapsed = dict(shared_digests())
    collapsed["serving_functions"] = collapsed["serving_lambda"]
    lambda_only = {"generic": False, "lambda": True, "functions": False}
    functions_only = {"generic": False, "lambda": False, "functions": True}
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        ledger = reuse_ledger(
            [
                native_receipt(
                    blobs, pr=11, head=HEAD_B, worker=False, serving=lambda_only,
                    legacy_serving=lambda_only, legacy_worker=False,
                    image_inputs=collapsed, gate_run_id=1000,
                ),
                native_receipt(
                    blobs, pr=11, head=HEAD_C, worker=False, serving=functions_only,
                    legacy_serving=functions_only, legacy_worker=False,
                    image_inputs=collapsed, gate_run_id=1001,
                ),
            ],
            [
                image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T00:30:00Z"),
                image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T01:00:00Z", completed="2026-08-16T01:30:00Z"),
            ],
            [],
            root,
        )
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 0


def test_head_tree_receipts_are_never_reuse_evidence() -> None:
    """The images build the merge ref, so a head-tree digest cannot key reuse."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    shared = shared_digests()
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        ledger = reuse_ledger(
            [
                native_receipt(
                    blobs, pr=11, head=head, worker=False, serving=ALL_VARIANTS,
                    legacy_serving=ALL_VARIANTS, legacy_worker=False,
                    image_inputs=shared, gate_run_id=gate, tree="head",
                )
                for head, gate in ((HEAD_B, 1000), (HEAD_C, 1001))
            ],
            [
                image_run(201, MODULE.SERVING_WORKFLOW, HEAD_B, 11,
                          started="2026-08-16T00:00:00Z", completed="2026-08-16T00:30:00Z"),
                image_run(202, MODULE.SERVING_WORKFLOW, HEAD_C, 11,
                          started="2026-08-16T01:00:00Z", completed="2026-08-16T01:30:00Z"),
            ],
            [],
            root,
        )
        assert ledger["counts"]["native_countable_heads"] == 2
        assert ledger["counts"]["serving_reuse_eligible_heads"] == 0


def test_malformed_image_input_evidence_fails_closed() -> None:
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    broken_receipts = []
    missing = native_receipt(blobs, pr=11, head=HEAD_B, worker=True)
    del missing["image_input_digests"]
    broken_receipts.append(("image input digests", missing))
    newline = native_receipt(blobs, pr=11, head=HEAD_B, worker=True)
    newline["image_input_digests"]["worker"] += "\n"
    broken_receipts.append(("worker input digest", newline))
    mistyped = native_receipt(blobs, pr=11, head=HEAD_B, worker=True, tree="head")
    mistyped["image_input_tree_sha"] = "1" * 40
    broken_receipts.append(("image input tree identity", mistyped))
    unknown = native_receipt(blobs, pr=11, head=HEAD_B, worker=True)
    unknown["image_input_tree"] = "worktree"
    broken_receipts.append(("image input tree kind", unknown))
    for expected, receipt in broken_receipts:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            ledger = reuse_ledger([receipt], [], [], root)
            assert ledger["counts"]["native_countable_heads"] == 0
            assert not ledger["gates"]["integrity_clean"]
            assert expected in ledger["integrity_failures"][0]["reason"], expected


def test_full_mode_pr_gate_receipts_are_not_receipt_loss() -> None:
    """#3343: the >50% receipt loss was the reader, not the producer.

    `pr-gate-impact-observe.yml` names its receipt after the mode it classified,
    so a non-docs-only observation uploads `pr-gate-impact-full-v3-attempt-N`.
    The index only ever matched `docs-only`, so every `full` receipt — 222 of
    222 successful PR Gate observers in the 7-day window of run 32859724842 —
    was reported as a receipt that had never been emitted: a producer
    regression that had never happened.
    """
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        pr_run = run(1, MODULE.PR_GATE_WORKFLOW)
        pages(root / "pr-runs", "workflow_runs", [pr_run])
        pages(root / "native-runs", "workflow_runs", [])
        artifact_catalog(root / "pr-artifacts", pr_run, [
            artifact(11, pr_run, "pr-gate-impact-full-v3-attempt-1")
        ])
        (root / "native-artifacts").mkdir(parents=True, exist_ok=True)
        index = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert [item["artifact_id"] for item in index["artifacts"]] == [11]
        assert index["integrity_failures"] == []
        assert index["exclusions"] == []
        loss = index["receipt_emission"]["all"]
        assert loss["receipts_missing"] == 0
        assert loss["receipts_indexed"] == 1
        assert loss["loss_ratio"] == 0.0

        # It validates as a real receipt, and it does NOT join the docs-only
        # promotion sample — indexing it restores the loss metric, it does not
        # inflate the cohort.
        archives = root / "archives"
        full = pr_gate_receipt(blobs)
        full["mode"] = "full"
        full["reason"] = "source-change"
        entries = [entry(MODULE.PR_GATE_STREAM, 11, 1)]
        entries[0]["artifact_name"] = "pr-gate-impact-full-v3-attempt-1"
        archive(archives, 11, MODULE.PR_GATE_STREAM, full)
        pages(root / "serving", "workflow_runs", [])
        pages(root / "worker", "workflow_runs", [])
        ledger = MODULE.summarize(
            {**index, "artifacts": entries},
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert ledger["counts"]["integrity_failures"] == 0
        assert ledger["counts"]["validated_pr_gate_receipts"] == 1
        assert ledger["counts"]["docs_only_success_heads"] == 0
        assert ledger["receipt_loss_regression"] is False

        # The artifact name and the receipt body must agree about the mode:
        # evidence stored under a name that misdescribes it is not addressable.
        mislabelled = pr_gate_receipt(blobs)
        mislabelled["mode"] = "docs-only"
        archive(archives, 11, MODULE.PR_GATE_STREAM, mislabelled)
        mismatch = MODULE.summarize(
            {**index, "artifacts": entries},
            archives,
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert mismatch["counts"]["integrity_failures"] == 1
        assert "differs from its artifact name" in (
            mismatch["integrity_failures"][0]["reason"]
        )


def test_loss_separates_missing_from_not_yet_indexed() -> None:
    """#3343: "lost" and "not indexed yet" are different facts.

    GitHub finalises a run's artifact catalog after the run completes, so an
    observer that finished moments ago can legitimately list nothing. Counting
    those as loss puts a floor under the ratio that no producer fix can remove.
    """
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        now = datetime(2026, 8, 16, 12, tzinfo=timezone.utc)
        fresh = run(1, MODULE.PR_GATE_WORKFLOW)
        fresh["created_at"] = "2026-08-16T11:50:00Z"
        fresh["updated_at"] = "2026-08-16T11:55:00Z"
        stale = run(2, MODULE.PR_GATE_WORKFLOW)
        pages(root / "pr-runs", "workflow_runs", [fresh, stale])
        pages(root / "native-runs", "workflow_runs", [])
        artifact_catalog(root / "pr-artifacts", fresh, [])
        artifact_catalog(root / "pr-artifacts", stale, [])
        (root / "native-artifacts").mkdir(parents=True, exist_ok=True)
        index = MODULE.discover(
            root / "pr-runs",
            root / "native-runs",
            root / "pr-artifacts",
            root / "native-artifacts",
            policy(),
            now,
        )
        reasons = sorted(item["reason"] for item in index["exclusions"])
        assert reasons == [
            "observation-receipt-missing",
            "observation-receipt-pending-index",
        ]
        loss = index["receipt_emission"]["all"]
        # One owed, one still pending: pending is removed from the denominator
        # rather than counted as delivered.
        assert loss["receipts_pending_index"] == 1
        assert loss["receipts_missing"] == 1
        assert loss["receipts_owed"] == 1
        assert loss["loss_ratio"] == 1.0
        assert loss["measured"] is True

        pages(root / "serving", "workflow_runs", [])
        pages(root / "worker", "workflow_runs", [])
        ledger = MODULE.summarize(
            index,
            root / "archives",
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert ledger["receipt_loss_regression"] is True
        assert ledger["gates"]["receipt_loss_within_budget"] is False
        assert ledger["counts"]["observation_receipts_pending_index"] == 1
        assert ledger["counts"]["observation_receipts_missing"] == 1

        # A window that owed nothing is UNMEASURED. It blocks promotion without
        # being a regression to go red over.
        quiet = {**index, "receipt_emission": emission(indexed=0, pending=2)}
        idle = MODULE.summarize(
            quiet,
            root / "archives",
            root / "serving",
            root / "worker",
            policy(),
            REPOSITORY_ROOT,
        )
        assert idle["receipt_loss_regression"] is False
        assert idle["gates"]["receipt_loss_within_budget"] is False


def test_tombstones_quarantine_explicitly_and_expire() -> None:
    """#3343: unverifiable past evidence is named and dated, never widened away."""
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    issue = f"https://github.com/{MODULE.REPOSITORY}/issues/3343"
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        archives = root / "archives"
        broken = pr_gate_receipt(blobs)
        broken["trusted_execution"] = "unverified"
        archive(archives, 301, MODULE.PR_GATE_STREAM, broken)
        pages(root / "serving", "workflow_runs", [])
        pages(root / "worker", "workflow_runs", [])
        index = {
            "contract": MODULE.INDEX_CONTRACT,
            "artifacts": [entry(MODULE.PR_GATE_STREAM, 301, 1)],
            "exclusions": [],
            "receipt_emission": emission(),
            "integrity_failures": [],
        }

        def summarized(tombstones: object, now: datetime) -> dict:
            return MODULE.summarize(
                index,
                archives,
                root / "serving",
                root / "worker",
                policy(),
                REPOSITORY_ROOT,
                tombstone_value=tombstones,
                now=now,
            )

        live = {
            "contract": MODULE.TOMBSTONE_CONTRACT,
            "tombstones": [{
                "kind": "receipt",
                "producer_run_id": 1,
                "reason": "producer run artifacts were deleted before the audit",
                "issue": issue,
                "expires_at": "2026-12-01T00:00:00Z",
            }],
        }
        quarantined = summarized(live, datetime(2026, 8, 16, 12, tzinfo=timezone.utc))
        assert quarantined["counts"]["integrity_failures"] == 0
        assert quarantined["counts"]["quarantined_by_tombstone"] == 1
        assert quarantined["gates"]["integrity_clean"] is True
        assert quarantined["quarantined"][0]["tombstone"]["issue"] == issue

        # Past its expiry the quarantine stops working. That is the whole point:
        # it cannot become permanent by neglect.
        expired = summarized(live, datetime(2026, 12, 2, tzinfo=timezone.utc))
        assert expired["gates"]["integrity_clean"] is False
        assert any(
            "tombstone expired" in item["reason"]
            for item in expired["integrity_failures"]
        )

        # A tombstone matching nothing is reported so it can be removed.
        unused = summarized(
            {
                "contract": MODULE.TOMBSTONE_CONTRACT,
                "tombstones": [{**live["tombstones"][0], "producer_run_id": 99}],
            },
            datetime(2026, 8, 16, 12, tzinfo=timezone.utc),
        )
        assert unused["counts"]["stale_tombstones"] == 1
        assert unused["counts"]["integrity_failures"] == 1

    # A quarantine without an owner or an expiry is not a quarantine.
    for invalid in (
        {"kind": "receipt", "producer_run_id": 1, "reason": "x", "issue": issue},
        {"kind": "receipt", "producer_run_id": 1, "reason": "x",
         "issue": "https://example.invalid/3343", "expires_at": "2026-12-01T00:00:00Z"},
        {"kind": "receipt", "producer_run_id": 1, "reason": "",
         "issue": issue, "expires_at": "2026-12-01T00:00:00Z"},
        {"kind": "image-outcome", "reason": "x", "issue": issue,
         "expires_at": "2026-12-01T00:00:00Z"},
    ):
        try:
            MODULE.load_tombstones(
                {"contract": MODULE.TOMBSTONE_CONTRACT, "tombstones": [invalid]},
                datetime(2026, 8, 16, tzinfo=timezone.utc),
            )
        except ValueError:
            continue
        raise AssertionError("an unowned or undated tombstone must fail closed")


def test_repeated_audits_are_idempotent_and_retries_do_not_double_count() -> None:
    """The receipt store is append-only; reading it must be a pure function.

    Two properties the ledger depends on and nothing else was checking:
    re-auditing unchanged evidence produces an identical ledger, and a
    re-attempted observer that emits a second receipt for the SAME head
    collapses to one countable head instead of inflating the sample.
    """
    blobs = MODULE.current_blobs(REPOSITORY_ROOT)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        archives = root / "archives"
        shared = {
            name: hashlib.sha256(f"{name}:shared".encode("utf-8")).hexdigest()
            for name in MODULE.IMAGE_INPUT_CLASSES
        }
        entries = []
        for artifact_id, producer, gate in ((401, 41, 5001), (402, 42, 5002)):
            entries.append(entry(MODULE.NATIVE_STREAM, artifact_id, producer))
            archive(archives, artifact_id, MODULE.NATIVE_STREAM, native_receipt(
                blobs, pr=11, head=HEAD_B, worker=True,
                image_inputs=shared, gate_run_id=gate,
            ))
        pages(root / "serving", "workflow_runs", [
            image_run(501, MODULE.SERVING_WORKFLOW, HEAD_B, 11)
        ])
        pages(root / "worker", "workflow_runs", [
            image_run(502, MODULE.WORKER_WORKFLOW, HEAD_B, 11)
        ])
        index = {
            "contract": MODULE.INDEX_CONTRACT,
            "repository": MODULE.REPOSITORY,
            "cutoff": "2026-08-15T00:00:00Z",
            "artifacts": entries,
            "exclusions": [],
            "receipt_emission": emission(indexed=2),
            "integrity_failures": [],
        }
        now = datetime(2026, 8, 16, 12, tzinfo=timezone.utc)
        first = MODULE.summarize(
            index, archives, root / "serving", root / "worker",
            policy(), REPOSITORY_ROOT, now=now,
        )
        second = MODULE.summarize(
            index, archives, root / "serving", root / "worker",
            policy(), REPOSITORY_ROOT, now=now,
        )
        assert json.dumps(first, sort_keys=True) == json.dumps(second, sort_keys=True)
        assert first["counts"]["validated_native_receipts"] == 1
        assert first["counts"]["native_countable_heads"] == 1
        assert first["counts"]["integrity_failures"] == 0

        # Reversing the index order must not change the ledger either: the
        # store has no inherent order and neither may its reading.
        reversed_index = {**index, "artifacts": list(reversed(entries))}
        flipped = MODULE.summarize(
            reversed_index, archives, root / "serving", root / "worker",
            policy(), REPOSITORY_ROOT, now=now,
        )
        assert json.dumps(flipped, sort_keys=True) == json.dumps(first, sort_keys=True)


def test_trend_measures_the_consecutive_green_promotion_gate() -> None:
    """#3343: "green >=7 days with <5% loss" must be readable from the auditor."""
    def daily(day: int, *, green: bool = True, loss: float = 0.0) -> dict:
        return {
            "contract": MODULE.LEDGER_CONTRACT,
            "generated_at": f"2026-08-{day:02d}T14:00:00Z",
            "gates": {
                "integrity_clean": green,
                "receipt_loss_within_budget": green,
            },
            "counts": {"integrity_failures": 0 if green else 3},
            "receipt_emission": {"all": {"loss_ratio": loss, "measured": True}},
        }

    now = datetime(2026, 8, 25, 20, tzinfo=timezone.utc)
    ready = MODULE.trend(
        [daily(day, loss=0.01) for day in range(19, 26)], policy(), now
    )
    assert ready["consecutive_green_days"] == 7
    assert ready["promotion_gate_ready"] is True
    assert ready["maximum_loss_ratio_in_streak"] == 0.01

    # A red day breaks the streak rather than being averaged away.
    broken = MODULE.trend(
        [daily(day, green=day != 22) for day in range(19, 26)], policy(), now
    )
    assert broken["consecutive_green_days"] == 3
    assert broken["promotion_gate_ready"] is False

    # A day with NO ledger is a missing measurement, not a passing one.
    gap = MODULE.trend(
        [daily(day) for day in (19, 20, 21, 23, 24, 25)], policy(), now
    )
    assert gap["consecutive_green_days"] == 3
    assert gap["promotion_gate_ready"] is False

    # Two ledgers on one day: the worse one decides.
    conflicted = MODULE.trend(
        [daily(day) for day in range(19, 26)] + [daily(24, green=False)],
        policy(),
        now,
    )
    assert conflicted["consecutive_green_days"] == 1
    assert MODULE.trend_markdown(conflicted).startswith(
        "## Impact-routing ledger promotion trend"
    )

test_policy_and_discovery()
test_summary_requires_real_candidate_and_image_evidence()
test_integrity_failures_do_not_count()
test_workflows_are_read_only_and_attempt_bound()
test_exact_input_reuse_counts_when_routing_never_narrows()
test_reuse_requires_the_attestation_to_exist_when_the_head_starts()
test_reuse_ordering_is_independent_of_observer_run_id()
test_reuse_only_credits_variants_the_workflow_actually_built()
test_reuse_keys_are_scoped_per_variant()
test_head_tree_receipts_are_never_reuse_evidence()
test_malformed_image_input_evidence_fails_closed()
test_full_mode_pr_gate_receipts_are_not_receipt_loss()
test_loss_separates_missing_from_not_yet_indexed()
test_tombstones_quarantine_explicitly_and_expire()
test_repeated_audits_are_idempotent_and_retries_do_not_double_count()
test_trend_measures_the_consecutive_green_promotion_gate()
print("impact-routing-evidence-ledger=ok mode=report-only")
