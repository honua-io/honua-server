#!/usr/bin/env python3
"""Offline tests for validate-tracked-file-encoding.py.

Two layers, neither of which touches the network:

* synthetic repositories built in temp directories, which exercise the real git
  plumbing the check depends on -- text/binary classification via
  ``.gitattributes`` and via content auto-detection, index-vs-worktree reads,
  multi-offender reporting, and awkward path spellings; and
* the live repository, which pins the two families of binary fixture #3321
  called out (COG parser ``*.tif``/``*.bin`` and gzipped CI evidence) as
  excluded, so a future ``.gitattributes`` edit that reclassified them as text
  fails here instead of turning the gate red for no reason.
"""

from __future__ import annotations

import importlib.util
import subprocess
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("validate-tracked-file-encoding.py")
SPEC = importlib.util.spec_from_file_location("validate_tracked_file_encoding", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

REPO_ROOT = Path(__file__).resolve().parents[2]

# 0x97 is the byte that took a local reproduction to diagnose in #3320: an em
# dash saved as Windows-1252 instead of UTF-8.
CP1252_EM_DASH = b"\x97"


def git(root: Path, *args: str) -> None:
    subprocess.run(("git", *args), cwd=root, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def new_repo(stack) -> Path:
    root = Path(stack.enter_context(tempfile.TemporaryDirectory()))
    git(root, "init", "-q", "-b", "trunk")
    git(root, "config", "user.email", "test@example.invalid")
    git(root, "config", "user.name", "Test")
    return root


def track(root: Path, relative: str, payload: bytes) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    git(root, "add", "--", relative)


def failures(root: Path) -> list[str]:
    return MODULE.find_failures(root, [])


def assert_that(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def test_clean_repository_passes(stack) -> None:
    root = new_repo(stack)
    track(root, "docs/note.md", "scan → manifest → apply — all UTF-8\n".encode())
    track(root, "scripts/run.sh", b"#!/bin/sh\necho ok\n")
    assert_that(failures(root) == [], "valid UTF-8 files must not be reported")


def test_cp1252_file_is_reported_with_file_byte_offset_and_line(stack) -> None:
    root = new_repo(stack)
    payload = b"line one\nan em dash " + CP1252_EM_DASH + b" here\n"
    track(root, "docs/bad.md", payload)
    found = failures(root)
    assert_that(len(found) == 1, f"expected exactly one finding, got {found}")
    finding = found[0]
    for expected in ("docs/bad.md", "0x97", f"offset {payload.index(CP1252_EM_DASH)}", "line 2"):
        assert_that(expected in finding, f"finding must name {expected}: {finding}")
    # The whole point of #3321 is that the operator learns it was an encoding
    # fault, not a logic failure, without a local reproduction.
    assert_that("Windows-1252" in finding, f"finding must offer the cp1252 reading: {finding}")


def test_every_offender_is_reported_not_just_the_first(stack) -> None:
    root = new_repo(stack)
    for index in range(4):
        track(root, f"docs/bad-{index}.md", b"x" + CP1252_EM_DASH)
    track(root, "docs/good.md", "fine — fine\n".encode())
    found = failures(root)
    assert_that(len(found) == 4, f"expected all four offenders, got {len(found)}: {found}")
    named = " ".join(found)
    for index in range(4):
        assert_that(f"docs/bad-{index}.md" in named, f"offender {index} was not reported")


def test_declared_binary_is_excluded(stack) -> None:
    root = new_repo(stack)
    track(root, ".gitattributes", b"* text=auto eol=lf\n*.tif binary\n*.json.gz binary\n")
    # Bytes that are neither UTF-8 nor NUL-bearing: auto-detection alone would
    # call these text, so this proves the .gitattributes declaration is honoured.
    track(root, "fixtures/tile.tif", b"\xff\xfe\x97\x97 not utf-8 and not nul-bearing")
    track(root, "evidence/baseline.json.gz", b"\x1f\x8b\x08\x00\x97\x97\x97\x97")
    assert_that(failures(root) == [], "files declared binary in .gitattributes must be skipped")


def test_auto_detected_binary_is_excluded_without_an_extension_rule(stack) -> None:
    root = new_repo(stack)
    # No .gitattributes entry: git's content auto-detection classifies a blob
    # with a NUL in its first 8000 bytes as binary. A new binary fixture type
    # therefore needs no change to this check.
    track(root, "fixtures/unknown.newext", b"\x00\x01\x02\x97\xff payload")
    assert_that(failures(root) == [], "auto-detected binary must be skipped without an extension rule")


def test_text_file_containing_only_a_bad_byte_is_still_checked(stack) -> None:
    root = new_repo(stack)
    # A lone 0x97 is not NUL-bearing, so git calls it text and the check owns it.
    track(root, "docs/tiny.md", CP1252_EM_DASH)
    found = failures(root)
    assert_that(len(found) == 1, f"a one-byte text file must still be checked: {found}")


def test_reads_the_index_not_the_working_tree(stack) -> None:
    root = new_repo(stack)
    track(root, "docs/bad.md", b"bad " + CP1252_EM_DASH)
    # Repairing only the working tree must not clear the finding: the index is
    # what a merge would take, and it still holds the corrupt bytes.
    (root / "docs/bad.md").write_text("repaired — in the worktree only\n", encoding="utf-8")
    found = failures(root)
    assert_that(len(found) == 1, f"a worktree-only repair must not clear the finding: {found}")
    assert_that("docs/bad.md" in found[0], found[0])

    git(root, "add", "--", "docs/bad.md")
    assert_that(failures(root) == [], "staging the repair must clear the finding")


def test_awkward_paths_are_handled(stack) -> None:
    root = new_repo(stack)
    track(root, "docs/a file with spaces.md", b"ok " + CP1252_EM_DASH)
    track(root, "docs/ümlaut — name.md", b"ok " + CP1252_EM_DASH)
    found = failures(root)
    assert_that(len(found) == 2, f"quoted/unicode paths must be parsed, got {found}")
    named = " ".join(found)
    assert_that("a file with spaces.md" in named, named)
    assert_that("ümlaut" in named, named)


def test_identical_blobs_at_two_paths_are_both_reported(stack) -> None:
    root = new_repo(stack)
    # The same SHA appears twice in one `git cat-file --batch` request; both
    # requests must get their own payload or the second path is misattributed.
    payload = b"duplicate " + CP1252_EM_DASH
    track(root, "docs/one.md", payload)
    track(root, "docs/two.md", payload)
    found = failures(root)
    assert_that(len(found) == 2, f"both paths sharing one blob must be reported, got {found}")


def test_report_truncates_but_says_how_many_it_dropped(stack) -> None:
    root = new_repo(stack)
    over = MODULE.MAX_REPORTED + 3
    for index in range(over):
        track(root, f"docs/bad-{index:03d}.md", b"x" + CP1252_EM_DASH)
    result = subprocess.run(("python3", str(SCRIPT)), cwd=root, capture_output=True, text=True, check=False)
    assert_that(result.returncode == 1, f"expected exit 1, got {result.returncode}")
    assert_that(f"{over} tracked text file(s)" in result.stderr, result.stderr[:400])
    listed = result.stderr.count("  - docs/bad-")
    assert_that(listed == MODULE.MAX_REPORTED, f"expected {MODULE.MAX_REPORTED} listed, got {listed}")
    # A silent cap reads as "that was all of them"; say what was dropped.
    assert_that(f"and {over - MODULE.MAX_REPORTED} more" in result.stderr, result.stderr[-400:])


def test_cli_exit_codes_and_output(stack) -> None:
    root = new_repo(stack)
    track(root, "docs/good.md", "fine — fine\n".encode())
    clean = subprocess.run(
        ("python3", str(SCRIPT)), cwd=root, capture_output=True, text=True, check=False
    )
    assert_that(clean.returncode == 0, f"clean repo must exit 0: {clean.stderr}")

    track(root, "docs/bad.md", b"bad " + CP1252_EM_DASH)
    dirty = subprocess.run(
        ("python3", str(SCRIPT)), cwd=root, capture_output=True, text=True, check=False
    )
    assert_that(dirty.returncode == 1, f"an offending repo must exit 1, got {dirty.returncode}")
    assert_that("docs/bad.md" in dirty.stderr, dirty.stderr)
    # The remediation note has to mention the unrecoverable '?' substitution:
    # the bytes named above are only half of what a cp1252 round trip did.
    assert_that("'?'" in dirty.stderr, dirty.stderr)

    outside = subprocess.run(
        ("python3", str(SCRIPT), "--root", str(root / "docs")),
        cwd=root,
        capture_output=True,
        text=True,
        check=False,
    )
    assert_that(outside.returncode in (0, 1), f"a subdirectory root must still resolve: {outside.stderr}")


def test_pathspec_narrows_the_scan(stack) -> None:
    root = new_repo(stack)
    track(root, "docs/bad.md", b"bad " + CP1252_EM_DASH)
    track(root, "src/bad.txt", b"bad " + CP1252_EM_DASH)
    assert_that(len(MODULE.find_failures(root, ["docs"])) == 1, "pathspec must narrow the scan")
    assert_that(len(MODULE.find_failures(root, [])) == 2, "an empty pathspec must scan everything")


def test_live_repository_is_clean_and_still_excludes_its_binary_fixtures() -> None:
    scanned = set(MODULE.text_paths(REPO_ROOT, []))
    assert_that(len(scanned) > 1000, f"expected the live scan to cover the repository, got {len(scanned)}")

    tracked = subprocess.run(
        ("git", "ls-files", "-z", "--", "tests/dotnet/Honua.Core.Tests/Raster/CogParser/Fixtures/"),
        cwd=REPO_ROOT,
        capture_output=True,
        check=True,
    ).stdout.decode()
    fixtures = [path for path in tracked.split("\0") if path.endswith((".tif", ".bin"))]
    assert_that(bool(fixtures), "expected COG parser binary fixtures to exist")
    for fixture in fixtures:
        assert_that(fixture not in scanned, f"binary COG fixture must be excluded: {fixture}")

    gzipped = subprocess.run(
        ("git", "ls-files", "-z", "--", "*.json.gz"), cwd=REPO_ROOT, capture_output=True, check=True
    ).stdout.decode()
    for archive in (path for path in gzipped.split("\0") if path):
        assert_that(archive not in scanned, f"gzipped evidence must be excluded: {archive}")

    assert_that(MODULE.find_failures(REPO_ROOT, []) == [], "the live repository must already be clean")


def main() -> int:
    import contextlib

    synthetic = [value for name, value in sorted(globals().items()) if name.startswith("test_") and name != "main"]
    for case in synthetic:
        with contextlib.ExitStack() as stack:
            if case.__code__.co_argcount:
                case(stack)
            else:
                case()
        print(f"ok - {case.__name__}")
    print(f"\n{len(synthetic)} passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
