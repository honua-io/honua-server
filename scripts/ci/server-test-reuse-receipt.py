#!/usr/bin/env python3
"""Build/validate an exact-head, content-addressed server-test reuse receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import time
from pathlib import Path

SCHEMA = "honua.server-test-reuse-receipt/v1"
MAX_RECEIPT_BYTES = 64 * 1024
MAX_TTL_SECONDS = 24 * 60 * 60
INPUT_PATHS = (
    ".github/server-test-artifact-projects.json",
    ".github/workflows/server-test-reuse-benchmark.yml",
    "Directory.Build.props",
    "Directory.Packages.props",
    "NuGet.config",
    "global.json",
    "scripts/ci/package-server-test-binaries.sh",
    "scripts/ci/restore-server-test-binaries.sh",
    "scripts/ci/server-test-reuse-receipt.py",
)
OPTIONAL_INPUT_PATHS = {"global.json"}


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


def load_json_bytes(data: bytes, label: str) -> dict:
    def reject_duplicate(pairs: list[tuple[str, object]]) -> dict:
        result: dict = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"{label} contains duplicate key {key!r}")
            result[key] = value
        return result

    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=reject_duplicate)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"{label} is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object")
    return value


def git_prefix(repo: Path) -> list[str]:
    dot_git = repo / ".git"
    if dot_git.is_file():
        marker = dot_git.read_text(encoding="utf-8").strip()
        if not marker.startswith("gitdir: "):
            raise ValueError("worktree .git pointer is invalid")
        raw = marker.removeprefix("gitdir: ")
        git_dir = Path(raw)
        if not git_dir.is_absolute():
            drive_path = re.fullmatch(r"([A-Za-z]):[/\\](.*)", raw)
            if drive_path and os.name != "nt":
                git_dir = Path("/mnt") / drive_path.group(1).lower() / drive_path.group(2).replace("\\", "/")
            else:
                git_dir = repo / git_dir
        return ["git", "--git-dir", str(git_dir), "--work-tree", str(repo)]
    return ["git", "-C", str(repo)]


def git(repo: Path, *args: str, binary: bool = False) -> bytes | str:
    completed = subprocess.run(
        [*git_prefix(repo), *args], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    return completed.stdout if binary else completed.stdout.decode("utf-8").strip()


def source_inputs(repo: Path, source_sha: str, project: str) -> tuple[str, list[dict]]:
    tree_sha = str(git(repo, "rev-parse", f"{source_sha}^{{tree}}"))
    paths = sorted(set((*INPUT_PATHS, project)))
    inputs: list[dict] = []
    for path in paths:
        try:
            data = git(repo, "cat-file", "blob", f"{source_sha}:{path}", binary=True)
        except subprocess.CalledProcessError as exc:
            if path in OPTIONAL_INPUT_PATHS:
                continue
            raise ValueError(f"required fingerprint input is absent from {source_sha}: {path}") from exc
        assert isinstance(data, bytes)
        inputs.append({"path": path, "sha256": sha256(data)})
    return tree_sha, inputs


def fingerprint_payload(
    *, source_sha: str, tree_sha: str, project: str, configuration: str,
    dotnet_sdk: str, runner_os: str, runner_arch: str, inputs: list[dict]
) -> dict:
    return {
        "configuration": configuration,
        "dotnet_sdk": dotnet_sdk,
        "inputs": inputs,
        "project": project,
        "runner_arch": runner_arch,
        "runner_os": runner_os,
        "source_sha": source_sha,
        "source_tree_sha": tree_sha,
    }


def build_receipt(
    *, repo: Path, source_sha: str, project: str, configuration: str, dotnet_sdk: str,
    runner_os: str, runner_arch: str, run_id: int, run_attempt: int,
    manifest_path: Path, archive_path: Path, now_epoch: int
) -> dict:
    if len(source_sha) != 40 or any(char not in "0123456789abcdef" for char in source_sha):
        raise ValueError("source SHA must be lowercase full hexadecimal")
    manifest_bytes = manifest_path.read_bytes()
    manifest = load_json_bytes(manifest_bytes, "artifact manifest")
    archive_bytes = archive_path.stat().st_size
    archive_sha256 = sha256_file(archive_path)
    if manifest.get("source_sha") != source_sha or manifest.get("project") != project:
        raise ValueError("artifact manifest source/project does not match receipt")
    if manifest.get("dotnet_sdk") != dotnet_sdk:
        raise ValueError("artifact manifest SDK does not match receipt")
    if manifest.get("archive_file") != archive_path.name:
        raise ValueError("artifact manifest archive name does not match")
    if manifest.get("archive_sha256") != archive_sha256 or manifest.get("archive_bytes") != archive_bytes:
        raise ValueError("artifact archive does not match its manifest")
    tree_sha, inputs = source_inputs(repo, source_sha, project)
    payload = fingerprint_payload(
        source_sha=source_sha, tree_sha=tree_sha, project=project, configuration=configuration,
        dotnet_sdk=dotnet_sdk, runner_os=runner_os, runner_arch=runner_arch, inputs=inputs,
    )
    return {
        "artifact": {
            "archive_bytes": archive_bytes,
            "archive_file": archive_path.name,
            "archive_sha256": archive_sha256,
            "manifest_file": manifest_path.name,
            "manifest_sha256": sha256(manifest_bytes),
        },
        "created_at_epoch": now_epoch,
        "expires_at_epoch": now_epoch + MAX_TTL_SECONDS,
        "fingerprint": sha256(canonical(payload)),
        "inputs": inputs,
        "producer": {"run_attempt": run_attempt, "run_id": run_id},
        "schema": SCHEMA,
        "source": {
            "configuration": configuration,
            "dotnet_sdk": dotnet_sdk,
            "project": project,
            "runner_arch": runner_arch,
            "runner_os": runner_os,
            "sha": source_sha,
            "tree_sha": tree_sha,
        },
    }


def validate_receipt(
    receipt_bytes: bytes, *, repo: Path, source_sha: str, project: str, configuration: str,
    dotnet_sdk: str, runner_os: str, runner_arch: str, run_id: int, run_attempt: int,
    manifest_path: Path, archive_path: Path, now_epoch: int
) -> dict:
    if len(receipt_bytes) > MAX_RECEIPT_BYTES:
        raise ValueError("reuse receipt exceeds its size bound")
    receipt = load_json_bytes(receipt_bytes, "reuse receipt")
    required = {"artifact", "created_at_epoch", "expires_at_epoch", "fingerprint", "inputs", "producer", "schema", "source"}
    if set(receipt) != required or receipt.get("schema") != SCHEMA:
        raise ValueError("reuse receipt schema/keys are invalid")
    source = receipt.get("source")
    producer = receipt.get("producer")
    artifact = receipt.get("artifact")
    if not all(isinstance(item, dict) for item in (source, producer, artifact)):
        raise ValueError("reuse receipt sections must be objects")
    if set(source) != {"configuration", "dotnet_sdk", "project", "runner_arch", "runner_os", "sha", "tree_sha"}:
        raise ValueError("reuse receipt source keys are invalid")
    if set(producer) != {"run_attempt", "run_id"} or set(artifact) != {
        "archive_bytes", "archive_file", "archive_sha256", "manifest_file", "manifest_sha256"
    }:
        raise ValueError("reuse receipt producer/artifact keys are invalid")
    expected_source = {
        "configuration": configuration,
        "dotnet_sdk": dotnet_sdk,
        "project": project,
        "runner_arch": runner_arch,
        "runner_os": runner_os,
        "sha": source_sha,
        "tree_sha": str(git(repo, "rev-parse", f"{source_sha}^{{tree}}")),
    }
    producer_attempt = producer.get("run_attempt")
    if (
        source != expected_source
        or producer.get("run_id") != run_id
        or not isinstance(producer_attempt, int)
        or not 1 <= producer_attempt <= run_attempt
    ):
        raise ValueError("reuse receipt source/run binding does not match")
    created = receipt.get("created_at_epoch")
    expires = receipt.get("expires_at_epoch")
    if not isinstance(created, int) or not isinstance(expires, int) or not (
        created <= now_epoch <= expires and 0 < expires - created <= MAX_TTL_SECONDS
    ):
        raise ValueError("reuse receipt is outside its validity window")
    manifest_bytes = manifest_path.read_bytes()
    archive_bytes = archive_path.stat().st_size
    archive_sha256 = sha256_file(archive_path)
    expected_artifact = {
        "archive_bytes": archive_bytes,
        "archive_file": archive_path.name,
        "archive_sha256": archive_sha256,
        "manifest_file": manifest_path.name,
        "manifest_sha256": sha256(manifest_bytes),
    }
    if artifact != expected_artifact:
        raise ValueError("reuse receipt artifact digest/size does not match")
    manifest = load_json_bytes(manifest_bytes, "artifact manifest")
    if manifest.get("source_sha") != source_sha or manifest.get("project") != project or manifest.get("dotnet_sdk") != dotnet_sdk:
        raise ValueError("inner artifact manifest binding does not match")
    if manifest.get("archive_sha256") != expected_artifact["archive_sha256"] or manifest.get("archive_bytes") != archive_bytes:
        raise ValueError("inner artifact manifest digest/size does not match")
    tree_sha, inputs = source_inputs(repo, source_sha, project)
    if receipt.get("inputs") != inputs:
        raise ValueError("reuse receipt input digests do not match exact source")
    payload = fingerprint_payload(
        source_sha=source_sha, tree_sha=tree_sha, project=project, configuration=configuration,
        dotnet_sdk=dotnet_sdk, runner_os=runner_os, runner_arch=runner_arch, inputs=inputs,
    )
    if receipt.get("fingerprint") != sha256(canonical(payload)):
        raise ValueError("reuse receipt fingerprint is invalid")
    return receipt


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "validate"))
    parser.add_argument("--repo-root", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--dotnet-sdk", required=True)
    parser.add_argument("--runner-os", required=True)
    parser.add_argument("--runner-arch", required=True)
    parser.add_argument("--run-id", type=positive_int, required=True)
    parser.add_argument("--run-attempt", type=positive_int, default=1)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--now-epoch", type=positive_int, default=None)
    args = parser.parse_args()
    now = args.now_epoch or int(time.time())
    common = dict(
        repo=args.repo_root, source_sha=args.source_sha.lower(), project=args.project,
        configuration=args.configuration, dotnet_sdk=args.dotnet_sdk,
        runner_os=args.runner_os, runner_arch=args.runner_arch, run_id=args.run_id,
        manifest_path=args.manifest, archive_path=args.archive, now_epoch=now,
    )
    if args.mode == "build":
        receipt = build_receipt(run_attempt=args.run_attempt, **common)
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_bytes(canonical(receipt) + b"\n")
        print(f"reuse-receipt={receipt['fingerprint']} inputs={len(receipt['inputs'])}")
    else:
        receipt = validate_receipt(args.receipt.read_bytes(), run_attempt=args.run_attempt, **common)
        print(f"reuse-receipt=accepted fingerprint={receipt['fingerprint']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
