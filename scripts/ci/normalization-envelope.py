#!/usr/bin/env python3
"""Build and validate the data-only derived-artifact normalization envelope."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import re
import stat
import sys
import zipfile
from pathlib import Path
from typing import Any

SCHEMA = "honua.normalization-envelope/v2"
WORKFLOW_PATH = ".github/workflows/normalize-derived-artifacts.yml"
ARCHIVE_MEMBER = "normalization-envelope.json"
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")

OUTPUT_LIMITS = {
    "docs/gis/data/feature-catalog.json": 4 * 1024 * 1024,
    "docs/gis/data/admin-openapi-operation-ids.json": 128 * 1024,
    "docs/gis/data/admin-mcp-projection-manifest.json": 64 * 1024,
    "docs/gis/data/geoservices-rest-parity.json": 1024 * 1024,
    "docs/gis/data/capability-matrix.v1.json": 1024 * 1024,
}
MAX_TOTAL_OUTPUT_BYTES = 6 * 1024 * 1024
MAX_ENVELOPE_BYTES = 9 * 1024 * 1024
MAX_ARCHIVE_BYTES = 10 * 1024 * 1024

GENERATOR_INPUTS = (
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.config",
    "scripts/generate-feature-catalog.sh",
    "scripts/generate-geoservices-parity.sh",
    "scripts/ci/capability-impact.py",
    "scripts/ci/generate-capability-matrix.py",
    "tests/dotnet/Honua.Architecture.Tests/ArchitectureTestHelpers.cs",
    "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj",
    "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/CapabilityRouteMapper.cs",
    "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogEmitter.cs",
    "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs",
    "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/ProofLedgerProjection.cs",
    "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityEmitter.cs",
    "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityGenerator.cs",
    "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesParityModels.cs",
    "tests/dotnet/Honua.Architecture.Tests/GeoServicesParity/GeoServicesRouteRoster.cs",
    "docs/gis/data/capability-route-mapping.v1.json",
    "docs/gis/data/geoservices-parity-judgment.json",
    "docs/gis/data/public-interface-proof.json",
)


class EnvelopeError(ValueError):
    """A normalization artifact violated the trusted data contract."""


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def reject_constant(value: str) -> None:
    raise EnvelopeError(f"non-finite JSON constant is forbidden: {value}")


def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise EnvelopeError(f"duplicate JSON key is forbidden: {key}")
        result[key] = value
    return result


def strict_json_bytes(value: bytes, label: str) -> Any:
    try:
        text = value.decode("utf-8", errors="strict")
    except UnicodeDecodeError as error:
        raise EnvelopeError(f"{label} is not valid UTF-8") from error
    try:
        return json.loads(
            text,
            object_pairs_hook=reject_duplicate_keys,
            parse_constant=reject_constant,
        )
    except json.JSONDecodeError as error:
        raise EnvelopeError(f"{label} is not valid JSON: {error}") from error


def exact_keys(value: Any, expected: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise EnvelopeError(f"{label} must be an object")
    actual = set(value)
    if actual != expected:
        raise EnvelopeError(
            f"{label} keys must be {sorted(expected)}; got {sorted(actual)}"
        )
    return value


def require_commit(value: Any, label: str) -> str:
    if not isinstance(value, str) or not COMMIT_PATTERN.fullmatch(value):
        raise EnvelopeError(f"{label} must be a lowercase full commit SHA")
    return value


def require_positive_int(value: Any, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise EnvelopeError(f"{label} must be a positive integer")
    return value


def read_regular_bytes(path: Path, label: str) -> bytes:
    try:
        mode = path.lstat().st_mode
    except OSError as error:
        raise EnvelopeError(f"{label} is unavailable") from error
    if not stat.S_ISREG(mode):
        raise EnvelopeError(f"{label} must be one regular file")
    return path.read_bytes()


def build_envelope(
    repo_root: Path,
    output_root: Path,
    repository: str,
    pull_request: int,
    source_sha: str,
    source_tree_sha: str,
    base_sha: str,
    run_id: int,
    run_attempt: int,
) -> dict[str, Any]:
    require_commit(source_sha, "source_sha")
    require_commit(source_tree_sha, "source_tree_sha")
    require_commit(base_sha, "base_sha")
    require_positive_int(pull_request, "pull_request")
    require_positive_int(run_id, "run_id")
    require_positive_int(run_attempt, "run_attempt")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise EnvelopeError("repository must be owner/name")

    outputs: list[dict[str, Any]] = []
    total = 0
    for relative_path, limit in OUTPUT_LIMITS.items():
        content = read_regular_bytes(output_root / relative_path, relative_path)
        parsed = strict_json_bytes(content, relative_path)
        if not isinstance(parsed, dict):
            raise EnvelopeError(f"{relative_path} must contain a JSON object")
        if len(content) > limit:
            raise EnvelopeError(f"{relative_path} exceeds {limit} bytes")
        total += len(content)
        outputs.append(
            {
                "content_base64": base64.b64encode(content).decode("ascii"),
                "length": len(content),
                "path": relative_path,
                "sha256": sha256_bytes(content),
            }
        )
    if total > MAX_TOTAL_OUTPUT_BYTES:
        raise EnvelopeError(f"normalization outputs exceed {MAX_TOTAL_OUTPUT_BYTES} bytes")

    generators = []
    for relative_path in GENERATOR_INPUTS:
        content = read_regular_bytes(repo_root / relative_path, relative_path)
        generators.append({"path": relative_path, "sha256": sha256_bytes(content)})

    return {
        "generators": generators,
        "outputs": outputs,
        "producer": {
            "event": "pull_request",
            "run_attempt": run_attempt,
            "run_id": run_id,
            "workflow_path": WORKFLOW_PATH,
        },
        "schema": SCHEMA,
        "source": {
            "base_sha": base_sha,
            "pull_request": pull_request,
            "repository": repository,
            "sha": source_sha,
            "tree_sha": source_tree_sha,
        },
    }


def validate_envelope(
    raw: bytes,
    *,
    repository: str,
    pull_request: int,
    source_sha: str,
    source_tree_sha: str,
    base_sha: str | None,
    run_id: int,
    run_attempt: int,
) -> dict[str, Any]:
    if len(raw) > MAX_ENVELOPE_BYTES:
        raise EnvelopeError(f"envelope exceeds {MAX_ENVELOPE_BYTES} bytes")
    envelope = exact_keys(
        strict_json_bytes(raw, "normalization envelope"),
        {"generators", "outputs", "producer", "schema", "source"},
        "normalization envelope",
    )
    if envelope["schema"] != SCHEMA:
        raise EnvelopeError(f"schema must be {SCHEMA}")

    source = exact_keys(
        envelope["source"],
        {"base_sha", "pull_request", "repository", "sha", "tree_sha"},
        "source",
    )
    expected_source = {
        "pull_request": pull_request,
        "repository": repository,
        "sha": source_sha,
        "tree_sha": source_tree_sha,
    }
    if {key: source[key] for key in expected_source} != expected_source or (
        base_sha is not None and source["base_sha"] != base_sha
    ):
        raise EnvelopeError("source identity does not match the workflow event")
    require_commit(source["sha"], "source.sha")
    require_commit(source["tree_sha"], "source.tree_sha")
    require_commit(source["base_sha"], "source.base_sha")

    producer = exact_keys(
        envelope["producer"],
        {"event", "run_attempt", "run_id", "workflow_path"},
        "producer",
    )
    expected_producer = {
        "event": "pull_request",
        "run_attempt": run_attempt,
        "run_id": run_id,
        "workflow_path": WORKFLOW_PATH,
    }
    if producer != expected_producer:
        raise EnvelopeError("producer identity does not match the workflow run")

    generators = envelope["generators"]
    if not isinstance(generators, list) or len(generators) != len(GENERATOR_INPUTS):
        raise EnvelopeError("generator evidence count is invalid")
    generator_paths: list[str] = []
    for index, generator_value in enumerate(generators):
        generator = exact_keys(generator_value, {"path", "sha256"}, f"generators[{index}]")
        if not isinstance(generator["path"], str):
            raise EnvelopeError("generator path must be a string")
        if not isinstance(generator["sha256"], str) or not SHA256_PATTERN.fullmatch(generator["sha256"]):
            raise EnvelopeError("generator digest must be lowercase SHA-256")
        generator_paths.append(generator["path"])
    if generator_paths != list(GENERATOR_INPUTS):
        raise EnvelopeError("generator path allowlist/order mismatch")

    outputs = envelope["outputs"]
    if not isinstance(outputs, list) or len(outputs) != len(OUTPUT_LIMITS):
        raise EnvelopeError("output count is invalid")
    decoded_outputs: list[dict[str, Any]] = []
    total = 0
    seen_paths: set[str] = set()
    for index, output_value in enumerate(outputs):
        output = exact_keys(
            output_value,
            {"content_base64", "length", "path", "sha256"},
            f"outputs[{index}]",
        )
        path = output["path"]
        if not isinstance(path, str) or path not in OUTPUT_LIMITS:
            raise EnvelopeError(f"output path is not allowlisted: {path!r}")
        if path in seen_paths:
            raise EnvelopeError(f"duplicate output path: {path}")
        seen_paths.add(path)
        length = require_positive_int(output["length"], f"{path}.length")
        if length > OUTPUT_LIMITS[path]:
            raise EnvelopeError(f"{path} exceeds {OUTPUT_LIMITS[path]} bytes")
        digest = output["sha256"]
        if not isinstance(digest, str) or not SHA256_PATTERN.fullmatch(digest):
            raise EnvelopeError(f"{path}.sha256 must be lowercase SHA-256")
        encoded = output["content_base64"]
        if not isinstance(encoded, str):
            raise EnvelopeError(f"{path}.content_base64 must be a string")
        try:
            content = base64.b64decode(encoded, validate=True)
        except (binascii.Error, ValueError) as error:
            raise EnvelopeError(f"{path} content is not canonical base64") from error
        if base64.b64encode(content).decode("ascii") != encoded:
            raise EnvelopeError(f"{path} content is not canonical base64")
        if len(content) != length or sha256_bytes(content) != digest:
            raise EnvelopeError(f"{path} length/digest mismatch")
        parsed = strict_json_bytes(content, path)
        if not isinstance(parsed, dict):
            raise EnvelopeError(f"{path} must contain a JSON object")
        total += length
        decoded_outputs.append(
            {"content_base64": encoded, "length": length, "path": path, "sha256": digest}
        )
    if seen_paths != set(OUTPUT_LIMITS):
        raise EnvelopeError("output allowlist is incomplete")
    if total > MAX_TOTAL_OUTPUT_BYTES:
        raise EnvelopeError(f"normalization outputs exceed {MAX_TOTAL_OUTPUT_BYTES} bytes")

    return {
        "schema": SCHEMA,
        "source": source,
        "producer": producer,
        "generators": generators,
        "outputs": decoded_outputs,
        "total_output_bytes": total,
    }


def validate_archive(path: Path, **expected: Any) -> dict[str, Any]:
    if path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise EnvelopeError(f"artifact archive exceeds {MAX_ARCHIVE_BYTES} bytes")
    try:
        archive = zipfile.ZipFile(path)
    except (OSError, zipfile.BadZipFile) as error:
        raise EnvelopeError("artifact is not a valid zip archive") from error
    with archive:
        members = archive.infolist()
        if len(members) != 1 or members[0].filename != ARCHIVE_MEMBER:
            raise EnvelopeError(f"archive must contain only {ARCHIVE_MEMBER}")
        member = members[0]
        mode = (member.external_attr >> 16) & 0xFFFF
        file_type = stat.S_IFMT(mode)
        if member.is_dir() or file_type == stat.S_IFLNK or file_type not in (0, stat.S_IFREG):
            raise EnvelopeError("archive member must be one regular file")
        if member.flag_bits & 0x1:
            raise EnvelopeError("encrypted archive members are forbidden")
        if member.file_size > MAX_ENVELOPE_BYTES:
            raise EnvelopeError(f"envelope exceeds {MAX_ENVELOPE_BYTES} bytes")
        raw = archive.read(member)
    return validate_envelope(raw, **expected)


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    subparsers = result.add_subparsers(dest="command", required=True)

    build = subparsers.add_parser("build")
    build.add_argument("--repo-root", type=Path, default=Path.cwd())
    build.add_argument("--output-root", type=Path, default=Path.cwd())
    build.add_argument("--repository", required=True)
    build.add_argument("--pull-request", type=int, required=True)
    build.add_argument("--source-sha", required=True)
    build.add_argument("--source-tree-sha", required=True)
    build.add_argument("--base-sha", required=True)
    build.add_argument("--run-id", type=int, required=True)
    build.add_argument("--run-attempt", type=int, required=True)
    build.add_argument("--output", type=Path, required=True)

    validate = subparsers.add_parser("validate-archive")
    validate.add_argument("--archive", type=Path, required=True)
    validate.add_argument("--repository", required=True)
    validate.add_argument("--pull-request", type=int, required=True)
    validate.add_argument("--source-sha", required=True)
    validate.add_argument("--source-tree-sha", required=True)
    validate.add_argument("--base-sha")
    validate.add_argument("--run-id", type=int, required=True)
    validate.add_argument("--run-attempt", type=int, required=True)
    validate.add_argument("--plan", type=Path, required=True)
    return result


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "build":
            envelope = build_envelope(
                args.repo_root.resolve(),
                args.output_root.resolve(),
                args.repository,
                args.pull_request,
                args.source_sha,
                args.source_tree_sha,
                args.base_sha,
                args.run_id,
                args.run_attempt,
            )
            text = json.dumps(envelope, indent=2, sort_keys=True) + "\n"
            if len(text.encode("utf-8")) > MAX_ENVELOPE_BYTES:
                raise EnvelopeError(f"envelope exceeds {MAX_ENVELOPE_BYTES} bytes")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(text, encoding="utf-8", newline="\n")
        else:
            plan = validate_archive(
                args.archive,
                repository=args.repository,
                pull_request=args.pull_request,
                source_sha=args.source_sha,
                source_tree_sha=args.source_tree_sha,
                base_sha=args.base_sha,
                run_id=args.run_id,
                run_attempt=args.run_attempt,
            )
            args.plan.parent.mkdir(parents=True, exist_ok=True)
            args.plan.write_text(json.dumps(plan, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except (EnvelopeError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
