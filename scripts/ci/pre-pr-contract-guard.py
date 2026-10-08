#!/usr/bin/env python3
"""Fail closed when FAST would defer a changed catalogue/proof input.

Only an accounted-for C# implementation/body edit can defer assembly discovery.
The complete diff includes committed, index, worktree and untracked paths. Route
registration is checked in both file versions, so deleting a route cannot evade
this guard. Unknown paths/syntax retain full architecture enforcement.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re
import subprocess

CONTRACT = re.compile(
    r"Endpoint|InterfaceOperation|OperationRegistry|Capability|ProofLedger|"
    r"Map(?:Get|Post|Put|Patch|Delete|Methods|Group|GrpcService)|"
    r"Add(?:Grpc|OData)|WithOpenApi|WithName|RequireAuthorization",
    re.IGNORECASE,
)
TEST_DISCOVERY = re.compile(
    r"\[(?:[^\]\n]*\b(?:Endpoint|Operation|Protocol|InterfaceOperation|"
    r"IntegrationTest|IntegrationTheory|Fact|Theory))\b|"
    r"\b(?:class|namespace)\s|\b(?:public|protected)\s.*\(",
)
SAFE_SOURCE_ROOTS = (
    "src/Honua.Geometry/", "src/Honua.Core/", "src/Honua.Core.Abstractions/",
    "src/Honua.Db/", "src/Honua.Hosting/", "src/Honua.Io/",
)


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], text=True, stderr=subprocess.DEVNULL)


def reason(base: str, paths: list[str]) -> str:
    try:
        merge_base = git("merge-base", base, "HEAD").strip()
        for path in paths:
            if not path:
                continue
            # These inputs govern selection or the catalogue/proof assertions.
            if path.startswith(("tests/dotnet/Honua.Architecture.Tests/",
                                "tests/dotnet/Honua.TestKit/", "docs/gis/",
                                ".github/", "scripts/ci/pre-pr-",
                                "scripts/ci/compute-affected-", "scripts/ci/classify-pre-pr-",
                                "scripts/ci/capability-impact.py", "scripts/ci/honua-server-targeted-tests.sh")):
                return f"governance/selector input: {path}"
            if path.startswith("scripts/ci/"):
                continue
            if path.startswith("docs/"):
                if re.search(r"proof|parity|capabilit|api-surface|operation", path, re.I):
                    return f"published contract: {path}"
                continue
            if not path.endswith(".cs"):
                return f"unclassified build/contract input: {path}"
            try:
                old = git("show", f"{merge_base}:{path}")
            except subprocess.CalledProcessError:
                return f"new or untracked C# input: {path}"
            current = Path(path).read_text() if Path(path).is_file() else ""
            if not current:
                return f"removed C# input: {path}"
            diffs = [git("diff", "--unified=0", f"{base}...HEAD", "--", path),
                     git("diff", "--unified=0", "--", path),
                     git("diff", "--cached", "--unified=0", "--", path)]
            changed = "\n".join(line[1:] for diff in diffs for line in diff.splitlines()
                                if line.startswith(("+", "-"))
                                and not line.startswith(("+++", "---")))
            snapshots = [old, current, git("show", f"HEAD:{path}"), git("show", f":{path}")]
            if path.startswith("src/"):
                if (not path.startswith(SAFE_SOURCE_ROOTS) or CONTRACT.search("\n".join(snapshots)) or CONTRACT.search(changed)
                        or TEST_DISCOVERY.search(changed)):
                    return f"route/capability source: {path}"
            elif path.startswith("tests/dotnet/"):
                # A body-only repair leaves reflected names and coverage metadata
                # unchanged. Include every diff layer, even when edits cancel out.
                attributes = re.compile(r"\[[^\]]*\]", re.S)
                declarations = re.compile(r"\b(?:public|protected)\s+[^{};=]+(?:\{|;|=>)")
                if any(attributes.findall(old) != attributes.findall(snapshot)
                       or declarations.findall(old) != declarations.findall(snapshot)
                       for snapshot in snapshots):
                    return f"test discovery/coverage metadata: {path}"
                if TEST_DISCOVERY.search(changed) or CONTRACT.search(changed):
                    return f"test discovery/coverage metadata: {path}"
            else:
                return f"unclassified C# input: {path}"
    except (OSError, subprocess.CalledProcessError, UnicodeError):
        return "contract guard could not account for the complete diff"
    return ""


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--changed-files", required=True)
    args = parser.parse_args()
    print(reason(args.base, Path(args.changed_files).read_text().splitlines()))


if __name__ == "__main__":
    main()
