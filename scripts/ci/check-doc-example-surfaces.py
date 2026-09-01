#!/usr/bin/env python3
"""Reject raw curl commands added to docs, except marked wire references."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

CURL_COMMAND = re.compile(r"(?<![\w-])curl[ \t]")
DIFF_LINE = re.compile(r"^\+\+\+ |^@@ .* \+(\d+)(?:,(\d+))? @@")
MARKER = "<!-- wire-reference -->"


def added_lines(repo: Path, base: str) -> list[tuple[str, int, str]]:
    result = subprocess.run(
        ["git", "diff", "--unified=0", "--no-ext-diff", f"{base}...HEAD", "--", "docs/**/*.md"],
        cwd=repo, text=True, capture_output=True, check=True,
    )
    found: list[tuple[str, int, str]] = []
    path = ""
    line_number = 0
    for line in result.stdout.splitlines():
        if line.startswith("+++ b/"):
            path = line[6:]
            continue
        match = DIFF_LINE.match(line)
        if match and line.startswith("@@"):
            line_number = int(match.group(1))
            continue
        if line.startswith("+") and not line.startswith("+++"):
            found.append((path, line_number, line[1:]))
            line_number += 1
        elif not line.startswith("-") and not line.startswith("\\"):
            line_number += 1
    return found


def marked_fence(lines: list[str], line_number: int) -> bool:
    in_fence = False
    marked = False
    for index, line in enumerate(lines, start=1):
        stripped = line.strip()
        if stripped == MARKER and not in_fence:
            marked = True
        elif stripped.startswith(("```", "~~~")):
            if in_fence:
                if index >= line_number:
                    return marked
                in_fence = False
                marked = False
            else:
                in_fence = True
        elif stripped and not in_fence:
            marked = False
        if index == line_number:
            return in_fence and marked
    return False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--base", default="origin/trunk")
    parser.add_argument("--allowlist", default="scripts/ci/doc-wire-reference-allowlist.v1.json")
    args = parser.parse_args(argv)
    repo = Path(args.repo_root).resolve()
    payload = json.loads((repo / args.allowlist).read_text(encoding="utf-8"))
    entries = payload.get("entries", [])
    allowed = {entry["path"] for entry in entries}
    errors: list[str] = []
    for path, number, content in added_lines(repo, args.base):
        if not CURL_COMMAND.search(content):
            continue
        if path not in allowed:
            errors.append(f"{path}:{number}: raw curl added; use Honua CLI, SDK, or MCP")
            continue
        lines = (repo / path).read_text(encoding="utf-8").splitlines()
        if not marked_fence(lines, number):
            errors.append(f"{path}:{number}: allowlisted curl example lacks {MARKER}")
    for entry in entries:
        path = entry.get("path", "")
        reason = entry.get("reason", "")
        if not path.startswith("docs/") or not path.endswith(".md") or not reason.strip():
            errors.append(f"invalid wire-reference allowlist entry: {entry!r}")
            continue
        page = repo / path
        if not page.is_file():
            errors.append(f"stale wire-reference allowlist entry: {path} does not exist")
            continue
        lines = page.read_text(encoding="utf-8").splitlines()
        marked_curl = any(
            CURL_COMMAND.search(line) and marked_fence(lines, number)
            for number, line in enumerate(lines, start=1)
        )
        if not marked_curl:
            errors.append(f"stale wire-reference allowlist entry: {path} has no marked curl example")
    if errors:
        print("documentation example surface gate failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    print("documentation example surface gate passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
