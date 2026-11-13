#!/usr/bin/env python3
"""Reject newly committed execution evidence and logs; keep fixtures as source."""

import argparse
from pathlib import PurePosixPath
import subprocess


def forbidden(path: str) -> bool:
    parts = PurePosixPath(path).parts
    return any(part.casefold() == "evidence" for part in parts) or ".log" in parts[-1].casefold()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args()
    paths = subprocess.check_output([
        "git", "diff", "--no-renames", "--diff-filter=A", "--name-only", "-z",
        args.base, args.head, "--",
    ]).decode().split("\0")
    failures = [path for path in paths if path and forbidden(path)]
    for path in failures:
        print(f"{path}: store execution evidence in gitignored artifacts/ or CI assets")
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
