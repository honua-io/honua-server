#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
from pathlib import Path

SCRIPT = Path(__file__).with_name("detect-stranded-merges.py")
SPEC = importlib.util.spec_from_file_location("detect_stranded_merges", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

DEFAULT_REF = "origin/trunk"


def pr(number: int, base: str, oid: str, title: str = "t") -> dict:
    return {
        "number": number,
        "title": title,
        "url": f"https://example.invalid/pull/{number}",
        "baseRefName": base,
        "mergeCommit": {"oid": oid},
        "mergedAt": "2026-08-01T00:00:00Z",
    }


def install(monkey: dict) -> None:
    """Point the module's shell-outs at in-memory fakes."""
    MODULE.is_ancestor = lambda commit, ref: commit in monkey["ancestors"]
    MODULE.path_exists_on = lambda ref, path: path in monkey["paths_on_default"]
    MODULE.fetch_merged_prs = lambda repo, limit: monkey["prs"][:limit]
    MODULE.fetch_pr_paths = lambda repo, number: monkey["pr_paths"].get(number, [])


def test_merges_reaching_the_default_branch_are_not_findings():
    install(
        {
            "ancestors": {"aaa"},
            "paths_on_default": set(),
            "prs": [pr(1, "trunk", "aaa")],
            "pr_paths": {},
        }
    )
    assert MODULE.sweep(repo=None, default_ref=DEFAULT_REF, limit=10, ignored_prefixes=()) == []


def test_stranded_merge_whose_files_are_absent_is_payload_missing():
    install(
        {
            "ancestors": set(),
            "paths_on_default": {"src/kept.cs"},
            "prs": [pr(2, "stack/base", "bbb", "feat: thing")],
            "pr_paths": {2: ["src/kept.cs", "src/lost.cs"]},
        }
    )
    findings = MODULE.sweep(repo=None, default_ref=DEFAULT_REF, limit=10, ignored_prefixes=())
    assert len(findings) == 1
    assert findings[0]["classification"] == MODULE.CLASSIFICATION_PAYLOAD_MISSING
    assert findings[0]["absentPaths"] == ["src/lost.cs"]


def test_stranded_merge_that_re_landed_elsewhere_is_content_present():
    # The honua-server#3113 / honua-sdk-js#863 case: the merge commit is not an
    # ancestor, but a later PR put the same files on the default branch. This
    # must NOT be reported as lost payload -- three of the first four findings
    # across two repos were exactly this, and calling them losses was wrong.
    install(
        {
            "ancestors": set(),
            "paths_on_default": {"src/a.cs", "src/b.cs"},
            "prs": [pr(3, "stack/base", "ccc")],
            "pr_paths": {3: ["src/a.cs", "src/b.cs"]},
        }
    )
    findings = MODULE.sweep(repo=None, default_ref=DEFAULT_REF, limit=10, ignored_prefixes=())
    assert len(findings) == 1
    assert findings[0]["classification"] == MODULE.CLASSIFICATION_CONTENT_PRESENT
    assert findings[0]["absentPaths"] == []


def test_ignored_base_prefixes_are_skipped():
    install(
        {
            "ancestors": set(),
            "paths_on_default": set(),
            "prs": [pr(4, "train/batch/abc/1", "ddd")],
            "pr_paths": {4: ["src/x.cs"]},
        }
    )
    assert MODULE.sweep(repo=None, default_ref=DEFAULT_REF, limit=10, ignored_prefixes=("train/batch/",)) == []


def test_pr_without_a_merge_commit_is_skipped():
    entry = pr(5, "stack/base", "eee")
    entry["mergeCommit"] = None
    install({"ancestors": set(), "paths_on_default": set(), "prs": [entry], "pr_paths": {}})
    assert MODULE.sweep(repo=None, default_ref=DEFAULT_REF, limit=10, ignored_prefixes=()) == []


def test_pure_deletions_do_not_count_as_missing_payload():
    # A path the PR deleted is supposed to be absent downstream. Counting it
    # would report every cleanup PR as a loss.
    assert MODULE._is_pure_deletion({"path": "gone.cs", "additions": 0, "deletions": 40}) is True
    assert MODULE._is_pure_deletion({"path": "edited.cs", "additions": 3, "deletions": 40}) is False
    assert MODULE._is_pure_deletion({"path": "added.cs", "additions": 12, "deletions": 0}) is False


def test_markdown_separates_the_two_classifications():
    findings = [
        {
            "number": 10,
            "title": "lost",
            "url": "u",
            "base": "b",
            "mergeCommit": "f" * 40,
            "classification": MODULE.CLASSIFICATION_PAYLOAD_MISSING,
            "absentPaths": ["x"],
        },
        {
            "number": 11,
            "title": "relanded",
            "url": "u",
            "base": "b",
            "mergeCommit": "e" * 40,
            "classification": MODULE.CLASSIFICATION_CONTENT_PRESENT,
            "absentPaths": [],
        },
    ]
    out = MODULE.render_markdown(findings, DEFAULT_REF, 250)
    assert "Payload missing (1)" in out
    assert "Stranded merge, content present (1)" in out
    assert "No recovery needed" in out


def test_markdown_says_so_when_clean():
    assert "Nothing to do" in MODULE.render_markdown([], DEFAULT_REF, 250)


test_merges_reaching_the_default_branch_are_not_findings()
test_stranded_merge_whose_files_are_absent_is_payload_missing()
test_stranded_merge_that_re_landed_elsewhere_is_content_present()
test_ignored_base_prefixes_are_skipped()
test_pr_without_a_merge_commit_is_skipped()
test_pure_deletions_do_not_count_as_missing_payload()
test_markdown_separates_the_two_classifications()
test_markdown_says_so_when_clean()
print("detect-stranded-merges=ok")
