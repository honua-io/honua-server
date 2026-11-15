#!/usr/bin/env python3
"""Reject added Redis mentions outside historical notes and the policy itself."""

import argparse
from pathlib import PurePosixPath
import re
import subprocess

POLICY_FILES = {
    "scripts/ci/check-retired-cache.py",
    "scripts/ci/check-retired-cache.test.py",
    "scripts/ci/check-retired-cache-workflow.test.py",
}


def exempt(path: str) -> bool:
    name = PurePosixPath(path)
    return (path in POLICY_FILES
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
