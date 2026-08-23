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


def classify(meta: str) -> str:
    """Classify one `git ls-files --eol` metadata field.

    Returns "text", "declared-binary" (an explicit .gitattributes rule), or
    "detected-binary" (git inferred it from content). The last two are not
    interchangeable: a declaration is a human saying "this file has no text
    payload", while detection is a heuristic that a NUL in the first 8000 bytes
    triggers -- and UTF-16 text is full of NULs.
    """
    fields = meta.split()
    if not fields:
        raise GitError(f"unparseable ls-files --eol metadata: {meta!r}")
    _, marker, declared = meta.partition("attr/")
    if marker and BINARY_MARKER in declared.split():
        return "declared-binary"
    if fields[0] == f"i/{BINARY_MARKER}":
        return "detected-binary"
    return "text"


# UTF-16 and UTF-32 text is NUL-dense, so git's content detection calls it
# binary and it would otherwise slip past the whole check -- a file saved as
# UTF-16 is exactly the accident this gate exists to catch. A byte-order mark is
# the unambiguous, cheap signal, and it cannot collide with a real binary fixture
# that anyone has declared: only auto-detected blobs are inspected, so declaring
# a format in .gitattributes remains the escape hatch.
BOMS = {
    b"\x00\x00\xfe\xff": "UTF-32BE",
    b"\xff\xfe\x00\x00": "UTF-32LE",
    b"\xfe\xff": "UTF-16BE",
    b"\xff\xfe": "UTF-16LE",
}


def encoded_text_bom(payload: bytes) -> str | None:
    """Name the non-UTF-8 Unicode encoding a blob announces, if it announces one."""
    for bom, label in BOMS.items():
        if payload.startswith(bom):
            return label
    return None


def classified_paths(root: Path, pathspec: list[str]) -> dict[str, str]:
    """Tracked path -> classification, in index order."""
    out = git(root, "ls-files", "--eol", "-z", "--", *pathspec)
    classified: dict[str, str] = {}
    for record in out.split(b"\0"):
        if not record:
            continue
        meta, separator, raw_path = record.partition(b"\t")
        if not separator:
            raise GitError(f"unparseable ls-files --eol record: {record!r}")
        classified[raw_path.decode("utf-8", errors="surrogateescape")] = classify(meta.decode())
    return classified


def text_paths(root: Path, pathspec: list[str]) -> list[str]:
    """Tracked paths git classifies as text, in index order."""
    return [path for path, kind in classified_paths(root, pathspec).items() if kind == "text"]


# Index modes that name a blob with a byte payload: regular file, executable,
# and symlink (whose payload is its target path). Mode 160000 is a gitlink -- a
# submodule's recorded commit -- which has no payload here. Feeding one to
# `cat-file --batch` returns a `commit` header, and rejecting that as "not a
# blob" would fail the required gate for every submodule a PR adds.
BLOB_MODES = frozenset({"100644", "100755", "120000"})


def staged_blobs(root: Path, pathspec: list[str]) -> dict[str, str]:
    """Map tracked path -> index blob SHA, excluding entries that name no blob."""
    out = git(root, "ls-files", "-s", "-z", "--", *pathspec)
    blobs: dict[str, str] = {}
    for record in out.split(b"\0"):
        if not record:
            continue
        meta, separator, raw_path = record.partition(b"\t")
        if not separator:
            raise GitError(f"unparseable ls-files -s record: {record!r}")
        fields = meta.split()
        if len(fields) < 3:
            raise GitError(f"unparseable ls-files -s metadata: {meta!r}")
        if fields[0].decode() not in BLOB_MODES:
            continue
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
    classified = classified_paths(root, pathspec)
    blobs = staged_blobs(root, pathspec)

    # Text blobs must decode. Auto-detected binaries are additionally screened
    # for a UTF-16/UTF-32 byte-order mark, because git's content detection calls
    # that binary and it would otherwise leave a whole family of mis-encoded
    # text outside the guarantee. Blobs a human declared binary in
    # .gitattributes are not second-guessed -- that declaration is the escape
    # hatch that keeps new binary fixture types from having to teach this check.
    subjects = [
        (path, kind)
        for path, kind in classified.items()
        if kind in ("text", "detected-binary") and path in blobs
    ]
    if not subjects:
        return []
    payloads = read_blobs(root, [blobs[path] for path, _ in subjects])

    failures: list[str] = []
    for (path, kind), payload in zip(subjects, payloads):
        bom = encoded_text_bom(payload)
        if bom is not None:
            failures.append(
                f"{path}: starts with a {bom} byte-order mark, so it is Unicode text in the wrong "
                f"encoding. Re-save it as UTF-8; if it is genuinely binary, declare it in "
                f".gitattributes."
            )
            continue
        if kind != "text":
            continue
        if failure := decode_failure(path, payload):
            failures.append(failure)
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
