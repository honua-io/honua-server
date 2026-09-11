#!/usr/bin/env python3
"""Offline tests for check-okf-bundle.py.

A gate that wrongly reports clean is the failure mode that matters, so every arm
is exercised against a synthetic bundle before the real one is trusted: a valid
concept, a page with no frontmatter, an unclosed fence, a missing `type`, an
unknown `type`, the rejected `timestamp` field, an empty `title`, a malformed
date, and a stale exclusion entry pointing at something that no longer exists.

The last two cases assert the live repository is green and that its declared
concept types are the ones actually in use, so a docs edit that drops
frontmatter fails here as well as in CI.
"""

from __future__ import annotations

import contextlib
import importlib.util
import json
import tempfile
from contextlib import redirect_stderr, redirect_stdout
from io import StringIO
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-okf-bundle.py")
SPEC = importlib.util.spec_from_file_location("check_okf_bundle", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

REPO_ROOT = Path(__file__).resolve().parents[2]


def assert_that(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


@contextlib.contextmanager
def synthetic(pages: dict[str, str], manifest_overrides: dict | None = None):
    """Run the gate over a temp docs tree with its own manifest."""
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for rel, body in pages.items():
            path = root / "docs" / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(body, encoding="utf-8")
        (root / "docs" / "internal").mkdir(parents=True, exist_ok=True)
        (root / "docs" / "internal" / "notes.md").write_text("# no frontmatter here\n", encoding="utf-8")

        manifest = {
            "version": "honua.okf-bundle/v1",
            "description": "test",
            "spec": {"name": "Open Knowledge Format", "version": "0.2", "url": "x", "note": "y"},
            "program": "x",
            "root": "docs",
            "conceptTypes": {"concept": "d", "guide": "d", "reference": "d", "index": "d"},
            "excludedDirs": [{"path": "docs/internal", "why": "test"}],
            "excludedFiles": [],
            "rejectedFields": {"timestamp": "not an OKF field; use `generated`"},
            "dateFields": ["generated", "verified", "stale_after"],
        }
        manifest.update(manifest_overrides or {})
        scripts = root / "scripts" / "ci"
        scripts.mkdir(parents=True, exist_ok=True)
        (scripts / "okf-bundle.v1.json").write_text(json.dumps(manifest), encoding="utf-8")

        saved_root, saved_manifest = MODULE.REPO_ROOT, MODULE.MANIFEST_PATH
        MODULE.REPO_ROOT = root
        MODULE.MANIFEST_PATH = scripts / "okf-bundle.v1.json"
        try:
            out, err = StringIO(), StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = MODULE.main([])
            yield code, out.getvalue() + err.getvalue()
        finally:
            MODULE.REPO_ROOT, MODULE.MANIFEST_PATH = saved_root, saved_manifest


VALID = '---\ntype: guide\ntitle: "A page"\ndescription: "What it covers."\n---\n# A page\n'


def test_accepts_a_valid_concept():
    with synthetic({"a.md": VALID}) as (code, output):
        assert_that(code == 0, f"valid concept rejected: {output}")
        assert_that("1 concept" in output, output)


def test_excluded_directories_are_not_graded():
    # docs/internal/notes.md has no frontmatter and must not fail the gate.
    with synthetic({"a.md": VALID}) as (code, output):
        assert_that(code == 0, f"excluded dir was graded: {output}")


def test_rejects_a_page_with_no_frontmatter():
    with synthetic({"a.md": "# Bare page\n"}) as (code, output):
        assert_that(code == 1, "a page with no frontmatter passed")
        assert_that("no OKF frontmatter" in output, output)


def test_rejects_an_unclosed_frontmatter_fence():
    with synthetic({"a.md": "---\ntype: guide\n# A page\n"}) as (code, output):
        assert_that(code == 1, "unclosed fence passed")
        assert_that("never closed" in output, output)


def test_rejects_missing_and_unknown_type():
    with synthetic({"a.md": '---\ntitle: "No type"\n---\n# x\n'}) as (code, output):
        assert_that(code == 1, "missing type passed")
        assert_that("no `type`" in output, output)
    with synthetic({"a.md": "---\ntype: sandwich\n---\n# x\n"}) as (code, output):
        assert_that(code == 1, "unknown type passed")
        assert_that("unknown `type`" in output, output)


def test_rejects_the_v01_timestamp_field():
    page = '---\ntype: guide\ntimestamp: "2026-08-27"\n---\n# x\n'
    with synthetic({"a.md": page}) as (code, output):
        assert_that(code == 1, "`timestamp` passed")
        assert_that("`timestamp` must not be used" in output, output)


def test_rejects_empty_title_and_malformed_dates():
    with synthetic({"a.md": "---\ntype: guide\ntitle:\n---\n# x\n"}) as (code, output):
        assert_that(code == 1, "empty title passed")
        assert_that("`title` is present but empty" in output, output)
    for bad in ("last Tuesday", "2026-02-30", "2026-13-01"):
        page = f'---\ntype: guide\ngenerated: "{bad}"\n---\n# x\n'
        with synthetic({"a.md": page}) as (code, output):
            assert_that(code == 1, f"{bad} passed as a date")
            assert_that("`generated` must be an ISO-8601" in output, output)
    page = '---\ntype: guide\ngenerated: "2026-02-29"\n---\n# x\n'
    with synthetic({"a.md": page}) as (code, output):
        assert_that(code == 1, "2026-02-29 is not a real day and must fail")


def test_accepts_the_v02_trust_and_lifecycle_fields():
    page = (
        '---\ntype: guide\ngenerated: "2026-09-11"\nverified: "2026-09-11"\n'
        'stale_after: "2027-01-01"\nstatus: "available"\n---\n# x\n'
    )
    with synthetic({"a.md": page}) as (code, output):
        assert_that(code == 0, f"v0.2 fields rejected: {output}")


def test_a_stale_exclusion_entry_fails():
    overrides = {"excludedDirs": [{"path": "docs/internal", "why": "t"}, {"path": "docs/gone", "why": "t"}]}
    with synthetic({"a.md": VALID}, overrides) as (code, output):
        assert_that(code == 1, "stale exclusion passed")
        assert_that("does not exist" in output, output)


def test_a_remediation_target_must_be_typed_runbook():
    """Retyping a cited page away from `runbook` has to fail, or the rule is decoration."""
    registry = json.loads(
        (REPO_ROOT / "scripts" / "ci" / "code-referenced-anchors.v1.json").read_text(encoding="utf-8")
    )
    base = registry.get("docsBaseUrl", "").rstrip("/")
    cited = [
        entry["url"][len(base):].lstrip("/").split("#")[0] + ".md"
        for entry in registry["references"]
        if MODULE.REMEDIATION_RE.search(entry.get("why", "")) and entry["url"].startswith(base)
    ]
    assert_that(bool(cited), "the anchor registry names no remediation targets; the rule is inert")

    target = REPO_ROOT / "docs" / cited[0]
    original = target.read_text(encoding="utf-8")
    assert_that("type: runbook" in original.split("---")[1], f"{cited[0]} is not typed runbook today")
    try:
        target.write_text(original.replace("type: runbook", "type: guide", 1), encoding="utf-8")
        out, err = StringIO(), StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = MODULE.main([])
        output = out.getvalue() + err.getvalue()
        assert_that(code == 1, f"a mistyped remediation target passed: {output}")
        assert_that("must be `type: runbook`" in output, output)
    finally:
        target.write_text(original, encoding="utf-8")


def test_the_live_repository_is_green():
    out, err = StringIO(), StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = MODULE.main([])
    output = out.getvalue() + err.getvalue()
    assert_that(code == 0, f"the live docs/ bundle does not pass its own gate:\n{output}")


def test_every_declared_concept_type_is_actually_used():
    """A vocabulary entry nothing uses is a type somebody meant to apply and did not."""
    manifest = MODULE.load_manifest()
    out = StringIO()
    with redirect_stdout(out):
        MODULE.main(["--summary"])
    census = out.getvalue()
    for concept_type in manifest["conceptTypes"]:
        assert_that(
            f"  {concept_type:<10}" in census,
            f"concept type {concept_type!r} is declared but no page uses it; "
            "either apply it or drop it from the manifest",
        )


def main() -> int:
    cases = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for case in cases:
        case()
        print(f"ok - {case.__name__}")
    print(f"\n{len(cases)} passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
