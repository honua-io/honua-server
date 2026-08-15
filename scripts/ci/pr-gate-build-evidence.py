#!/usr/bin/env python3
"""Create and validate trusted, tree-equivalent PR Gate build evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import time
from pathlib import Path


SCHEMA = "honua.pr-gate-server-test-evidence/v1"
CONTEXT_SCHEMA = "honua.pr-gate-server-test-metadata/v1"
MANIFEST_SCHEMA = "honua.server-test-binaries.v1"
PRODUCER_WORKFLOW = ".github/workflows/pr-gate.yml"
OBSERVER_WORKFLOW = ".github/workflows/pr-gate-impact-observe.yml"
MAX_RECEIPT_BYTES = 256 * 1024
MAX_ARTIFACT_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024
MAX_UNPACKED_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_ENTRIES = 100_000
MAX_TTL_SECONDS = 24 * 60 * 60
SHA = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^[0-9a-f]{64}$")
ARTIFACT_NAME = re.compile(r"^pr-gate-server-test-binaries-[1-9][0-9]*-attempt-[1-9][0-9]*$")
ARCHIVE_NAME = re.compile(r"^server-test-binaries-[a-z0-9-]+\.tar\.gz$")
MANIFEST_NAME = re.compile(r"^server-test-binaries-[a-z0-9-]+\.manifest\.json$")

# A candidate that changes the producer, validator, routing, or train consumer
# contract is deliberately ineligible for reuse. It still follows the ordinary
# independent restore/build path and may land that policy change normally.
POLICY_PATHS = (
    ".github/actions/lean-gate/action.yml",
    ".github/actions/setup-dotnet-ci/action.yml",
    ".github/ci-shards.json",
    ".github/server-test-artifact-projects.json",
    ".github/server-test-prebuild-observe.json",
    ".github/workflows/ci.yml",
    ".github/workflows/merge-train.yml",
    OBSERVER_WORKFLOW,
    PRODUCER_WORKFLOW,
    "scripts/ci/honua-server-targeted-tests.sh",
    "scripts/ci/lib/jq-cr-safe.sh",
    "scripts/ci/merge-train/select.sh",
    "scripts/ci/merge-train/smart-ci.sh",
    "scripts/ci/merge-train/train.sh",
    "scripts/ci/package-server-test-binaries.sh",
    "scripts/ci/plan-server-test-prebuild.py",
    "scripts/ci/pr-gate-build-evidence.py",
    "scripts/ci/restore-server-test-binaries.sh",
    "scripts/ci/review-gate-snapshot.js",
    "scripts/ci/trusted-pr-workflow-run.js",
    "scripts/ci/validate-server-test-archive.py",
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":"), sort_keys=True).encode("utf-8")


def load_object(path: Path, label: str, max_bytes: int = MAX_RECEIPT_BYTES) -> dict:
    data = path.read_bytes()
    if len(data) > max_bytes:
        raise ValueError(f"{label} exceeds its size bound")

    def reject_duplicates(pairs: list[tuple[str, object]]) -> dict:
        result: dict = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"{label} contains duplicate key {key!r}")
            result[key] = value
        return result

    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=reject_duplicates)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"{label} is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object")
    return value


def git(repo: Path, *args: str, binary: bool = False) -> bytes | str:
    completed = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return completed.stdout if binary else completed.stdout.decode("utf-8").strip()


def require_sha(value: object, label: str) -> str:
    if not isinstance(value, str) or not SHA.fullmatch(value):
        raise ValueError(f"{label} must be a lowercase full commit SHA")
    return value


def require_positive(value: object, label: str, maximum: int | None = None) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise ValueError(f"{label} must be a positive integer")
    if maximum is not None and value > maximum:
        raise ValueError(f"{label} exceeds its bound")
    return value


def require_exact_keys(value: dict, keys: set[str], label: str) -> None:
    if set(value) != keys:
        raise ValueError(f"{label} keys are invalid")


def blob_sha256(repo: Path, sha: str, path: str) -> str:
    try:
        data = git(repo, "cat-file", "blob", f"{sha}:{path}", binary=True)
    except subprocess.CalledProcessError as exc:
        raise ValueError(f"required policy input is absent from {sha}: {path}") from exc
    assert isinstance(data, bytes)
    return sha256(data)


def policy_inputs(repo: Path, sha: str) -> tuple[str, list[dict]]:
    require_sha(sha, "policy SHA")
    tree = str(git(repo, "rev-parse", f"{sha}^{{tree}}"))
    return tree, [
        {"path": path, "sha256": blob_sha256(repo, sha, path)} for path in POLICY_PATHS
    ]


def assert_source_policy_matches(source_repo: Path, source_sha: str, inputs: list[dict]) -> None:
    expected_paths = list(POLICY_PATHS)
    actual_paths = [item.get("path") for item in inputs if isinstance(item, dict)]
    if actual_paths != expected_paths:
        raise ValueError("trusted policy input roster is invalid")
    for item in inputs:
        if set(item) != {"path", "sha256"} or not DIGEST.fullmatch(str(item["sha256"])):
            raise ValueError("trusted policy input entry is invalid")
        if blob_sha256(source_repo, source_sha, str(item["path"])) != item["sha256"]:
            raise ValueError(f"producer policy differs at {item['path']}")


def validate_manifest(value: dict, *, expected_project: str, source_sha: str, sdk: str) -> dict:
    keys = {
        "contract", "source_sha", "dotnet_sdk", "project", "artifact_suffix",
        "archive_file", "archive_sha256", "raw_bytes", "unpacked_bytes",
        "archive_bytes", "file_count", "package_milliseconds", "created_at_epoch",
        "expires_at_epoch", "retained_runtime_ids",
    }
    require_exact_keys(value, keys, "server-test manifest")
    suffix = value.get("artifact_suffix")
    archive_file = value.get("archive_file")
    if (
        value.get("contract") != MANIFEST_SCHEMA
        or value.get("source_sha") != source_sha
        or value.get("dotnet_sdk") != sdk
        or value.get("project") != expected_project
        or not isinstance(suffix, str)
        or not re.fullmatch(r"[a-z0-9-]+", suffix)
        or not isinstance(archive_file, str)
        or not ARCHIVE_NAME.fullmatch(archive_file)
        or archive_file != f"server-test-binaries-{suffix}.tar.gz"
        or not DIGEST.fullmatch(str(value.get("archive_sha256", "")))
        or value.get("retained_runtime_ids") != ["linux", "linux-x64", "unix"]
    ):
        raise ValueError(f"server-test manifest identity is invalid for {expected_project}")
    require_positive(value.get("raw_bytes"), "manifest raw bytes")
    require_positive(
        value.get("unpacked_bytes"), "manifest unpacked bytes", MAX_UNPACKED_BYTES
    )
    require_positive(value.get("archive_bytes"), "manifest archive bytes", MAX_ARCHIVE_BYTES)
    require_positive(value.get("file_count"), "manifest file count", MAX_ARCHIVE_ENTRIES)
    package_ms = value.get("package_milliseconds")
    if not isinstance(package_ms, int) or isinstance(package_ms, bool) or package_ms < 0:
        raise ValueError("manifest package duration is invalid")
    created = require_positive(value.get("created_at_epoch"), "manifest creation time")
    expires = require_positive(value.get("expires_at_epoch"), "manifest expiry time")
    if not 0 < expires - created <= MAX_TTL_SECONDS:
        raise ValueError("manifest validity window is invalid")
    return value


def validate_plan(plan: dict) -> list[dict]:
    require_exact_keys(
        plan,
        {
            "contract", "consumers", "deferred_repeated_projects", "descriptor_reason",
            "producers", "selected_shard_count",
        },
        "server-test prebuild plan",
    )
    if plan.get("contract") != "honua.server-test-prebuild-plan/v1":
        raise ValueError("server-test prebuild plan contract is invalid")
    producers = plan.get("producers")
    if not isinstance(producers, list) or len(producers) > 2:
        raise ValueError("server-test prebuild producer plan is invalid")
    seen: set[str] = set()
    for item in producers:
        if not isinstance(item, dict) or set(item) != {
            "identity", "project", "project_suffix", "selected_shard_count"
        }:
            raise ValueError("server-test prebuild producer entry is invalid")
        project = item.get("project")
        suffix = item.get("project_suffix")
        if (
            not isinstance(project, str)
            or not project.endswith(".csproj")
            or project in seen
            or not isinstance(suffix, str)
            or not re.fullmatch(r"[a-z0-9-]+", suffix)
            or item.get("identity") != suffix
        ):
            raise ValueError("server-test prebuild producer identity is invalid")
        selected_count = require_positive(item.get("selected_shard_count"), "selected shard count")
        if selected_count < 2:
            raise ValueError("server-test prebuild producer is not repeated across shards")
        seen.add(project)
    return producers


def validate_artifact(value: dict, *, run_id: int, run_attempt: int) -> dict:
    require_exact_keys(
        value,
        {"artifact_bytes", "artifact_digest", "artifact_id", "artifact_name"},
        "PR Gate source artifact",
    )
    artifact_id = require_positive(value.get("artifact_id"), "source artifact id")
    artifact_bytes = require_positive(
        value.get("artifact_bytes"), "source artifact bytes", MAX_ARTIFACT_BYTES
    )
    expected_name = f"pr-gate-server-test-binaries-{run_id}-attempt-{run_attempt}"
    if (
        value.get("artifact_name") != expected_name
        or not ARTIFACT_NAME.fullmatch(str(value.get("artifact_name", "")))
        or not re.fullmatch(r"sha256:[0-9a-f]{64}", str(value.get("artifact_digest", "")))
    ):
        raise ValueError("source artifact identity is invalid")
    return {
        "artifact_bytes": artifact_bytes,
        "artifact_digest": value["artifact_digest"],
        "artifact_id": artifact_id,
        "artifact_name": expected_name,
    }


def validate_project_evidence(value: object) -> list[dict]:
    if not isinstance(value, list) or not 1 <= len(value) <= 2:
        raise ValueError("receipt project evidence count is invalid")
    result: list[dict] = []
    seen_projects: set[str] = set()
    seen_suffixes: set[str] = set()
    for item in value:
        if not isinstance(item, dict):
            raise ValueError("receipt project evidence entry must be an object")
        require_exact_keys(
            item,
            {
                "archive_bytes", "archive_file", "archive_sha256", "manifest_file",
                "manifest_sha256", "project", "project_suffix", "proof_filter",
                "selected_shard_count",
            },
            "receipt project evidence",
        )
        project = item.get("project")
        suffix = item.get("project_suffix")
        proof_filter = item.get("proof_filter")
        if (
            not isinstance(project, str)
            or not project.startswith("tests/dotnet/")
            or not project.endswith(".csproj")
            or "\\" in project
            or any(part in ("", ".", "..") for part in project.split("/"))
            or project in seen_projects
            or not isinstance(suffix, str)
            or not re.fullmatch(r"[a-z0-9-]+", suffix)
            or suffix in seen_suffixes
            or item.get("archive_file") != f"server-test-binaries-{suffix}.tar.gz"
            or item.get("manifest_file") != f"server-test-binaries-{suffix}.manifest.json"
            or not ARCHIVE_NAME.fullmatch(str(item.get("archive_file", "")))
            or not MANIFEST_NAME.fullmatch(str(item.get("manifest_file", "")))
            or not DIGEST.fullmatch(str(item.get("archive_sha256", "")))
            or not DIGEST.fullmatch(str(item.get("manifest_sha256", "")))
            or not isinstance(proof_filter, str)
            or not 1 <= len(proof_filter) <= 4096
            or any(ord(character) < 32 or ord(character) == 127 for character in proof_filter)
        ):
            raise ValueError("receipt project evidence identity is invalid")
        require_positive(item.get("archive_bytes"), "project archive bytes", MAX_ARCHIVE_BYTES)
        selected_count = require_positive(item.get("selected_shard_count"), "selected shard count")
        if selected_count < 2:
            raise ValueError("receipt project evidence is not repeated across shards")
        seen_projects.add(project)
        seen_suffixes.add(suffix)
        result.append(item)
    return result


def fingerprint(receipt: dict) -> str:
    return sha256(canonical({key: value for key, value in receipt.items() if key != "fingerprint"}))


def build_receipt(args: argparse.Namespace) -> dict:
    context = load_object(args.context, "PR Gate build context")
    plan = load_object(args.plan, "PR Gate build plan")
    expected_plan = load_object(args.expected_plan, "trusted expected build plan")
    artifact = load_object(args.source_artifact, "PR Gate source artifact metadata")
    require_exact_keys(
        context,
        {
            "base_sha", "configuration", "contract", "dotnet_sdk", "head_sha",
            "merge_sha", "merge_tree_sha", "pull_request", "repository", "run_attempt",
            "run_id", "runner_arch", "runner_image", "runner_os", "workflow_path",
        },
        "PR Gate build context",
    )
    if plan != expected_plan:
        raise ValueError("candidate and trusted repeated-project plans differ")
    producers = validate_plan(plan)
    if not producers:
        raise ValueError("PR Gate build evidence has no repeated project")

    source_sha = require_sha(context.get("merge_sha"), "producer merge SHA")
    source_tree = require_sha(context.get("merge_tree_sha"), "producer merge tree SHA")
    base_sha = require_sha(context.get("base_sha"), "producer base SHA")
    head_sha = require_sha(context.get("head_sha"), "producer head SHA")
    policy_sha = require_sha(args.policy_sha.lower(), "observer policy SHA")
    if (
        context.get("contract") != CONTEXT_SCHEMA
        or context.get("repository") != args.repository
        or context.get("pull_request") != args.pull_request
        or context.get("run_id") != args.source_run_id
        or context.get("run_attempt") != args.source_run_attempt
        or context.get("workflow_path") != PRODUCER_WORKFLOW
        or context.get("configuration") != args.configuration
        or context.get("dotnet_sdk") != args.dotnet_sdk
        or context.get("runner_os") != args.runner_os
        or context.get("runner_arch") != args.runner_arch
        or context.get("runner_image") != args.runner_image
        or base_sha != args.base_sha.lower()
        or head_sha != args.head_sha.lower()
    ):
        raise ValueError("PR Gate build context does not match the canonical source run")

    checked_tree = str(git(args.source_root, "rev-parse", "HEAD^{tree}"))
    if checked_tree != source_tree:
        raise ValueError("current pull-request merge tree differs from the producer tree")
    for ancestor, label in ((base_sha, "base"), (head_sha, "head")):
        completed = subprocess.run(
            ["git", "-C", str(args.source_root), "merge-base", "--is-ancestor", ancestor, "HEAD"],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        if completed.returncode != 0:
            raise ValueError(f"producer {label} is not an ancestor of the observed merge tree")

    policy_tree, inputs = policy_inputs(args.policy_root, policy_sha)
    assert_source_policy_matches(args.source_root, "HEAD", inputs)

    trusted_artifact = validate_artifact(
        artifact,
        run_id=args.source_run_id,
        run_attempt=args.source_run_attempt,
    )

    project_evidence = []
    created_values: list[int] = []
    expiry_values: list[int] = []
    for producer in producers:
        project = producer["project"]
        suffix = producer["project_suffix"]
        manifest_path = args.metadata_dir / "manifests" / f"server-test-binaries-{suffix}.manifest.json"
        manifest = validate_manifest(
            load_object(manifest_path, f"manifest for {project}"),
            expected_project=project,
            source_sha=source_sha,
            sdk=args.dotnet_sdk,
        )
        created_values.append(manifest["created_at_epoch"])
        expiry_values.append(manifest["expires_at_epoch"])
        project_evidence.append(
            {
                "archive_bytes": manifest["archive_bytes"],
                "archive_file": manifest["archive_file"],
                "archive_sha256": manifest["archive_sha256"],
                "manifest_file": manifest_path.name,
                "manifest_sha256": sha256(manifest_path.read_bytes()),
                "project": project,
                "project_suffix": suffix,
                "proof_filter": args.registry_by_project[project]["proof_filter"],
                "selected_shard_count": producer["selected_shard_count"],
            }
        )
    project_evidence = validate_project_evidence(project_evidence)

    created = max(created_values)
    expires = min(expiry_values)
    if not created <= args.now_epoch <= expires:
        raise ValueError("PR Gate build evidence is outside its validity window")
    receipt = {
        "artifact": trusted_artifact,
        "build": {
            "configuration": args.configuration,
            "dotnet_sdk": args.dotnet_sdk,
            "runner_arch": args.runner_arch,
            "runner_image": args.runner_image,
            "runner_os": args.runner_os,
        },
        "created_at_epoch": created,
        "expires_at_epoch": expires,
        "observer": {
            "run_attempt": args.observer_run_attempt,
            "run_id": args.observer_run_id,
            "workflow_path": OBSERVER_WORKFLOW,
        },
        "policy": {
            "inputs": inputs,
            "sha": policy_sha,
            "tree_sha": policy_tree,
        },
        "projects": project_evidence,
        "schema": SCHEMA,
        "source": {
            "base_sha": base_sha,
            "head_sha": head_sha,
            "merge_sha": source_sha,
            "merge_tree_sha": source_tree,
            "pull_request": args.pull_request,
            "repository": args.repository,
            "run_attempt": args.source_run_attempt,
            "run_id": args.source_run_id,
            "workflow_path": PRODUCER_WORKFLOW,
        },
    }
    receipt["fingerprint"] = fingerprint(receipt)
    return receipt


def validate_receipt_shape(receipt: dict) -> None:
    require_exact_keys(
        receipt,
        {
            "artifact", "build", "created_at_epoch", "expires_at_epoch", "fingerprint",
            "observer", "policy", "projects", "schema", "source",
        },
        "PR Gate build receipt",
    )
    if receipt.get("schema") != SCHEMA or receipt.get("fingerprint") != fingerprint(receipt):
        raise ValueError("PR Gate build receipt schema or fingerprint is invalid")


def validate_for_consumer(args: argparse.Namespace) -> dict:
    receipt = load_object(args.receipt, "PR Gate build receipt")
    validate_receipt_shape(receipt)
    source = receipt.get("source")
    build = receipt.get("build")
    artifact = receipt.get("artifact")
    policy = receipt.get("policy")
    observer = receipt.get("observer")
    if not all(isinstance(item, dict) for item in (source, build, artifact, policy, observer)):
        raise ValueError("PR Gate build receipt sections must be objects")
    require_exact_keys(
        source,
        {
            "base_sha", "head_sha", "merge_sha", "merge_tree_sha", "pull_request",
            "repository", "run_attempt", "run_id", "workflow_path",
        },
        "receipt source",
    )
    require_exact_keys(
        build,
        {"configuration", "dotnet_sdk", "runner_arch", "runner_image", "runner_os"},
        "receipt build",
    )
    require_exact_keys(
        artifact,
        {"artifact_bytes", "artifact_digest", "artifact_id", "artifact_name"},
        "receipt artifact",
    )
    require_exact_keys(policy, {"inputs", "sha", "tree_sha"}, "receipt policy")
    require_exact_keys(observer, {"run_attempt", "run_id", "workflow_path"}, "receipt observer")
    require_sha(source.get("base_sha"), "receipt source base SHA")
    source_head = require_sha(source.get("head_sha"), "receipt source head SHA")
    source_merge = require_sha(source.get("merge_sha"), "receipt source merge SHA")
    source_tree = require_sha(source.get("merge_tree_sha"), "receipt source merge tree SHA")
    source_run_id = require_positive(source.get("run_id"), "receipt source run id")
    source_run_attempt = require_positive(
        source.get("run_attempt"), "receipt source run attempt"
    )
    trusted_artifact = validate_artifact(
        artifact,
        run_id=source_run_id,
        run_attempt=source_run_attempt,
    )
    require_sha(policy.get("sha"), "receipt policy SHA")
    require_sha(policy.get("tree_sha"), "receipt policy tree SHA")
    require_positive(observer.get("run_id"), "receipt observer run id")
    require_positive(observer.get("run_attempt"), "receipt observer run attempt")
    if (
        source.get("repository") != args.repository
        or source.get("pull_request") != args.pull_request
        or source_head != args.head_sha.lower()
        or source_run_id != args.source_run_id
        or source_run_attempt != args.source_run_attempt
        or source.get("workflow_path") != PRODUCER_WORKFLOW
        or trusted_artifact["artifact_id"] != args.source_artifact_id
        or build
        != {
            "configuration": args.configuration,
            "dotnet_sdk": args.dotnet_sdk,
            "runner_arch": args.runner_arch,
            "runner_image": args.runner_image,
            "runner_os": args.runner_os,
        }
        or observer.get("workflow_path") != OBSERVER_WORKFLOW
    ):
        raise ValueError("PR Gate build receipt does not match the requested consumer identity")
    created = require_positive(receipt.get("created_at_epoch"), "receipt creation time")
    expires = require_positive(receipt.get("expires_at_epoch"), "receipt expiry time")
    if not created <= args.now_epoch <= expires or expires - created > MAX_TTL_SECONDS:
        raise ValueError("PR Gate build receipt is outside its validity window")

    consumer_sha = require_sha(args.consumer_sha.lower(), "consumer SHA")
    consumer_tree = str(git(args.consumer_root, "rev-parse", f"{consumer_sha}^{{tree}}"))
    if consumer_tree != source_tree:
        raise ValueError("consumer tree differs from the PR Gate producer tree")
    inputs = policy.get("inputs")
    if not isinstance(inputs, list):
        raise ValueError("receipt policy inputs are invalid")
    assert_source_policy_matches(args.consumer_root, consumer_sha, inputs)

    projects = validate_project_evidence(receipt.get("projects"))
    matches = [item for item in projects if item.get("project") == args.project]
    if len(matches) != 1:
        raise ValueError("receipt does not identify exactly one requested project")
    evidence = matches[0]
    manifest_path = args.payload_dir / str(evidence["manifest_file"])
    archive_path = args.payload_dir / str(evidence["archive_file"])
    if not manifest_path.is_file() or not archive_path.is_file():
        raise ValueError("downloaded payload is incomplete")
    if sha256(manifest_path.read_bytes()) != evidence.get("manifest_sha256"):
        raise ValueError("downloaded manifest digest does not match receipt")
    manifest = validate_manifest(
        load_object(manifest_path, "downloaded server-test manifest"),
        expected_project=args.project,
        source_sha=source_merge,
        sdk=args.dotnet_sdk,
    )
    if (
        manifest.get("archive_file") != evidence.get("archive_file")
        or manifest.get("archive_sha256") != evidence.get("archive_sha256")
        or manifest.get("archive_bytes") != evidence.get("archive_bytes")
        or archive_path.stat().st_size != evidence.get("archive_bytes")
        or sha256_file(archive_path) != evidence.get("archive_sha256")
    ):
        raise ValueError("downloaded archive does not match trusted project evidence")
    return receipt


def load_registry(path: Path) -> dict[str, dict]:
    registry = load_object(path, "server-test artifact registry")
    projects = registry.get("projects")
    if registry.get("contract_version") != 1 or not isinstance(projects, list):
        raise ValueError("server-test artifact registry contract is invalid")
    result: dict[str, dict] = {}
    for item in projects:
        if not isinstance(item, dict) or set(item) != {"artifact_suffix", "csproj", "proof_filter"}:
            raise ValueError("server-test artifact registry entry is invalid")
        project = item.get("csproj")
        if not isinstance(project, str) or project in result:
            raise ValueError("server-test artifact registry project is invalid or duplicated")
        result[project] = item
    return result


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "validate"))
    parser.add_argument("--repository", required=True)
    parser.add_argument("--pull-request", type=positive_int, required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--source-run-id", type=positive_int, required=True)
    parser.add_argument("--source-run-attempt", type=positive_int, required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--dotnet-sdk", required=True)
    parser.add_argument("--runner-os", required=True)
    parser.add_argument("--runner-arch", required=True)
    parser.add_argument("--runner-image", required=True)
    parser.add_argument("--now-epoch", type=positive_int)
    parser.add_argument("--receipt", type=Path, required=True)

    parser.add_argument("--policy-root", type=Path)
    parser.add_argument("--policy-sha")
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--base-sha")
    parser.add_argument("--context", type=Path)
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--expected-plan", type=Path)
    parser.add_argument("--source-artifact", type=Path)
    parser.add_argument("--metadata-dir", type=Path)
    parser.add_argument("--registry", type=Path)
    parser.add_argument("--observer-run-id", type=positive_int)
    parser.add_argument("--observer-run-attempt", type=positive_int)

    parser.add_argument("--consumer-root", type=Path)
    parser.add_argument("--consumer-sha")
    parser.add_argument("--source-artifact-id", type=positive_int)
    parser.add_argument("--payload-dir", type=Path)
    parser.add_argument("--project")
    args = parser.parse_args()
    args.now_epoch = args.now_epoch or int(time.time())

    if args.mode == "build":
        required = (
            "policy_root", "policy_sha", "source_root", "base_sha", "context", "plan",
            "expected_plan", "source_artifact", "metadata_dir", "registry",
            "observer_run_id", "observer_run_attempt",
        )
        missing = [name for name in required if getattr(args, name) in (None, "")]
        if missing:
            parser.error(f"build mode is missing: {', '.join(missing)}")
        args.registry_by_project = load_registry(args.registry)
        receipt = build_receipt(args)
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_bytes(canonical(receipt) + b"\n")
        print(
            f"pr-gate-build-evidence=created fingerprint={receipt['fingerprint']} "
            f"projects={len(receipt['projects'])}"
        )
    else:
        required = ("consumer_root", "consumer_sha", "source_artifact_id", "payload_dir", "project")
        missing = [name for name in required if getattr(args, name) in (None, "")]
        if missing:
            parser.error(f"validate mode is missing: {', '.join(missing)}")
        receipt = validate_for_consumer(args)
        print(
            f"pr-gate-build-evidence=accepted fingerprint={receipt['fingerprint']} "
            f"project={args.project}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
