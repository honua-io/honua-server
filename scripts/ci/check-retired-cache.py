#!/usr/bin/env python3
"""Reject added Redis mentions outside historical notes and the policy itself."""

import argparse
from pathlib import PurePosixPath
import re
import subprocess

POLICY_FILES = {
    # The architecture guard must name the banned packages it detects.
    "tests/dotnet/Honua.Architecture.SourceTests/RetiredCacheDependencyTests.cs",
    "scripts/ci/check-retired-cache.py",
    "scripts/ci/check-retired-cache.test.py",
    "scripts/ci/check-retired-cache-workflow.test.py",
}

# CI routing data mirrors the test inventory: a shard filter, capacity contract or container
# expectation names a cache test only while that test exists, and the test itself is checked.
ROUTING_DATA_FILES = {
    ".github/ci-shards.json",
    "scripts/ci/fixtures/full-run-capacity-contracts.json",
    "scripts/ci/fixtures/live-output-capacity-contracts.json",
    "scripts/ci/test-shard-image-selection.py",
    # Analyzer suppressions and retired-term allowances are keyed by test file path.
    "scripts/ci/analyzer-baseline/AnalyzerBaseline.tsv",
    "scripts/ci/retired-terms-allowlist.v1.json",
}

# Recorded CI runs list the files those runs changed; history is not rewritten.
RECORDED_RUN_ROOTS = ("eng/ci/fixtures/shard-promotion/",)

# The Lambda preview is outside the ECS-on-Fargate scope of the cache retirement (#7354);
# its certification harness and event endpoint keep their wording until Lambda is retired.
LAMBDA_PREVIEW_FILES = {
    "scripts/cloud/lambda-certification.md",
    "scripts/cloud/lambda-certification.py",
    "scripts/cloud/test-certify-lambda-preview.py",
    "src/Honua.Server/Features/ControlPlane/ControlPlaneLambdaEventEndpoint.cs",
}

# Diagnostic redaction must keep recognising any connection URL a user may paste.
REDACTION_FILES = {"src/Honua.Geoprocessing/Core/Features/Geoprocessing/Diagnostics/GpDiagnosticRedactor.cs"}


# NuGet lock files record the resolved closure of references that the checked project
# files and Directory.Packages.props already declare, directly or transitively, so a cache
# package appears there only while a checked file still pulls it in.
GENERATED_DEPENDENCY_RECORDS = {"packages.lock.json"}


def exempt(path: str) -> bool:
    name = PurePosixPath(path)
    return (path in POLICY_FILES
            or path in ROUTING_DATA_FILES
            or path in LAMBDA_PREVIEW_FILES
            or path in REDACTION_FILES
            or path.startswith(RECORDED_RUN_ROOTS)
            or name.name in GENERATED_DEPENDENCY_RECORDS
            or name.name.casefold() in {"changelog", "changelog.md", "changelog.rst", "changelog.txt",
                                        "third-party-notices.md", "third-party-notices.txt"}
            or path.casefold().startswith("docs/changelog/"))


def git_diff(revisions, options):
    return subprocess.check_output([
        "git", "--literal-pathspecs", "-c", "core.quotePath=false", "diff", "--no-ext-diff", "--no-textconv",
        "--no-renames", *options, *revisions,
    ])


def mentions_retired_cache(line: str) -> bool:
    # Git's text patch preserves NUL padding in UTF-16/32 files. Redis tokens
    # use ASCII letters, so remove padding before identifying those tokens.
    line = line.replace("\x00", "")
    for identifier in re.findall(r"[A-Za-z]+", line):
        if identifier.casefold() == "redis":
            return True
        # Recognize PascalCase, camelCase and acronym boundaries in API names.
        words = re.sub(r"([A-Z])([A-Z][a-z])", r"\1 \2", identifier)
        words = re.sub(r"([a-z])([A-Z])", r"\1 \2", words)
        if "redis" in (word.casefold() for word in words.split()):
            return True
    return False


def violations(revisions, path):
    patch = git_diff(revisions, ["--text", "--unified=0", "--no-color", "--src-prefix=a/", "--dst-prefix=b/"])
    line_number = 0
    for line in patch.decode("utf-8", errors="replace").split("\n"):
        hunk = re.match(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", line)
        if hunk:
            line_number = int(hunk.group(1))
        elif line_number and line.startswith("+"):
            if mentions_retired_cache(line):
                yield f"{path}:{line_number}: remove retired cache dependency or mention"
            line_number += 1
        elif line.startswith(" "):
            line_number += 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args()
    common_base = subprocess.check_output(
        ["git", "merge-base", args.base, args.head], text=True,
    ).strip()
    revisions = [common_base, args.head, "--"]
    paths = git_diff(revisions, ["--name-only", "-z"]).decode("utf-8").split("\0")
    failures = [failure for path in paths if path and not exempt(path)
                for failure in violations([*revisions, path], path)]
    for failure in failures:
        print(failure)
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
