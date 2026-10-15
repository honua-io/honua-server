#!/usr/bin/env python3
"""Bound retention for non-default protocol profile outputs.

Dry-run unless --apply is set. Deletes only bin/protocol-* and obj/protocol-*
directories older than seven days under --root. Default all outputs
(bin/Debug, bin/Release, obj/Debug, obj/Release) are refused. An active marker
(.protocol-profile-active) refuses the run. Paths outside --root are refused.
Foreign jobs are not deleted: the root is caller-supplied and must contain the
trees. A size over about 8 GiB is a warning, not a reason to delete them.
"""

from __future__ import annotations

import argparse
import shutil
import sys
import time
from pathlib import Path


MARKER = ".protocol-profile-active"
WARN_BYTES = 8 * 1024 * 1024 * 1024
MAX_AGE_DAYS = 7


def directory_size(path: Path) -> int:
    total = 0
    for file_path in path.rglob("*"):
        if file_path.is_file() and not file_path.is_symlink():
            total += file_path.stat().st_size
    return total


def candidates(root: Path):
    found = []
    for parent_name in ("bin", "obj"):
        for parent in root.rglob(parent_name):
            if not parent.is_dir() or parent.name != parent_name:
                continue
            resolved_parent = parent.resolve()
            if not resolved_parent.is_relative_to(root):
                raise SystemExit(f"refusing path outside root: {parent}")
            for child in parent.iterdir():
                if not child.is_dir() or not child.name.startswith("protocol-"):
                    continue
                resolved = child.resolve()
                if not resolved.is_relative_to(root):
                    raise SystemExit(f"refusing path outside root: {child}")
                relative = resolved.relative_to(root).as_posix()
                if "/bin/Debug" in f"/{relative}" or "/bin/Release" in f"/{relative}":
                    raise SystemExit(f"refusing default output: {relative}")
                if "/obj/Debug" in f"/{relative}" or "/obj/Release" in f"/{relative}":
                    raise SystemExit(f"refusing default output: {relative}")
                found.append(resolved)
    return found


def main(argv=None):
    parser = argparse.ArgumentParser(description="Prune old protocol-profile build trees")
    parser.add_argument("--root", required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--max-age-days", type=float, default=MAX_AGE_DAYS)
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"refusing missing root: {root}", file=sys.stderr)
        return 2

    if any(root.rglob(MARKER)):
        print(f"refusing prune while {MARKER} is present under {root}", file=sys.stderr)
        return 2

    trees = candidates(root)
    total = sum(directory_size(path) for path in trees)
    if total >= WARN_BYTES:
        print(
            f"warning: protocol profile trees under {root} are {total} bytes; "
            "retention stays age-based and does not delete active or foreign jobs",
            file=sys.stderr,
        )

    cutoff = time.time() - (args.max_age_days * 86400)
    expired = [path for path in trees if path.stat().st_mtime < cutoff]
    for path in expired:
        print(f"{'delete' if args.apply else 'would delete'} {path}")
    if args.apply:
        for path in expired:
            shutil.rmtree(path)
    else:
        print(f"dry-run: {len(expired)} protocol tree(s) older than {args.max_age_days} days; nothing deleted")
    return 0


if __name__ == "__main__":
    sys.exit(main())
