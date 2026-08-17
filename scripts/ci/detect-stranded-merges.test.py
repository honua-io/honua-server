#!/usr/bin/env python3
"""Offline tests for detect-stranded-merges.py.

Two layers, neither of which touches the network:

* the pure classifiers, driven from a recorded fixture of the six real
  honua-server#3248 PRs plus the open-PR cases from #3316; and
* the git plumbing (payload extraction, blob comparison, base-branch state),
  driven against a synthetic repository built in a temp directory.
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("detect-stranded-merges.py")
SPEC = importlib.util.spec_from_file_location("detect_stranded_merges", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

FIXTURE = json.loads(
    (Path(__file__).parent / "fixtures" / "stranded-merges-3248.json").read_text(encoding="utf-8")
)
DEFAULT_REF = "origin/trunk"


def run_fixture_sweep(**overrides):
    resolver = MODULE.FixtureResolver(FIXTURE)
    kwargs = dict(
        default_branch="trunk",
        merged_limit=250,
        open_limit=250,
        ignored_prefixes=MODULE.DEFAULT_IGNORED_BASE_PREFIXES,
    )
    kwargs.update(overrides)
    return MODULE.sweep(resolver, **kwargs)


def by_number(findings):
    return {f["number"]: f for f in findings}


# --------------------------------------------------------------------------- #
# Diff parsing
# --------------------------------------------------------------------------- #


def test_added_lines_are_read_per_post_image_path():
    diff = (
        "diff --git a/src/a.cs b/src/a.cs\n"
        "--- a/src/a.cs\n"
        "+++ b/src/a.cs\n"
        "@@ -1,0 +2 @@\n"
        "+public sealed record Thing { public int Value { get; set; } }\n"
        "-removed line that should be ignored entirely\n"
        "diff --git a/src/gone.cs b/src/gone.cs\n"
        "--- a/src/gone.cs\n"
        "+++ /dev/null\n"
        "@@ -1 +0,0 @@\n"
        "-public sealed class Gone { }\n"
    )
    added = MODULE.significant_added_lines(diff)
    assert added["src/a.cs"] == ["public sealed record Thing { public int Value { get; set; } }"]
    assert "src/gone.cs" not in added, "a path the diff deletes contributes no added lines"


def test_short_and_punctuation_only_lines_are_not_evidence():
    diff = "--- a/x.cs\n+++ b/x.cs\n@@ -0,0 +1,3 @@\n+{\n+    }\n+    public int VeryDistinctiveThing { get; set; }\n"
    added = MODULE.significant_added_lines(diff)
    assert added["x.cs"] == ["public int VeryDistinctiveThing { get; set; }"]


# --------------------------------------------------------------------------- #
# Per-path adjudication
# --------------------------------------------------------------------------- #


def path(**kwargs):
    base = dict(
        path="src/a.cs",
        head_blob="aaa",
        default_blob="bbb",
        added_lines=[],
        default_text="",
        touched_on_default_since_merge=False,
    )
    base.update(kwargs)
    return MODULE.classify_path(**base)


def test_equal_blobs_are_identical_regardless_of_commit_identity():
    # The whole point: a squash or a re-land puts the same bytes on the default
    # branch under a different SHA, and that is not a loss.
    assert path(head_blob="same", default_blob="same")["verdict"] == MODULE.PATH_IDENTICAL


def test_missing_path_on_the_default_branch_is_absent():
    assert path(default_blob=None)["verdict"] == MODULE.PATH_ABSENT


def test_a_deletion_the_default_branch_already_has_is_not_a_finding():
    assert path(head_blob=None, default_blob=None)["verdict"] == MODULE.PATH_IDENTICAL


def test_a_deletion_the_default_branch_has_not_applied_is_reported_separately():
    # Not lost work: the file is still there, which is the opposite problem.
    assert path(head_blob=None)["verdict"] == MODULE.PATH_DELETION_PENDING


def test_all_added_lines_present_means_the_change_landed():
    verdict = path(added_lines=["public int Value { get; set; }"], default_text="x public int Value { get; set; } y")
    assert verdict["verdict"] == MODULE.PATH_PRESENT


def test_no_added_line_present_is_missing_and_records_the_counts():
    verdict = path(added_lines=["public int Value { get; set; }"], default_text="nothing like it here")
    assert verdict["verdict"] == MODULE.PATH_MISSING
    assert verdict["addedLinesProbed"] == 1
    assert verdict["addedLinesFound"] == 0


def test_some_added_lines_present_is_partial():
    verdict = path(added_lines=["alpha alpha alpha", "beta beta beta"], default_text="alpha alpha alpha")
    assert verdict["verdict"] == MODULE.PATH_PARTIAL
    assert (verdict["addedLinesFound"], verdict["addedLinesProbed"]) == (1, 2)


def test_a_change_with_no_added_lines_is_indeterminate_not_guessed():
    assert path(added_lines=[])["verdict"] == MODULE.PATH_INDETERMINATE


def test_superseded_is_recorded_on_the_path_not_inferred_later():
    verdict = path(added_lines=["alpha alpha alpha"], default_text="", touched_on_default_since_merge=True)
    assert verdict["supersededOnDefault"] is True


# --------------------------------------------------------------------------- #
# Merged-PR classification
# --------------------------------------------------------------------------- #


def merged_pr(paths, number=1):
    return MODULE.classify_merged_pr(
        {"number": number, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": {"oid": "f" * 40}},
        on_default_branch=False,
        path_verdicts=paths,
    )


def test_an_absent_file_makes_the_pr_stranded():
    finding = merged_pr([{"path": "a", "verdict": MODULE.PATH_IDENTICAL}, {"path": "b", "verdict": MODULE.PATH_ABSENT}])
    assert finding["classification"] == MODULE.MERGED_STRANDED
    assert finding["absentPaths"] == ["b"]


def test_unlanded_edits_on_an_untouched_path_are_edits_missing_not_stranded():
    finding = merged_pr(
        [{"path": "b", "verdict": MODULE.PATH_MISSING, "supersededOnDefault": False}]
    )
    assert finding["classification"] == MODULE.MERGED_EDITS_MISSING
    assert finding["unlandedEditPaths"] == ["b"]


def test_unlanded_edits_on_a_path_the_default_branch_moved_on_are_only_superseded():
    finding = merged_pr(
        [{"path": "b", "verdict": MODULE.PATH_PARTIAL, "supersededOnDefault": True}]
    )
    assert finding["classification"] == MODULE.MERGED_SUPERSEDED
    assert finding["unlandedEditPaths"] == []


def test_a_stranded_merge_whose_content_is_all_there_is_landed():
    finding = merged_pr(
        [{"path": "a", "verdict": MODULE.PATH_IDENTICAL}, {"path": "b", "verdict": MODULE.PATH_PRESENT}]
    )
    assert finding["classification"] == MODULE.MERGED_LANDED
    assert finding["classification"] not in MODULE.ACTIONABLE_CLASSIFICATIONS


def test_a_merge_on_the_default_branch_is_never_adjudicated():
    finding = MODULE.classify_merged_pr(
        {"number": 1, "title": "t", "url": "u", "baseRefName": "trunk", "mergeCommit": {"oid": "a" * 40}},
        on_default_branch=True,
        path_verdicts=[],
    )
    assert finding["classification"] == MODULE.MERGED_ON_DEFAULT
    assert "paths" not in finding


# --------------------------------------------------------------------------- #
# Open-PR classification (#3316)
# --------------------------------------------------------------------------- #


def open_pr(base, *, exists=True, landed=False, number=7):
    return MODULE.classify_open_pr(
        {"number": number, "title": "t", "url": "u", "baseRefName": base},
        default_branch="trunk",
        base_exists=exists,
        base_landed=landed,
        ignored_prefixes=MODULE.DEFAULT_IGNORED_BASE_PREFIXES,
    )


def test_open_pr_on_a_landed_base_needs_a_retarget_and_says_how():
    finding = open_pr("feat/already-landed", landed=True, number=42)
    assert finding["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert finding["remedy"] == "gh pr edit 42 --base trunk"


def test_open_pr_on_a_deleted_base_needs_a_retarget():
    finding = open_pr("feat/gone", exists=False, landed=None)
    assert finding["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert "no longer exists" in finding["reason"]


def test_open_pr_on_a_live_base_is_only_informational():
    finding = open_pr("feat/still-open")
    assert finding["classification"] == MODULE.OPEN_LIVE_BASE
    assert finding["classification"] not in MODULE.ACTIONABLE_CLASSIFICATIONS


def test_open_pr_on_the_default_branch_is_not_a_finding():
    assert open_pr("trunk")["classification"] == MODULE.OPEN_ON_DEFAULT


def test_open_pr_on_a_merge_train_batch_branch_is_not_a_finding():
    assert open_pr("train/batch/deadbeef/8")["classification"] == MODULE.OPEN_ON_DEFAULT


def test_an_unresolvable_base_is_reported_as_unknown_rather_than_assumed():
    finding = open_pr("feat/unresolvable", landed=None)
    assert finding["classification"] == MODULE.OPEN_UNKNOWN_BASE
    assert "remedy" not in finding


# --------------------------------------------------------------------------- #
# The recorded #3248 / #3316 fixture, end to end
# --------------------------------------------------------------------------- #


def test_the_three_pr_3248_merges_that_reached_trunk_are_not_findings():
    merged = by_number(run_fixture_sweep()["merged"])
    for number in (3119, 2974, 2836):
        assert number not in merged, f"#{number} is an ancestor of trunk and must not be reported"


def test_pr_3113_is_not_stranded_because_all_of_its_files_are_on_trunk():
    # The regression this whole rewrite exists for. The commit-identity detector
    # called #3113 the worst loss in the set ("~3,800 insertions not in the
    # product"); by content, every file is present and only two files' edits are
    # not. It must never be reported as stranded again.
    finding = by_number(run_fixture_sweep()["merged"])[3113]
    assert finding["classification"] != MODULE.MERGED_STRANDED
    assert finding["classification"] == MODULE.MERGED_EDITS_MISSING
    assert finding["absentPaths"] == []
    assert finding["unlandedEditPaths"] == [
        "src/Honua.Core.Abstractions/Features/Security/Domain/AccessPolicy.cs",
        "src/Honua.Core/Features/Import/Domain/ImportLimits.cs",
    ]


def test_pr_2835_is_stranded_on_exactly_the_three_lifecycle_files():
    finding = by_number(run_fixture_sweep()["merged"])[2835]
    assert finding["classification"] == MODULE.MERGED_STRANDED
    assert finding["absentPaths"] == [
        "src/Honua.Server/Features/Admin/TileOperations/GeneratedTileCacheKey.cs",
        "src/Honua.Server/Features/Admin/TileOperations/TileOperationExecutionCore.Lifecycle.cs",
        "tests/dotnet/Honua.Server.Tests/Features/Admin/TileCacheLifecycleExecutionTests.cs",
    ]


def test_pr_3116_is_stranded_on_the_raster_capability_registry():
    finding = by_number(run_fixture_sweep()["merged"])[3116]
    assert finding["classification"] == MODULE.MERGED_STRANDED
    assert any("RasterEngineCapabilityRegistry.cs" in p for p in finding["absentPaths"])


def test_a_merge_train_batch_base_is_ignored_even_when_its_files_are_absent():
    assert 9001 not in by_number(run_fixture_sweep()["merged"])


def test_the_fixture_open_prs_split_into_retarget_and_informational():
    opened = by_number(run_fixture_sweep()["open"])
    assert opened[9101]["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert opened[9102]["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert opened[3310]["classification"] == MODULE.OPEN_LIVE_BASE
    assert opened[9105]["classification"] == MODULE.OPEN_UNKNOWN_BASE
    assert 9103 not in opened, "a PR based on trunk is not a finding"
    assert 9104 not in opened, "a merge-train batch base is not a finding"


def test_actionable_counts_both_passes():
    result = run_fixture_sweep()
    numbers = sorted(f["number"] for f in MODULE.actionable(result))
    assert numbers == [2835, 3113, 3116, 9101, 9102]


def test_open_pass_can_be_switched_off():
    assert run_fixture_sweep(include_open=False)["open"] == []


# --------------------------------------------------------------------------- #
# Report rendering
# --------------------------------------------------------------------------- #


def test_markdown_separates_preventive_from_post_mortem():
    out = MODULE.render_markdown(run_fixture_sweep(), DEFAULT_REF, 250)
    assert "### Needs re-target (2)" in out
    assert "### Stranded (2)" in out
    assert "### Edits missing (1)" in out
    assert "gh pr edit 9101 --base trunk" in out


def test_markdown_says_so_when_nothing_is_actionable():
    out = MODULE.render_markdown({"merged": [], "open": []}, DEFAULT_REF, 250)
    assert "Nothing actionable" in out


# --------------------------------------------------------------------------- #
# Git plumbing, against a synthetic repository
# --------------------------------------------------------------------------- #


def _git(cwd, *args):
    subprocess.run(["git", "-C", cwd, *args], check=True, capture_output=True, text=True)


def _write(root, rel, text):
    target = Path(root) / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")


def _build_synthetic_repo(root):
    """A stack-merge, reproduced in miniature.

    trunk ---- A ------------------- C (trunk moves on)
                \\
                 stack/base ---- M (merge of feat/x, never reaches trunk)
                                /
                 feat/x -------
    """
    env = {
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@example.invalid",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@example.invalid",
    }
    os.environ.update(env)
    _git(root, "init", "--quiet", "--initial-branch", "trunk")
    _write(root, "kept.cs", "public sealed class Kept { }\n")
    _write(root, "edited.cs", "public sealed class Edited { public int Old { get; init; } }\n")
    _git(root, "add", "-A")
    _git(root, "commit", "--quiet", "-m", "A")

    _git(root, "branch", "stack/base")
    _git(root, "checkout", "--quiet", "-b", "feat/x")
    _write(root, "added.cs", "public sealed class AddedByThePullRequest { }\n")
    _write(root, "edited.cs", "public sealed class Edited { public int New { get; set; } }\n")
    _git(root, "add", "-A")
    _git(root, "commit", "--quiet", "-m", "PR payload")

    _git(root, "checkout", "--quiet", "stack/base")
    _git(root, "merge", "--quiet", "--no-ff", "feat/x", "-m", "Merge pull request #1 from feat/x")
    merge_commit = subprocess.run(
        ["git", "-C", root, "rev-parse", "HEAD"], check=True, capture_output=True, text=True
    ).stdout.strip()

    # trunk moves on without ever taking the payload.
    _git(root, "checkout", "--quiet", "trunk")
    _write(root, "unrelated.cs", "public sealed class Unrelated { }\n")
    _git(root, "add", "-A")
    _git(root, "commit", "--quiet", "-m", "C")

    # Remote-tracking refs, so base_state() behaves as it does in a real clone.
    _git(root, "update-ref", "refs/remotes/origin/trunk", "trunk")
    _git(root, "update-ref", "refs/remotes/origin/stack/base", "stack/base")
    return merge_commit


def test_git_payload_extraction_ignores_default_branch_drift_and_finds_the_real_loss():
    if shutil.which("git") is None:  # pragma: no cover - CI always has git
        print("  (skipped: git not on PATH)")
        return
    root = tempfile.mkdtemp(prefix="stranded-merges-")
    cwd = os.getcwd()
    try:
        merge_commit = _build_synthetic_repo(root)
        os.chdir(root)
        resolver = MODULE.LiveResolver(repo=None, default_ref="origin/trunk", remote="origin")

        assert resolver.on_default_branch(merge_commit) is False, "the merge never reached trunk"

        verdicts = {v["path"]: v["verdict"] for v in resolver.path_verdicts({"mergedAt": None}, merge_commit)}
        # Only the two files the PR itself touched; unrelated.cs is trunk drift.
        assert set(verdicts) == {"added.cs", "edited.cs"}, verdicts
        assert verdicts["added.cs"] == MODULE.PATH_ABSENT
        assert verdicts["edited.cs"] == MODULE.PATH_MISSING

        finding = MODULE.classify_merged_pr(
            {"number": 1, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": merge_commit},
            on_default_branch=False,
            path_verdicts=resolver.path_verdicts({"mergedAt": None}, merge_commit),
        )
        assert finding["classification"] == MODULE.MERGED_STRANDED
        assert finding["absentPaths"] == ["added.cs"]
    finally:
        os.chdir(cwd)
        shutil.rmtree(root, ignore_errors=True)


def test_git_base_state_distinguishes_live_deleted_and_landed_bases():
    if shutil.which("git") is None:  # pragma: no cover - CI always has git
        print("  (skipped: git not on PATH)")
        return
    root = tempfile.mkdtemp(prefix="stranded-bases-")
    cwd = os.getcwd()
    try:
        _build_synthetic_repo(root)
        os.chdir(root)
        # A landed base: point a remote-tracking ref at a commit trunk already has.
        _git(root, "update-ref", "refs/remotes/origin/feat/landed", "trunk~1")
        resolver = MODULE.LiveResolver(repo=None, default_ref="origin/trunk", remote="origin")
        resolver._remote_heads = {"trunk": "x", "stack/base": "x", "feat/landed": "x"}

        assert resolver.base_state("stack/base") == (True, False), "an open stack base has not landed"
        assert resolver.base_state("feat/landed") == (True, True), "an ancestor of trunk has landed"
        assert resolver.base_state("feat/never-existed") == (False, None)
    finally:
        os.chdir(cwd)
        shutil.rmtree(root, ignore_errors=True)


TESTS = [value for name, value in sorted(globals().items()) if name.startswith("test_") and callable(value)]
for case in TESTS:
    case()
print(f"detect-stranded-merges={len(TESTS)} tests ok")
