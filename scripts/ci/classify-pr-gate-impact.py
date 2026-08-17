#!/usr/bin/env python3
"""Classify a pull-request diff for non-authoritative PR Gate observation."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import PurePosixPath
from typing import Any


CONTRACT = "honua.pr-gate-impact-observation/v3"
MAX_FILES = 3_000
DOCS_PREFIX = "docs/internal/"

# Markdown under `docs/internal/` whose *content* is asserted by a gate step the
# docs-only route would skip. Editing one of these files alone can turn the
# authoritative gate red, so the docs-only class must never claim them.
#
# This is the only routing policy in this module; the test file owns the
# reference-only allowlist and the source globs it scans, so that curating those
# lists never changes this file's blob and never resets an observation cohort
# that binds it.
#
# `scripts/ci/classify-pr-gate-impact.test.py` rescans the gate's own inputs --
# including paths built segment-by-segment through `CombinePath`/`Path.Combine`,
# which is the house style -- and fails when a `docs/internal/**.md` reference is
# neither listed here nor listed as reference-only, so this set cannot silently
# fall behind the gate.
LEAN_GATE_GOVERNED_DOCS = frozenset(
    {
        # AuditCoverageMatrixDriftTests joins the documented auth-route rows
        # against DefaultAuditActionResolver in both directions.
        "docs/internal/operator/audit-coverage-matrix.md",
        # ModuleDependencyPolicyTests.MatrixAndAdr_ShouldCrossReference_EachOther
        # requires the ADR to exist and to contain "ModuleDependencyPolicyTests"
        # and "Dependency direction matrix".
        "docs/internal/contributor/adr/0047-module-dependency-policy.md",
        # ServingImageBoundaryTests asserts exact sentences about which tags a
        # GA promotion may move.
        "docs/internal/contributor/release-bundle.md",
        # PublicInterfaceProofLedgerTests uses this document as on-disk proof
        # evidence; treat it as governed rather than reason about its parser.
        "docs/internal/contributor/public-interface-quality-model.md",
        # DocumentationMatrixDriftTests.GeocodeServerMatrix_... compares the
        # documented GeocodeServer route roster against feature-catalog.json in
        # both directions.
        "docs/internal/spikes/geocode-server-matrix.md",
        # validate-early-failure-observe.sh (a lean-gate step) greps this file
        # for two exact phrases.
        "docs/internal/ci/merge-train-early-failure-observe.md",
        # base-image-mirrors.sh --verify-inventory-doc parses this inventory in
        # the always-on "Verify .NET base-image security inventory" step. That
        # step is guarded to stay unconditional, but the document is listed here
        # as well so the class stays sound if the gate is ever restructured.
        "docs/internal/security/code-scanning-2026-Q2-remediation.md",
    }
)

EXPECTED_REPOSITORY = "honua-io/honua-server"
ALLOWED_GATE_CONCLUSIONS = {
    "success",
    "failure",
    "cancelled",
    "timed_out",
    "action_required",
    "neutral",
    "skipped",
    "stale",
    "startup_failure",
}


def _full(
    reason: str,
    *,
    count: int = 0,
    digest: str = "",
    identity: dict[str, Any] | None = None,
) -> dict[str, Any]:
    identity = identity or {
        "repository": None,
        "pull_request": None,
        "base_sha": None,
        "head_sha": None,
        "policy_sha": None,
        "policy_blob_sha": None,
        "gate_workflow_blob_sha": None,
        "resolver_blob_sha": None,
        "observer_workflow_blob_sha": None,
        "trusted_execution": None,
        "gate_workflow_path": None,
        "gate_run_id": None,
        "gate_run_attempt": None,
        "gate_run_head_sha": None,
        "gate_run_conclusion": None,
    }
    return {
        "contract": CONTRACT,
        "rollout": "observe",
        "mode": "full",
        "reason": reason,
        "changed_file_count": count,
        "files_sha256": digest,
        "authoritative_gate": "full",
        **identity,
    }


def _safe_path(value: object) -> str | None:
    if not isinstance(value, str) or not value or "\\" in value:
        return None
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        return None
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or str(path) != value:
        return None
    return value


def classify(payload: object) -> dict[str, Any]:
    if not isinstance(payload, dict):
        return _full("invalid-payload")
    unavailable = payload.get("unavailable_reason")
    if unavailable:
        return _full("diff-unavailable")
    changed_files = payload.get("changed_files")
    files = payload.get("files")
    repository = payload.get("repository")
    pull_request = payload.get("pull_request")
    base_sha = payload.get("base_sha")
    head_sha = payload.get("head_sha")
    policy_sha = payload.get("policy_sha")
    policy_blob_sha = payload.get("policy_blob_sha")
    gate_workflow_blob_sha = payload.get("gate_workflow_blob_sha")
    resolver_blob_sha = payload.get("resolver_blob_sha")
    observer_workflow_blob_sha = payload.get("observer_workflow_blob_sha")
    trusted_execution = payload.get("trusted_execution")
    gate_workflow_path = payload.get("gate_workflow_path")
    gate_run_id = payload.get("gate_run_id")
    gate_run_attempt = payload.get("gate_run_attempt")
    gate_run_head_sha = payload.get("gate_run_head_sha")
    gate_run_conclusion = payload.get("gate_run_conclusion")
    if (
        repository != EXPECTED_REPOSITORY
        or not isinstance(pull_request, int)
        or isinstance(pull_request, bool)
        or pull_request < 1
        or not isinstance(changed_files, int)
        or isinstance(changed_files, bool)
        or not isinstance(files, list)
        or not isinstance(base_sha, str)
        or not isinstance(head_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", base_sha) is None
        or re.fullmatch(r"[0-9a-f]{40}", head_sha) is None
        or not isinstance(policy_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", policy_sha) is None
        or not isinstance(policy_blob_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", policy_blob_sha) is None
        or not isinstance(gate_workflow_blob_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", gate_workflow_blob_sha) is None
        or not isinstance(resolver_blob_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", resolver_blob_sha) is None
        or not isinstance(observer_workflow_blob_sha, str)
        or re.fullmatch(r"[0-9a-f]{40}", observer_workflow_blob_sha) is None
        or trusted_execution != "default-branch-workflow-run/v1"
        or gate_workflow_path != ".github/workflows/pr-gate.yml"
        or not isinstance(gate_run_id, int)
        or isinstance(gate_run_id, bool)
        or gate_run_id < 1
        or not isinstance(gate_run_attempt, int)
        or isinstance(gate_run_attempt, bool)
        or gate_run_attempt < 1
        or gate_run_head_sha != head_sha
        or gate_run_conclusion not in ALLOWED_GATE_CONCLUSIONS
    ):
        return _full("invalid-diff-identity")
    identity = {
        "repository": repository,
        "pull_request": pull_request,
        "base_sha": base_sha,
        "head_sha": head_sha,
        "policy_sha": policy_sha,
        "policy_blob_sha": policy_blob_sha,
        "gate_workflow_blob_sha": gate_workflow_blob_sha,
        "resolver_blob_sha": resolver_blob_sha,
        "observer_workflow_blob_sha": observer_workflow_blob_sha,
        "trusted_execution": trusted_execution,
        "gate_workflow_path": gate_workflow_path,
        "gate_run_id": gate_run_id,
        "gate_run_attempt": gate_run_attempt,
        "gate_run_head_sha": gate_run_head_sha,
        "gate_run_conclusion": gate_run_conclusion,
    }
    def full(reason: str, count: int = 0, digest: str = "") -> dict[str, Any]:
        return _full(reason, count=count, digest=digest, identity=identity)
    if changed_files < 1 or changed_files >= MAX_FILES:
        return full("unbounded-file-count", count=max(changed_files, 0))
    if len(files) != changed_files:
        return full("truncated-file-list", count=changed_files)

    normalized: list[dict[str, str]] = []
    names: set[str] = set()
    for item in files:
        if not isinstance(item, dict):
            return full("invalid-file-record", count=changed_files)
        filename = _safe_path(item.get("filename"))
        status = item.get("status")
        if filename is None or not isinstance(status, str):
            return full("unsafe-file-record", count=changed_files)
        if filename in names:
            return full("duplicate-file-record", count=changed_files)
        names.add(filename)
        normalized.append({"filename": filename, "status": status})

    normalized.sort(key=lambda item: (item["filename"], item["status"]))
    digest = hashlib.sha256(
        json.dumps(normalized, separators=(",", ":"), sort_keys=True).encode("utf-8")
    ).hexdigest()
    for item in normalized:
        filename = item["filename"]
        if item["status"] not in {"added", "modified"}:
            return full("rename-delete-or-unknown-status", count=changed_files, digest=digest)
        if not filename.startswith(DOCS_PREFIX) or not filename.endswith(".md"):
            return full("path-requires-full-gate", count=changed_files, digest=digest)
        if filename in LEAN_GATE_GOVERNED_DOCS:
            return full("lean-gate-governed-doc", count=changed_files, digest=digest)

    return {
        "contract": CONTRACT,
        "rollout": "observe",
        "mode": "docs-only",
        "reason": "internal-markdown-only",
        "changed_file_count": changed_files,
        "files_sha256": digest,
        "authoritative_gate": "full",
        **identity,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        with open(args.input, encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, json.JSONDecodeError):
        payload = {"unavailable_reason": "input-read-failed"}
    result = classify(payload)
    with open(args.output, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
        handle.write("\n")
    print(
        f"pr-gate-impact={result['mode']} reason={result['reason']} "
        f"files={result['changed_file_count']} authoritative=full"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
