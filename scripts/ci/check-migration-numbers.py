#!/usr/bin/env python3
"""Preserve trusted migration hashes and reject colliding new migration numbers."""

import argparse
import hashlib
from pathlib import PurePosixPath
import re
import subprocess

DIRECTORIES = ("src/Honua.Server/Migrations", "src/Honua.Db/Postgres/Migrations")
# The licence notice is the second line of every migration, after the copyright line.
NOTICE_LINE = 1


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
    # A licence notice update is admitted when the branch applies it, or when
    # trunk applied it after the branch forked and the branch left both files alone.
    return sorted(path for path in changed if path and path in inherited | candidate
                  and not is_license_notice_update(trunk, head, path)
                  and not (is_license_notice_update(head, trunk, path)
                           and unchanged_since_fork(ancestor, head, path)))


def unchanged_since_fork(ancestor, head, pin):
    sql = pin.removeprefix("eng/schema/migration-hashes/").removesuffix(".sha256")
    return subprocess.run(["git", "diff", "--quiet", ancestor, head, "--", sql, pin]).returncode == 0


def is_license_notice_update(older, newer, pin):
    root = "eng/schema/migration-hashes/"
    sql = pin.removeprefix(root).removesuffix(".sha256")
    if not pin.startswith(root) or not pin.endswith(".sha256") or str(PurePosixPath(sql).parent) not in DIRECTORIES:
        return False
    contents = []
    for ref in (older, newer):
        for path in (sql, pin):
            entry = subprocess.check_output(["git", "ls-tree", ref, "--", path], text=True)
            if not entry.startswith("100644 blob "):
                return False
            contents.append(subprocess.check_output(["git", "show", f"{ref}:{path}"]))
    before, old_pin, after, new_pin = contents
    old = b"-- Licensed under the Elastic License 2.0. See LICENSE in the project root."
    new = b"-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root."
    lines = before.splitlines(keepends=True)
    if len(lines) < NOTICE_LINE + 1 or lines[NOTICE_LINE].rstrip(b"\r\n") != old:
        return False
    expected = b"".join(lines[:NOTICE_LINE]) + lines[NOTICE_LINE].replace(old, new) + b"".join(lines[NOTICE_LINE + 1:])
    old_digest = hashlib.sha256(before.replace(b"\r\n", b"\n")).hexdigest().encode()
    new_digest = hashlib.sha256(after.replace(b"\r\n", b"\n")).hexdigest().encode()
    return after == expected and old_pin.strip().lower() == old_digest and new_pin.strip().lower() == new_digest


def claim_names_these_paths(trunk, path, expected):
    """A reservation names the migration that may land; any other path still collides."""
    if PurePosixPath(path).suffix != ".txt":
        return False
    claimed = subprocess.run(["git", "show", f"{trunk}:{path}"], capture_output=True, text=True)
    return claimed.returncode == 0 and claimed.stdout == expected


def reserved_numbers(trunk):
    """Claim files reserve a number even when the SQL has not landed yet."""
    root = "eng/schema/migration-numbers"
    result = {}
    for path in git_paths("ls-tree", "-r", "--name-only", "-z", trunk, "--", root):
        item = PurePosixPath(path)
        if not re.fullmatch(r"\d+\.txt", item.name):
            continue
        directory = str(item.relative_to(root).parent)
        result.setdefault((directory, int(item.stem)), []).append(path)
    return result


def check(base, head, trunk):
    ancestor = subprocess.check_output(["git", "merge-base", base, head], text=True).strip()
    additions = numbered(git_paths("diff", "--no-renames", "--diff-filter=A", "--name-only", "-z", ancestor, head, "--", *DIRECTORIES))
    existing = numbered(git_paths("ls-tree", "-r", "--name-only", "-z", trunk, "--", *DIRECTORIES))
    for key, paths in reserved_numbers(trunk).items():
        existing.setdefault(key, []).extend(paths)
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
        collisions = [
            path for path in existing.get((directory, number), [])
            if not claim_names_these_paths(trunk, path, expected)
        ]
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
