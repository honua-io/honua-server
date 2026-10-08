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
    "src/Honua.Protocols.", "src/Honua.Server/Features/", "src/Honua.Import/",
)


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], text=True, stderr=subprocess.DEVNULL)


def metadata(source: str) -> tuple[list[str], list[str], str]:
    """Over-approximate attributes and declaration headers without parsing strings
    as C# delimiters. Collection/indexer edits may escalate, never under-select.
    In particular, `]` inside a route constraint must not truncate an attribute.
    """
    mask = list(source)
    brackets = []
    stack = []
    i = 0
    while i < len(source):
        start = i
        if source.startswith("//", i):
            end = source.find("\n", i)
            i = len(source) if end < 0 else end
        elif source.startswith("/*", i):
            end = source.find("*/", i + 2)
            if end < 0:
                raise ValueError("unterminated C# comment")
            i = end + 2
        elif source[i] in ('"', "'"):
            quote = source[i]
            end_quotes = i + 1
            while end_quotes < len(source) and source[end_quotes] == quote:
                end_quotes += 1
            run = end_quotes - i
            if quote == '"' and run >= 3:
                end = source.find(quote * run, i + run)
                if end < 0:
                    raise ValueError("unterminated C# raw string")
                i = end + run
            else:
                verbatim = quote == '"' and (source[max(0, i - 1):i] == "@" or source[max(0, i - 2):i] == "@$")
                i += 1
                while i < len(source):
                    if source[i] == quote:
                        if verbatim and source.startswith(quote * 2, i):
                            i += 2
                            continue
                        i += 1
                        break
                    if not verbatim and source[i] == "\\":
                        i += 2
                    else:
                        i += 1
                else:
                    raise ValueError("unterminated C# literal")
        else:
            if source[i] == "[":
                stack.append(i)
            elif source[i] == "]":
                if not stack:
                    raise ValueError("unbalanced C# bracket")
                opening = stack.pop()
                if not stack:
                    brackets.append(source[opening:i + 1])
            i += 1
            continue
        # Keep offsets/newlines, mask literal/comment punctuation for headers.
        for offset in range(start, min(i, len(source))):
            if source[offset] not in "\r\n":
                mask[offset] = " "
    if stack:
        raise ValueError("unbalanced C# bracket")
    masked = "".join(mask)
    headers = [source[match.start():match.end()] for match in re.finditer(
        r"\b(?:public|protected)\s+[^{};]*(?:\{|;)", masked)]
    return brackets, headers, masked


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
            if path == "README.md":
                continue
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
            spans = [metadata(snapshot) for snapshot in snapshots]
            metadata_changed = any(spans[0][:2] != span[:2] for span in spans)
            if path.startswith("src/"):
                public_contract = any(re.search(
                    r"\b(?:public|protected)\s+(?:\w+\s+)*(?:enum|interface|record|readonly|const)\b|"
                    r"\b(?:public|protected)\s+[^{}();]*[=;]", span[2]) for span in spans)
                contract_path = re.search(r"Endpoint|Route|Registry|Catalog|Capability|Proof|Parity|Startup|Program", path, re.I)
                if (metadata_changed or public_contract or contract_path or not path.startswith(SAFE_SOURCE_ROOTS) or CONTRACT.search("\n".join(snapshots)) or CONTRACT.search(changed)
                        or TEST_DISCOVERY.search(changed)
                        or re.search(r"\b(?:public|protected)\b|^\s*#", changed, re.M)):
                    return f"route/capability source: {path}"
            elif path.startswith("tests/dotnet/"):
                # A body-only repair leaves reflected names and coverage metadata
                # unchanged. Include every diff layer, even when edits cancel out.
                if metadata_changed:
                    return f"test discovery/coverage metadata: {path}"
                if (TEST_DISCOVERY.search(changed) or CONTRACT.search(changed)
                        or re.search(r"^\s*#", changed, re.M)):
                    return f"test discovery/coverage metadata: {path}"
            else:
                return f"unclassified C# input: {path}"
    except (OSError, subprocess.CalledProcessError, UnicodeError, ValueError):
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
