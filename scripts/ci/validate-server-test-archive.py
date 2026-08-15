#!/usr/bin/env python3
"""Validate a server-test tar payload before any filesystem extraction."""

from __future__ import annotations

import argparse
import posixpath
import re
import tarfile
from pathlib import PurePosixPath


def validate_archive(path: str, max_unpacked_bytes: int, max_entries: int) -> tuple[int, int]:
    seen: set[str] = set()
    total = 0
    files = 0
    with tarfile.open(path, mode="r:gz") as archive:
        for index, member in enumerate(archive, start=1):
            if index > max_entries:
                raise ValueError("archive exceeds its entry-count bound")
            # GNU tar emits this harmless marker for `tar -C <staging> .`.
            # Accept only the exact directory form; a file or link at the
            # archive root remains invalid.
            if member.name in (".", "./"):
                if not member.isdir():
                    raise ValueError("archive root marker is not a directory")
                continue
            name = member.name.removeprefix("./")
            comparison_name = name[:-1] if member.isdir() and name.endswith("/") else name
            normalized = posixpath.normpath(comparison_name)
            path_value = PurePosixPath(normalized)
            if (
                not name
                or name.startswith(("/", "\\"))
                or re.match(r"^[A-Za-z]:", name) is not None
                or "\\" in name
                or normalized in ("", ".")
                or comparison_name != normalized
                or normalized.startswith("../")
                or any(part in ("", ".", "..") for part in path_value.parts)
                or any(ord(character) < 32 or ord(character) == 127 for character in name)
            ):
                raise ValueError(f"archive contains an unsafe path: {member.name!r}")
            if normalized in seen:
                raise ValueError(f"archive contains a duplicate path: {normalized!r}")
            seen.add(normalized)
            if member.isdir():
                continue
            if not member.isfile():
                raise ValueError(f"archive contains a non-regular entry: {normalized!r}")
            if member.size < 0:
                raise ValueError(f"archive contains an invalid file size: {normalized!r}")
            total += member.size
            files += 1
            if total > max_unpacked_bytes:
                raise ValueError("archive exceeds its unpacked-byte bound")
    if files == 0:
        raise ValueError("archive contains no regular files")
    return files, total


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", required=True)
    parser.add_argument("--max-unpacked-bytes", type=positive_int, required=True)
    parser.add_argument("--max-entries", type=positive_int, default=100_000)
    args = parser.parse_args()
    files, total = validate_archive(args.archive, args.max_unpacked_bytes, args.max_entries)
    print(f"server-test-archive=accepted files={files} unpacked_bytes={total}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
