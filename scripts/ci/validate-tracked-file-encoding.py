#!/usr/bin/env python3
"""Assert every tracked text file in the repository decodes as UTF-8.

A branch in #3320 pushed four documentation files saved as Windows-1252. Only
one of them was read by anything in CI, so the other three would have merged
corrupted. The corruption is also partly irreversible: characters the target
encoding could not represent were replaced with a literal ASCII ``?`` (trunk's
``scan -> manifest -> apply`` became ``scan ? manifest ? apply``), and ``?`` is
a valid byte, so no transcoding pass can recover it and no reviewer skimming a
docs diff will notice. It has to be blocked at the boundary.

Text-vs-binary comes from git's own classification (``git ls-files --eol``),
which honours ``.gitattributes`` and falls back to content auto-detection, so a
new binary fixture type does not have to teach this check about itself. The
repository legitimately tracks non-UTF-8 blobs -- COG parser fixtures
(``*.tif``, ``*.bin``) and gzipped CI evidence (``*.json.gz``) -- and those are
excluded by that classification rather than by an extension denylist.

Content is read from the index rather than the working tree, so the check sees
exactly the bytes a merge would take.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

# `git ls-files --eol` announces binary two different ways, and both have to be
# honoured or a declared-binary fixture leaks into the scan:
#
#   i/-text  the *content* has no usable line endings -- git auto-detected a
#            binary blob (a NUL in the first 8000 bytes), e.g. the COG parser
#            `*.bin` fixtures, which carry `attr/text=auto`.
#   attr/-text  the *declaration* unsets `text`, e.g. `*.tif binary` in
#            .gitattributes. A declared-binary blob that happens to hold no NUL
#            reports `i/none`, which is indistinguishable from a legitimate
#            single-line text file, so the attribute is the only signal.
#
# Every other index value (lf, crlf, mixed, none) is a text blob to decode.
BINARY_MARKER = "-text"

MAX_REPORTED = 50


class GitError(RuntimeError):
    """git itself failed, which is never a finding about a tracked file."""


def git(root: Path, *args: str) -> bytes:
    result = subprocess.run(
        ("git", *args),
        cwd=root,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed ({result.returncode}): {result.stderr.decode(errors='replace')}")
    return result.stdout


def is_binary(meta: str) -> bool:
    """Classify one `git ls-files --eol` metadata field as binary."""
    fields = meta.split()
    if not fields:
        raise GitError(f"unparseable ls-files --eol metadata: {meta!r}")
    if fields[0] == f"i/{BINARY_MARKER}":
        return True
    _, marker, declared = meta.partition("attr/")
    return bool(marker) and BINARY_MARKER in declared.split()


def text_paths(root: Path, pathspec: list[str]) -> list[str]:
    """Tracked paths git classifies as text, in index order."""
    out = git(root, "ls-files", "--eol", "-z", "--", *pathspec)
    paths: list[str] = []
    for record in out.split(b"\0"):
        if not record:
            continue
        meta, separator, raw_path = record.partition(b"\t")
        if not separator:
            raise GitError(f"unparseable ls-files --eol record: {record!r}")
        if is_binary(meta.decode()):
            continue
        paths.append(raw_path.decode("utf-8", errors="surrogateescape"))
    return paths


def staged_blobs(root: Path, pathspec: list[str]) -> dict[str, str]:
    """Map tracked path -> index blob SHA."""
    out = git(root, "ls-files", "-s", "-z", "--", *pathspec)
    blobs: dict[str, str] = {}
    for record in out.split(b"\0"):
        if not record:
            continue
        meta, _, raw_path = record.partition(b"\t")
        if not _:
            raise GitError(f"unparseable ls-files -s record: {record!r}")
        fields = meta.split()
        if len(fields) < 3:
            raise GitError(f"unparseable ls-files -s metadata: {meta!r}")
        blobs[raw_path.decode("utf-8", errors="surrogateescape")] = fields[1].decode()
    return blobs


def read_blobs(root: Path, shas: list[str]) -> list[bytes]:
    """Read many blobs in one `git cat-file --batch` pass.

    One request per input line, in order, so duplicate SHAs -- identical files
    tracked at two paths -- still yield one payload each.
    """
    if not shas:
        return []
    process = subprocess.run(
        ("git", "cat-file", "--batch", "--buffer"),
        cwd=root,
        input=("\n".join(shas) + "\n").encode(),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if process.returncode != 0:
        raise GitError(f"git cat-file --batch failed ({process.returncode}): {process.stderr.decode(errors='replace')}")

    payloads: list[bytes] = []
    buffer = process.stdout
    offset = 0
    for sha in shas:
        newline = buffer.find(b"\n", offset)
        if newline < 0:
            raise GitError(f"git cat-file --batch truncated before {sha}")
        header = buffer[offset:newline].decode()
        parts = header.split()
        if len(parts) != 3 or parts[1] != "blob":
            raise GitError(f"git cat-file --batch returned an unexpected header: {header!r}")
        size = int(parts[2])
        start = newline + 1
        payloads.append(buffer[start : start + size])
        # Payload is followed by a single trailing newline the object does not own.
        offset = start + size + 1
    return payloads


def decode_failure(path: str, payload: bytes) -> str | None:
    """A one-line finding for a blob that is not UTF-8, or None when it decodes."""
    try:
        payload.decode("utf-8")
    except UnicodeDecodeError as error:
        byte = error.object[error.start : error.start + 1]
        line = payload.count(b"\n", 0, error.start) + 1
        guess = ""
        try:
            recovered = byte.decode("cp1252")
        except UnicodeDecodeError:
            recovered = ""
        if recovered:
            guess = f"; 0x{byte.hex()} is {recovered!r} in Windows-1252"
        return (
            f"{path}: byte 0x{byte.hex()} at offset {error.start} (line {line}) "
            f"is not valid UTF-8 ({error.reason}){guess}"
        )
    return None


def find_failures(root: Path, pathspec: list[str]) -> list[str]:
    paths = text_paths(root, pathspec)
    if not paths:
        return []
    blobs = staged_blobs(root, pathspec)
    missing = [path for path in paths if path not in blobs]
    if missing:
        raise GitError(f"tracked path has no index entry: {missing[0]}")
    payloads = read_blobs(root, [blobs[path] for path in paths])
    failures = [failure for path, payload in zip(paths, payloads) if (failure := decode_failure(path, payload))]
    return failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=Path.cwd(), help="repository root (default: cwd)")
    parser.add_argument("pathspec", nargs="*", help="optional pathspec to narrow the scan")
    args = parser.parse_args(argv)

    try:
        failures = find_failures(args.root, list(args.pathspec))
    except GitError as error:
        print(f"tracked-file encoding check could not run: {error}", file=sys.stderr)
        return 2

    if not failures:
        return 0

    print(f"{len(failures)} tracked text file(s) are not valid UTF-8:", file=sys.stderr)
    for failure in failures[:MAX_REPORTED]:
        print(f"  - {failure}", file=sys.stderr)
    if len(failures) > MAX_REPORTED:
        print(f"  ... and {len(failures) - MAX_REPORTED} more", file=sys.stderr)
    print(
        "\nRe-save each file as UTF-8. Note that a Windows-1252 round trip also replaces\n"
        "characters the target encoding could not represent with a literal '?', which no\n"
        "transcoding pass can recover -- check the diff for '?' where punctuation or\n"
        "arrows used to be, not only for the bytes named above.\n"
        "\n"
        "If a file is genuinely binary, declare it in .gitattributes (e.g. `*.ext binary`)\n"
        "so git stops classifying it as text; do not weaken this check.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
