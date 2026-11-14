#!/usr/bin/env python3
"""Preserve trusted migration hashes and reject colliding new migration numbers."""

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


def changed_existing_pins(ancestor, head, trunk):
    root = "eng/schema/migration-hashes"
    inherited = set(git_paths("ls-tree", "-r", "--name-only", "-z", ancestor, "--", root))
    candidate = set(git_paths("ls-tree", "-r", "--name-only", "-z", head, "--", root))
    # Ignore pins added only on newer trunk, which a stale branch need not carry.
    # Any trunk pin present on the branch must retain its trusted contents.
    changed = git_paths("diff", "--no-renames", "--diff-filter=MDTT", "--name-only", "-z", trunk, head, "--", root)
    return sorted(path for path in changed if path and path in inherited | candidate)


def check(base, head, trunk):
    ancestor = subprocess.check_output(["git", "merge-base", base, head], text=True).strip()
    additions = numbered(git_paths("diff", "--no-renames", "--diff-filter=A", "--name-only", "-z", ancestor, head, "--", *DIRECTORIES))
    existing = numbered(git_paths("ls-tree", "-r", "--name-only", "-z", trunk, "--", *DIRECTORIES))
    failures = []
    changed_pins = changed_existing_pins(ancestor, head, trunk)
    if changed_pins:
        failures.append("Existing migration hash pins are immutable: " + ", ".join(changed_pins))
    claim_root = "eng/schema/migration-numbers"
    claims = git_paths("diff", "--no-renames", "--diff-filter=MDTT", "--name-only", "-z", ancestor, head, "--", claim_root)
    changed_claims = [path for path in claims if re.fullmatch(r"\d+\.txt", PurePosixPath(path).name)]
    if changed_claims:
        failures.append("Migration number claims are immutable: " + ", ".join(changed_claims))
    for (directory, number), paths in additions.items():
        # A shared number claim makes concurrent successful PRs conflict in Git
        # when one lands, even with non-strict branch protection.
        claim = f"{claim_root}/{directory}/{number:03d}.txt"
        expected = "\n".join(sorted(paths)) + "\n"
        actual = subprocess.run(["git", "show", f"{head}:{claim}"], capture_output=True, text=True)
        if actual.returncode or actual.stdout != expected:
            failures.append(f"Add {claim} containing exactly the new migration path followed by a newline: {', '.join(paths)}")
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
