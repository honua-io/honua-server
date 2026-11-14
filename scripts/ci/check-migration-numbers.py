#!/usr/bin/env python3
"""Reject new SQL migration numbers that collide with current trunk or each other."""

import argparse
from pathlib import PurePosixPath
import re
import subprocess

DIRECTORIES = ("src/Honua.Server/Migrations", "src/Honua.Db/Postgres/Migrations")


def git_paths(*arguments):
    return subprocess.check_output(["git", *arguments]).decode().rstrip("\0").split("\0")


def numbered(paths):
    result = {}
    for path in paths:
        item = PurePosixPath(path)
        match = re.fullmatch(r"(\d+)_.+\.sql", item.name)
        if str(item.parent) in DIRECTORIES and match:
            result.setdefault((str(item.parent), int(match[1])), []).append(path)
    return result


def check(base, head, trunk):
    ancestor = subprocess.check_output(["git", "merge-base", base, head], text=True).strip()
    additions = numbered(git_paths("diff", "--no-renames", "--diff-filter=A", "--name-only", "-z", ancestor, head, "--", *DIRECTORIES))
    existing = numbered(git_paths("ls-tree", "-r", "--name-only", "-z", trunk, "--", *DIRECTORIES))
    failures = []
    for (directory, number), paths in additions.items():
        collisions = existing.get((directory, number), [])
        if collisions or len(paths) > 1:
            next_number = max(value for folder, value in existing.keys() | additions.keys() if folder == directory) + 1
            failures.append(
                f"{', '.join(paths)}: migration number {number:03d} is already used by "
                f"{', '.join(collisions or paths)}; next free number: {next_number:03d}. "
                "Renumber the new migration and its hash pin."
            )
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--trunk", default="origin/trunk")
    args = parser.parse_args()
    failures = check(args.base, args.head, args.trunk)
    for failure in failures:
        print(failure)
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
