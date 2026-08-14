#!/usr/bin/env python3
"""Wrap server-test build evidence for safe cross-workflow prebuild reuse."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import time
from pathlib import Path

BASE_PATH = Path(__file__).with_name("server-test-reuse-receipt.py")
BASE_SPEC = importlib.util.spec_from_file_location("server_test_reuse_receipt", BASE_PATH)
assert BASE_SPEC and BASE_SPEC.loader
BASE = importlib.util.module_from_spec(BASE_SPEC)
BASE_SPEC.loader.exec_module(BASE)

SCHEMA = "honua.server-test-prebuild-receipt/v1"
MAX_RECEIPT_BYTES = 128 * 1024
WORKFLOW_PATH = ".github/workflows/server-test-prebuild-observe.yml"
POLICY_PATHS = (
    ".github/actions/setup-dotnet-ci/action.yml",
    ".github/server-test-artifact-projects.json",
    ".github/server-test-prebuild-observe.json",
    ".github/server-test-reuse-benchmark.json",
    ".github/workflows/server-test-prebuild-benchmark.yml",
    WORKFLOW_PATH,
    "scripts/ci/benchmark-server-test-transfer.sh",
    "scripts/ci/package-server-test-binaries.sh",
    "scripts/ci/plan-server-test-prebuild-benchmark.py",
    "scripts/ci/plan-server-test-prebuild.py",
    "scripts/ci/restore-server-test-binaries.sh",
    "scripts/ci/server-test-prebuild-receipt.py",
    "scripts/ci/server-test-reuse-receipt.py",
    "scripts/ci/summarize-server-test-prebuild-benchmark.py",
    "scripts/ci/try-server-test-prebuild.sh",
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def policy_inputs(repo: Path, sha: str) -> tuple[str, list[dict]]:
    if len(sha) != 40 or any(char not in "0123456789abcdef" for char in sha):
        raise ValueError("policy SHA must be lowercase full hexadecimal")
    tree_sha = str(BASE.git(repo, "rev-parse", f"{sha}^{{tree}}"))
    inputs = []
    for path in POLICY_PATHS:
        try:
            data = BASE.git(repo, "cat-file", "blob", f"{sha}:{path}", binary=True)
        except Exception as exc:
            raise ValueError(f"required policy input is absent from {sha}: {path}") from exc
        assert isinstance(data, bytes)
        inputs.append({"path": path, "sha256": sha256(data)})
    return tree_sha, inputs


def fingerprint(receipt: dict) -> str:
    payload = {key: value for key, value in receipt.items() if key != "fingerprint"}
    return sha256(BASE.canonical(payload))


def build_receipt(
    *, source_repo: Path, policy_repo: Path, repository: str, pull_request: int,
    source_sha: str, policy_sha: str, project: str, configuration: str,
    dotnet_sdk: str, runner_os: str, runner_arch: str, runner_image: str,
    producer_run_id: int, producer_run_attempt: int, manifest_path: Path,
    archive_path: Path, now_epoch: int,
) -> dict:
    if not repository or "/" not in repository or pull_request < 1:
        raise ValueError("repository and pull request identity are required")
    if not runner_image or len(runner_image) > 128:
        raise ValueError("runner image identity is invalid")
    inner = BASE.build_receipt(
        repo=source_repo,
        source_sha=source_sha,
        project=project,
        configuration=configuration,
        dotnet_sdk=dotnet_sdk,
        runner_os=runner_os,
        runner_arch=runner_arch,
        run_id=producer_run_id,
        run_attempt=producer_run_attempt,
        manifest_path=manifest_path,
        archive_path=archive_path,
        now_epoch=now_epoch,
    )
    policy_tree_sha, inputs = policy_inputs(policy_repo, policy_sha)
    receipt = {
        "artifact": inner["artifact"],
        "created_at_epoch": inner["created_at_epoch"],
        "expires_at_epoch": inner["expires_at_epoch"],
        "inner_receipt": inner,
        "policy": {
            "inputs": inputs,
            "sha": policy_sha,
            "tree_sha": policy_tree_sha,
            "workflow_path": WORKFLOW_PATH,
        },
        "producer": {
            "run_attempt": producer_run_attempt,
            "run_id": producer_run_id,
            "runner_image": runner_image,
        },
        "schema": SCHEMA,
        "source": {
            "pull_request": pull_request,
            "repository": repository,
            "sha": source_sha,
        },
    }
    receipt["fingerprint"] = fingerprint(receipt)
    return receipt


def validate_receipt(
    receipt_bytes: bytes, *, source_repo: Path, policy_repo: Path, repository: str,
    pull_request: int, source_sha: str, policy_sha: str, project: str,
    configuration: str, dotnet_sdk: str, runner_os: str, runner_arch: str,
    runner_image: str, producer_run_id: int, producer_run_attempt: int,
    manifest_path: Path, archive_path: Path, now_epoch: int,
    producer_policy_sha: str | None = None,
) -> dict:
    if len(receipt_bytes) > MAX_RECEIPT_BYTES:
        raise ValueError("prebuild receipt exceeds its size bound")
    receipt = BASE.load_json_bytes(receipt_bytes, "prebuild receipt")
    required = {
        "artifact", "created_at_epoch", "expires_at_epoch", "fingerprint",
        "inner_receipt", "policy", "producer", "schema", "source",
    }
    if set(receipt) != required or receipt.get("schema") != SCHEMA:
        raise ValueError("prebuild receipt schema/keys are invalid")
    source = receipt.get("source")
    producer = receipt.get("producer")
    policy = receipt.get("policy")
    if not all(isinstance(value, dict) for value in (source, producer, policy)):
        raise ValueError("prebuild receipt identity sections must be objects")
    expected_source = {"pull_request": pull_request, "repository": repository, "sha": source_sha}
    expected_producer = {
        "run_attempt": producer_run_attempt,
        "run_id": producer_run_id,
        "runner_image": runner_image,
    }
    producer_policy_sha = producer_policy_sha or policy_sha
    policy_tree_sha, inputs = policy_inputs(policy_repo, producer_policy_sha)
    expected_policy = {
        "inputs": inputs,
        "sha": producer_policy_sha,
        "tree_sha": policy_tree_sha,
        "workflow_path": WORKFLOW_PATH,
    }
    if source != expected_source or producer != expected_producer or policy != expected_policy:
        raise ValueError("prebuild source/producer/policy identity does not match")
    _, current_inputs = policy_inputs(policy_repo, policy_sha)
    if inputs != current_inputs:
        raise ValueError("current trusted policy inputs differ from the producer policy")
    if receipt.get("fingerprint") != fingerprint(receipt):
        raise ValueError("prebuild receipt fingerprint is invalid")
    inner = BASE.validate_receipt(
        BASE.canonical(receipt.get("inner_receipt")),
        repo=source_repo,
        source_sha=source_sha,
        project=project,
        configuration=configuration,
        dotnet_sdk=dotnet_sdk,
        runner_os=runner_os,
        runner_arch=runner_arch,
        run_id=producer_run_id,
        run_attempt=producer_run_attempt,
        manifest_path=manifest_path,
        archive_path=archive_path,
        now_epoch=now_epoch,
    )
    if receipt.get("artifact") != inner.get("artifact"):
        raise ValueError("outer and inner artifact evidence differ")
    if receipt.get("created_at_epoch") != inner.get("created_at_epoch") or receipt.get(
        "expires_at_epoch"
    ) != inner.get("expires_at_epoch"):
        raise ValueError("outer and inner validity windows differ")
    return receipt


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "validate"))
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--policy-root", type=Path, required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--pull-request", type=positive_int, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--policy-sha", required=True)
    parser.add_argument("--producer-policy-sha")
    parser.add_argument("--project", required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--dotnet-sdk", required=True)
    parser.add_argument("--runner-os", required=True)
    parser.add_argument("--runner-arch", required=True)
    parser.add_argument("--runner-image", required=True)
    parser.add_argument("--producer-run-id", type=positive_int, required=True)
    parser.add_argument("--producer-run-attempt", type=positive_int, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--now-epoch", type=positive_int)
    args = parser.parse_args()
    now = args.now_epoch or int(time.time())
    common = dict(
        source_repo=args.source_root,
        policy_repo=args.policy_root,
        repository=args.repository,
        pull_request=args.pull_request,
        source_sha=args.source_sha.lower(),
        policy_sha=args.policy_sha.lower(),
        project=args.project,
        configuration=args.configuration,
        dotnet_sdk=args.dotnet_sdk,
        runner_os=args.runner_os,
        runner_arch=args.runner_arch,
        runner_image=args.runner_image,
        producer_run_id=args.producer_run_id,
        producer_run_attempt=args.producer_run_attempt,
        manifest_path=args.manifest,
        archive_path=args.archive,
        now_epoch=now,
    )
    if args.mode == "build":
        receipt = build_receipt(**common)
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_bytes(BASE.canonical(receipt) + b"\n")
        print(f"prebuild-receipt={receipt['fingerprint']}")
    else:
        receipt = validate_receipt(
            args.receipt.read_bytes(),
            producer_policy_sha=(args.producer_policy_sha or args.policy_sha).lower(),
            **common,
        )
        print(f"prebuild-receipt=accepted fingerprint={receipt['fingerprint']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
