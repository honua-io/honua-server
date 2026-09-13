#!/usr/bin/env python3
"""Validate a server-test tar payload before any filesystem extraction."""

from __future__ import annotations

import argparse
import posixpath
import re
import tarfile
from pathlib import PurePosixPath


def within_allowed_roots(normalized: str, is_dir: bool, allowed_roots: tuple[str, ...]) -> bool:
    for root in allowed_roots:
        if normalized == root or normalized.startswith(root + "/"):
            return True
        # Parent directories of a declared root are emitted by `tar -C <staging> .`.
        if is_dir and root.startswith(normalized + "/"):
            return True
    return False


def validate_archive(
    path: str,
    max_unpacked_bytes: int,
    max_entries: int,
    allowed_roots: tuple[str, ...] | None = None,
) -> tuple[int, int]:
    if allowed_roots is not None:
        for root in allowed_roots:
            if (
                not root
                or root.startswith("/")
                or "\\" in root
                or posixpath.normpath(root) != root
                or any(part in ("", ".", "..") for part in root.split("/"))
            ):
                raise ValueError(f"allowed payload root is not a normalized relative path: {root!r}")
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
            # Extraction targets the repository root, so an entry outside the test
            # project's output and the manifest's declared content roots would overwrite
            # checkout content (#4453).
            if allowed_roots is not None and not within_allowed_roots(normalized, member.isdir(), allowed_roots):
                raise ValueError(f"archive entry is outside the declared payload roots: {normalized!r}")
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
    parser.add_argument("--allowed-root", action="append", dest="allowed_roots")
    args = parser.parse_args()
    allowed_roots = tuple(args.allowed_roots) if args.allowed_roots else None
    files, total = validate_archive(args.archive, args.max_unpacked_bytes, args.max_entries, allowed_roots)
    print(f"server-test-archive=accepted files={files} unpacked_bytes={total}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
