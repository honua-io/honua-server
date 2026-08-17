#!/usr/bin/env python3
"""Offline tests for detect-stranded-merges.py.

Two layers, neither of which touches the network:

* the pure classifiers, driven from a recorded fixture of the six real
  honua-server#3248 PRs plus the open-PR cases from #3316 and the edge cases the
  review of PR #3330 called out; and
* the git plumbing (payload extraction for merge/squash/rebase merges, patch
  identity, blob comparison, quoted paths, base-branch state), driven against
  synthetic repositories built in temp directories.
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

FIXTURE_PATH = Path(__file__).parent / "fixtures" / "stranded-merges-3248.json"
FIXTURE = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))
DEFAULT_REF = "origin/trunk"


def run_fixture_sweep(**overrides):
    kwargs = dict(
        default_branch="trunk",
        merged_limit=250,
        open_limit=250,
        ignored_prefixes=MODULE.DEFAULT_IGNORED_BASE_PREFIXES,
    )
    kwargs.update(overrides)
    return MODULE.sweep(MODULE.FixtureResolver(FIXTURE), **kwargs)


def by_number(findings):
    return {f["number"]: f for f in findings}


# --------------------------------------------------------------------------- #
# Diff parsing and quoted paths
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
    assert MODULE.significant_added_lines(diff)["x.cs"] == ["public int VeryDistinctiveThing { get; set; }"]


def test_git_quoted_paths_are_decoded():
    # Under core.quotepath=on git emits octal escapes; a path kept in that form
    # fails every blob lookup, which silently turns an absent file into "landed".
    assert MODULE.unquote_diff_path(r'"b/docs/h\303\251llo.md"') == "b/docs/héllo.md"
    assert MODULE.unquote_diff_path(r'"b/a\"quote\".md"') == 'b/a"quote".md'
    assert MODULE.unquote_diff_path("b/plain.md") == "b/plain.md"


def test_a_quoted_path_in_a_diff_header_reaches_the_classifier_undecorated():
    diff = '--- /dev/null\n+++ "b/docs/h\\303\\251llo.md"\n@@ -0,0 +1 @@\n+public sealed class Distinctive { }\n'
    assert list(MODULE.significant_added_lines(diff)) == ["docs/héllo.md"]


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
        patch_landed=False,
    )
    base.update(kwargs)
    return MODULE.classify_path(**base)


def test_equal_blobs_are_identical_regardless_of_commit_identity():
    # The whole point: a squash or a re-land puts the same bytes on the default
    # branch under a different SHA, and that is not a loss.
    assert path(head_blob="same", default_blob="same")["verdict"] == MODULE.PATH_IDENTICAL


def test_patch_identity_beats_the_added_line_heuristic():
    verdict = path(patch_landed=True, added_lines=["nothing like this on trunk"], default_text="")
    assert verdict["verdict"] == MODULE.PATH_PATCH_LANDED


def test_missing_path_on_the_default_branch_is_absent():
    assert path(default_blob=None)["verdict"] == MODULE.PATH_ABSENT


def test_a_deletion_the_default_branch_already_has_is_not_a_finding():
    assert path(head_blob=None, default_blob=None)["verdict"] == MODULE.PATH_IDENTICAL


def test_a_deletion_the_default_branch_has_not_applied_is_reported_separately():
    assert path(head_blob=None)["verdict"] == MODULE.PATH_DELETION_PENDING


def test_all_added_lines_present_means_the_change_landed():
    assert path(added_lines=["public int Value { get; set; }"],
                default_text="x public int Value { get; set; } y")["verdict"] == MODULE.PATH_PRESENT


def test_no_added_line_present_is_missing_and_records_the_counts():
    verdict = path(added_lines=["public int Value { get; set; }"], default_text="nothing like it here")
    assert verdict["verdict"] == MODULE.PATH_MISSING
    assert (verdict["addedLinesFound"], verdict["addedLinesProbed"]) == (0, 1)


def test_some_added_lines_present_is_partial():
    verdict = path(added_lines=["alpha alpha alpha", "beta beta beta"], default_text="alpha alpha alpha")
    assert verdict["verdict"] == MODULE.PATH_PARTIAL
    assert (verdict["addedLinesFound"], verdict["addedLinesProbed"]) == (1, 2)


def test_a_change_with_no_added_lines_is_indeterminate_not_guessed():
    assert path(added_lines=[])["verdict"] == MODULE.PATH_INDETERMINATE


def test_a_hot_path_touched_since_the_merge_is_not_superseded_when_nothing_landed():
    # AGENTS.md, ci-shards.json and feature-catalog.json change most weeks. A bare
    # wall-clock `--since` test would launder a wholly absent edit on any of them
    # into a non-actionable "superseded", which is the loss this tool exists for.
    verdict = path(added_lines=["alpha alpha alpha"], default_text="", touched_on_default_since_merge=True)
    assert verdict["verdict"] == MODULE.PATH_MISSING
    assert verdict["supersededOnDefault"] is False


def test_superseded_needs_the_default_branch_to_actually_have_part_of_the_change():
    verdict = path(
        added_lines=["alpha alpha alpha", "beta beta beta"],
        default_text="alpha alpha alpha",
        touched_on_default_since_merge=True,
    )
    assert verdict["verdict"] == MODULE.PATH_PARTIAL
    assert verdict["supersededOnDefault"] is True


# --------------------------------------------------------------------------- #
# Merged-PR classification
# --------------------------------------------------------------------------- #


def merged_pr(paths=(), number=1, reason=None):
    return MODULE.classify_merged_pr(
        {"number": number, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": {"oid": "f" * 40}},
        on_default_branch=False,
        path_verdicts=paths,
        indeterminate_reason=reason,
    )


def test_an_absent_file_makes_the_pr_stranded():
    finding = merged_pr([{"path": "a", "verdict": MODULE.PATH_IDENTICAL}, {"path": "b", "verdict": MODULE.PATH_ABSENT}])
    assert finding["classification"] == MODULE.MERGED_STRANDED
    assert finding["absentPaths"] == ["b"]


def test_unlanded_edits_on_an_untouched_path_are_edits_missing_not_stranded():
    finding = merged_pr([{"path": "b", "verdict": MODULE.PATH_MISSING, "supersededOnDefault": False}])
    assert finding["classification"] == MODULE.MERGED_EDITS_MISSING
    assert finding["unlandedEditPaths"] == ["b"]


def test_partly_landed_edits_on_a_path_the_default_branch_moved_on_are_only_superseded():
    finding = merged_pr([{"path": "b", "verdict": MODULE.PATH_PARTIAL, "supersededOnDefault": True}])
    assert finding["classification"] == MODULE.MERGED_SUPERSEDED
    assert finding["unlandedEditPaths"] == []


def test_a_stranded_merge_whose_content_is_all_there_is_landed():
    finding = merged_pr(
        [{"path": "a", "verdict": MODULE.PATH_IDENTICAL}, {"path": "b", "verdict": MODULE.PATH_PATCH_LANDED}]
    )
    assert finding["classification"] == MODULE.MERGED_LANDED
    assert finding["classification"] not in MODULE.ACTIONABLE_CLASSIFICATIONS


def test_an_unadjudicable_pr_is_indeterminate_and_actionable_never_landed():
    # The regression that mattered most: with no verdicts at all the old code fell
    # through to "landed", so a stack base that was deleted or force-pushed --
    # precisely when payload disappears -- reported a clean bill of health.
    finding = merged_pr(reason="merge commit deadbeef is not in this clone")
    assert finding["classification"] == MODULE.MERGED_INDETERMINATE
    assert finding["classification"] != MODULE.MERGED_LANDED
    assert finding["classification"] in MODULE.ACTIONABLE_CLASSIFICATIONS
    assert "not in this clone" in finding["reason"]


def test_a_merge_on_the_default_branch_is_never_adjudicated():
    finding = MODULE.classify_merged_pr(
        {"number": 1, "title": "t", "url": "u", "baseRefName": "trunk", "mergeCommit": {"oid": "a" * 40}},
        on_default_branch=True,
    )
    assert finding["classification"] == MODULE.MERGED_ON_DEFAULT
    assert "paths" not in finding


def test_merge_oid_tolerates_every_shape_github_returns():
    assert MODULE._merge_oid({"mergeCommit": {"oid": "abc"}}) == "abc"
    assert MODULE._merge_oid({"mergeCommit": "abc"}) == "abc"
    assert MODULE._merge_oid({"mergeCommit": None}) == ""
    assert MODULE._merge_oid({"mergeCommit": {}}) == ""
    assert MODULE._merge_oid({}) == ""


def test_a_pr_without_a_merge_commit_is_skipped_by_the_sweep():
    # GitHub stops reporting mergeCommit once the object is garbage-collected.
    # There is nothing to adjudicate, and it is not evidence of loss either way.
    class Resolver:
        def merged_prs(self, limit):
            return [
                {"number": 5, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": None},
                {"number": 6, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": {"oid": ""}},
            ]

        def open_prs(self, limit):
            return []

        def on_default_branch(self, commit):
            raise AssertionError("must not be asked about a PR with no merge commit")

        def adjudicate(self, pr, commit):
            raise AssertionError("must not adjudicate a PR with no merge commit")

    result = MODULE.sweep(
        Resolver(), default_branch="trunk", merged_limit=10, open_limit=10, ignored_prefixes=()
    )
    assert result["merged"] == []


# --------------------------------------------------------------------------- #
# Open-PR classification (#3316)
# --------------------------------------------------------------------------- #


def open_pr(base, *, exists=True, merged=False, ancestor=False, number=7):
    return MODULE.classify_open_pr(
        {"number": number, "title": "t", "url": "u", "baseRefName": base},
        default_branch="trunk",
        base_exists=exists,
        base_merged=merged,
        base_is_ancestor=ancestor,
        ignored_prefixes=MODULE.DEFAULT_IGNORED_BASE_PREFIXES,
    )


def test_open_pr_on_a_merged_base_needs_a_retarget_and_says_how():
    finding = open_pr("feat/already-merged", merged=True, ancestor=True, number=42)
    assert finding["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert finding["remedy"] == "gh pr edit 42 --base trunk"


def test_open_pr_on_a_deleted_base_needs_a_retarget():
    finding = open_pr("feat/gone", exists=False, merged=None, ancestor=None)
    assert finding["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert "no longer exists" in finding["reason"]


def test_a_fresh_base_that_is_merely_an_ancestor_of_trunk_is_not_a_retarget():
    # A stack base created or reset from trunk has no commits of its own, so its
    # tip *is* an ancestor of trunk. Telling the author to detach a live stack
    # from it would be wrong.
    finding = open_pr("feat/fresh", merged=False, ancestor=True)
    assert finding["classification"] == MODULE.OPEN_LIVE_BASE
    assert "no commits of its own" in finding["reason"]


def test_open_pr_on_a_live_base_is_only_informational():
    finding = open_pr("feat/still-open")
    assert finding["classification"] == MODULE.OPEN_LIVE_BASE
    assert finding["classification"] not in MODULE.ACTIONABLE_CLASSIFICATIONS


def test_open_pr_on_the_default_branch_is_not_a_finding():
    assert open_pr("trunk")["classification"] == MODULE.OPEN_ON_DEFAULT


def test_open_pr_on_a_merge_train_batch_branch_is_not_a_finding():
    assert open_pr("train/batch/deadbeef/8")["classification"] == MODULE.OPEN_ON_DEFAULT


def test_an_unresolvable_base_is_reported_as_unknown_rather_than_assumed():
    for finding in (
        open_pr("feat/unresolvable", exists=None, merged=None),
        open_pr("feat/merge-state-unknown", exists=True, merged=None),
    ):
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


def test_the_fixture_covers_the_unreachable_and_hot_path_cases():
    merged = by_number(run_fixture_sweep()["merged"])
    assert merged[9002]["classification"] == MODULE.MERGED_INDETERMINATE
    assert merged[9003]["classification"] == MODULE.MERGED_EDITS_MISSING
    assert merged[9003]["unlandedEditPaths"] == ["AGENTS.md"], "the hot path must stay actionable"
    assert merged[9004]["classification"] == MODULE.MERGED_LANDED
    assert 9005 not in merged, "a PR whose merge commit GitHub no longer reports is skipped"


def test_the_fixture_open_prs_split_into_retarget_and_informational():
    opened = by_number(run_fixture_sweep()["open"])
    assert opened[9101]["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert opened[9102]["classification"] == MODULE.OPEN_NEEDS_RETARGET
    assert opened[3310]["classification"] == MODULE.OPEN_LIVE_BASE
    assert opened[9106]["classification"] == MODULE.OPEN_LIVE_BASE
    assert opened[9105]["classification"] == MODULE.OPEN_UNKNOWN_BASE
    assert 9103 not in opened, "a PR based on trunk is not a finding"
    assert 9104 not in opened, "a merge-train batch base is not a finding"


def test_a_base_the_fixture_forgot_to_record_is_unknown_not_healthy():
    assert "feat/unrecorded-base" not in (FIXTURE.get("bases") or {})
    assert by_number(run_fixture_sweep()["open"])[9107]["classification"] == MODULE.OPEN_UNKNOWN_BASE


def test_actionable_counts_both_passes():
    numbers = sorted(f["number"] for f in MODULE.actionable(run_fixture_sweep()))
    assert numbers == [2835, 3113, 3116, 9002, 9003, 9101, 9102]


def test_open_pass_can_be_switched_off():
    result = run_fixture_sweep(include_open=False)
    assert result["open"] == []
    assert result["scope"]["openIncluded"] is False


# --------------------------------------------------------------------------- #
# Report rendering and the JSON contract
# --------------------------------------------------------------------------- #


def test_markdown_separates_preventive_from_post_mortem():
    out = MODULE.render_markdown(run_fixture_sweep(), DEFAULT_REF)
    assert "### Needs re-target (2)" in out
    assert "### Stranded (2)" in out
    assert "### Edits missing (2)" in out
    assert "### Could not be adjudicated (1)" in out
    assert "gh pr edit 9101 --base trunk" in out


def test_markdown_says_so_when_nothing_is_actionable():
    out = MODULE.render_markdown({"merged": [], "open": [], "scope": {}}, DEFAULT_REF)
    assert "Nothing actionable" in out


def test_markdown_reports_the_scope_it_actually_swept():
    out = MODULE.render_markdown(run_fixture_sweep(), DEFAULT_REF)
    assert "Swept 11 merged PRs and 8 open PRs" in out

    skipped = MODULE.render_markdown(run_fixture_sweep(include_open=False), DEFAULT_REF)
    assert "the open-PR pass was skipped" in skipped

    truncated = MODULE.render_markdown(run_fixture_sweep(merged_limit=2, open_limit=1), DEFAULT_REF)
    assert "cap was reached" in truncated


def test_json_document_carries_a_schema_version_and_both_arrays():
    document = MODULE.to_json_document(run_fixture_sweep(), DEFAULT_REF)
    assert document["schemaVersion"] == 2
    assert document["actionable"] == 7
    assert {"findings", "openFindings", "scope", "defaultRef"} <= set(document)


def test_the_report_can_be_rendered_from_a_saved_json_document_without_sweeping_again():
    document = MODULE.to_json_document(run_fixture_sweep(), DEFAULT_REF)
    with tempfile.TemporaryDirectory() as root:
        saved = Path(root) / "findings.json"
        saved.write_text(json.dumps(document), encoding="utf-8")
        rendered = subprocess.run(
            ["python3", str(SCRIPT), "--from-json", str(saved)], capture_output=True, text=True, check=True
        ).stdout
    assert "### Needs re-target (2)" in rendered
    assert "Swept 11 merged PRs and 8 open PRs" in rendered


def test_fail_on_severities():
    result = run_fixture_sweep()
    assert MODULE._exit_code("never", result) == 0
    assert MODULE._exit_code("actionable", result) == 1
    assert MODULE._exit_code("stranded", result) == 1
    assert MODULE._exit_code("payload-missing", result) == 1, "deprecated alias must keep working"
    assert MODULE._exit_code("actionable", {"merged": [], "open": []}) == 0


# --------------------------------------------------------------------------- #
# Git plumbing, against synthetic repositories
# --------------------------------------------------------------------------- #

GIT_ENV = {
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@example.invalid",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@example.invalid",
    "GIT_AUTHOR_DATE": "2026-01-01T00:00:00Z",
    "GIT_COMMITTER_DATE": "2026-01-01T00:00:00Z",
}


def _git(cwd, *args):
    subprocess.run(["git", "-C", cwd, *args], check=True, capture_output=True, text=True)


def _rev(cwd, ref="HEAD"):
    return subprocess.run(
        ["git", "-C", cwd, "rev-parse", ref], check=True, capture_output=True, text=True
    ).stdout.strip()


def _write(root, rel, text):
    target = Path(root) / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")


def _commit(root, message, files):
    for rel, text in files.items():
        _write(root, rel, text)
    _git(root, "add", "-A")
    _git(root, "commit", "--quiet", "-m", message)
    return _rev(root)


def _init(root):
    os.environ.update(GIT_ENV)
    _git(root, "init", "--quiet", "--initial-branch", "trunk")
    _commit(root, "A", {"kept.cs": "public sealed class Kept { }\n",
                        "edited.cs": "public sealed class Edited { public int Old { get; init; } }\n"})


def _track(root, *branches):
    for branch in branches:
        _git(root, "update-ref", f"refs/remotes/origin/{branch}", branch)


def _in_repo(builder, body):
    root = tempfile.mkdtemp(prefix="stranded-merges-")
    cwd = os.getcwd()
    try:
        built = builder(root)
        os.chdir(root)
        body(root, built)
    finally:
        os.chdir(cwd)
        shutil.rmtree(root, ignore_errors=True)


def _resolver():
    return MODULE.LiveResolver(repo=None, default_ref="origin/trunk", remote="origin")


def _build_stack_merge(root):
    """A stack-merge, reproduced in miniature.

    trunk ---- A ------------------- C (trunk moves on)
                \\
                 stack/base ---- M (merge of feat/x, never reaches trunk)
                                /
                 feat/x -------
    """
    _init(root)
    _git(root, "branch", "stack/base")
    _git(root, "checkout", "--quiet", "-b", "feat/x")
    _commit(root, "PR payload", {
        "added.cs": "public sealed class AddedByThePullRequest { }\n",
        "edited.cs": "public sealed class Edited { public int New { get; set; } }\n",
        "docs/héllo.md": "# a distinctly non-ascii filename\n",
    })
    _git(root, "checkout", "--quiet", "stack/base")
    _git(root, "merge", "--quiet", "--no-ff", "feat/x", "-m", "Merge pull request #1 from feat/x")
    merge_commit = _rev(root)
    _git(root, "checkout", "--quiet", "trunk")
    _commit(root, "C", {"unrelated.cs": "public sealed class Unrelated { }\n"})
    _track(root, "trunk", "stack/base")
    return merge_commit


def test_git_payload_extraction_ignores_default_branch_drift_and_finds_the_real_loss():
    def body(root, merge_commit):
        resolver = _resolver()
        assert resolver.on_default_branch(merge_commit) is False, "the merge never reached trunk"
        verdicts, reason = resolver.adjudicate({"mergedAt": None, "number": 1}, merge_commit)
        assert reason is None
        seen = {v["path"]: v["verdict"] for v in verdicts}
        # Only what the PR itself touched; unrelated.cs is trunk drift.
        assert set(seen) == {"added.cs", "edited.cs", "docs/héllo.md"}, seen
        assert seen["added.cs"] == MODULE.PATH_ABSENT
        assert seen["edited.cs"] == MODULE.PATH_MISSING
        # A non-ASCII path must be adjudicated, not silently read as identical.
        assert seen["docs/héllo.md"] == MODULE.PATH_ABSENT

        finding = MODULE.classify_merged_pr(
            {"number": 1, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": merge_commit},
            on_default_branch=False,
            path_verdicts=verdicts,
            indeterminate_reason=reason,
        )
        assert finding["classification"] == MODULE.MERGED_STRANDED
        assert sorted(finding["absentPaths"]) == ["added.cs", "docs/héllo.md"]

    _in_repo(_build_stack_merge, body)


def test_a_merge_commit_missing_from_the_clone_is_indeterminate_not_landed():
    def body(root, _merge_commit):
        resolver = _resolver()
        verdicts, reason = resolver.adjudicate({"mergedAt": None, "number": 1}, "deadbeef" * 5)
        assert verdicts == []
        assert reason and "not in this clone" in reason
        finding = MODULE.classify_merged_pr(
            {"number": 1, "title": "t", "url": "u", "baseRefName": "stack/base", "mergeCommit": "deadbeef" * 5},
            on_default_branch=False,
            path_verdicts=verdicts,
            indeterminate_reason=reason,
        )
        assert finding["classification"] == MODULE.MERGED_INDETERMINATE

    _in_repo(_build_stack_merge, body)


def _build_rebase_merge(root):
    """A rebase merge: GitHub reports only the LAST rebased commit as mergeCommit."""
    _init(root)
    _git(root, "branch", "stack/base")
    _git(root, "checkout", "--quiet", "stack/base")
    _commit(root, "feat: first half", {"first.cs": "public sealed class FirstHalfOfThePayload { }\n"})
    last = _commit(root, "feat: second half", {"second.cs": "public sealed class SecondHalfOfThePayload { }\n"})
    _git(root, "checkout", "--quiet", "trunk")
    _track(root, "trunk", "stack/base")
    return last


def test_a_rebase_merge_adjudicates_every_rebased_commit_not_just_the_last():
    def body(root, last):
        resolver = _resolver()
        resolver._pr_commit_summaries = lambda pr: {"feat: first half", "feat: second half"}
        verdicts, reason = resolver.adjudicate({"mergedAt": None, "number": 1}, last)
        assert reason is None
        assert sorted(v["path"] for v in verdicts) == ["first.cs", "second.cs"], (
            "taking only GitHub's mergeCommit would have dropped first.cs entirely"
        )
        assert all(v["verdict"] == MODULE.PATH_ABSENT for v in verdicts)

    _in_repo(_build_rebase_merge, body)


def test_a_squash_merge_does_not_over_collect_earlier_base_commits():
    def body(root, last):
        resolver = _resolver()
        # A squash merge reports one commit for a PR that had several; the walk is
        # bounded by the PR's commit count but must keep only this PR's commits.
        resolver._pr_commit_summaries = lambda pr: {"feat: second half"}
        verdicts, reason = resolver.adjudicate({"mergedAt": None, "number": 1}, last)
        assert reason is None
        assert [v["path"] for v in verdicts] == ["second.cs"]

    _in_repo(_build_rebase_merge, body)


def _build_cherry_picked(root):
    """The payload commit is re-landed on trunk under a different SHA.

    trunk gets an unrelated commit first, so the cherry-pick cannot fast-forward
    and the re-landed commit is a genuinely different object; then trunk edits the
    file again, so blob equality no longer holds and only patch identity can prove
    the payload arrived.
    """
    _init(root)
    _git(root, "branch", "stack/base")
    _git(root, "checkout", "--quiet", "stack/base")
    payload = _commit(root, "feat: recovered later", {
        "recovered.cs": "public sealed class RecoveredByCherryPick { }\n"})
    _git(root, "checkout", "--quiet", "trunk")
    _commit(root, "unrelated trunk work", {"unrelated.cs": "public sealed class Unrelated { }\n"})
    _git(root, "cherry-pick", payload)
    relanded = _rev(root)
    _commit(root, "trunk edits it afterwards", {
        "recovered.cs": "public sealed class RecoveredByCherryPick { }\n// trunk kept editing\n"})
    _track(root, "trunk", "stack/base")
    return payload, relanded


def test_patch_identity_recognises_a_re_landed_payload_even_after_further_edits():
    def body(root, built):
        payload, relanded = built
        assert payload != relanded, "the re-land must be a different commit object"
        resolver = _resolver()
        resolver._pr_commit_summaries = lambda pr: {"feat: recovered later"}
        assert resolver._patch_landed([payload]) == {payload}, "patch identity must see the re-land"
        verdicts, reason = resolver.adjudicate({"mergedAt": None, "number": 1}, payload)
        assert reason is None
        assert [v["verdict"] for v in verdicts] == [MODULE.PATH_PATCH_LANDED]

    _in_repo(_build_cherry_picked, body)


def test_patch_identity_does_not_fire_on_a_different_change_to_the_same_file():
    def body(root, built):
        payload, _relanded = built
        resolver = _resolver()
        # A payload commit that trunk never took must not be laundered as landed
        # just because trunk edited the same file.
        _git(root, "checkout", "--quiet", "stack/base")
        other = _commit(root, "feat: never landed", {
            "recovered.cs": "public sealed class RecoveredByCherryPick { }\n// only on the stack\n"})
        _git(root, "checkout", "--quiet", "trunk")
        assert resolver._patch_landed([other]) == set()

    _in_repo(_build_cherry_picked, body)


def test_git_base_state_uses_local_refs_and_never_guesses_deleted():
    def body(root, _built):
        resolver = _resolver()
        calls = []
        resolver._base_merged = lambda base: calls.append(base) or False
        resolver._branch_exists_via_api = lambda base: None

        assert resolver._heads().keys() >= {"trunk", "stack/base"}, "for-each-ref must populate local heads"
        exists, merged, ancestor = resolver.base_state("stack/base")
        assert (exists, merged) == (True, False)
        assert ancestor is False
        assert calls == ["stack/base"]

        # A base with no local ref and an API lookup that fails is unknown -- not
        # "deleted", which would recommend detaching a live stack.
        assert resolver.base_state("feat/not-fetched") == (None, None, None)

    _in_repo(_build_stack_merge, body)


def test_git_base_state_reports_a_base_the_api_says_is_gone():
    def body(root, _built):
        resolver = _resolver()
        resolver._branch_exists_via_api = lambda base: False
        assert resolver.base_state("feat/deleted") == (False, None, None)

    _in_repo(_build_stack_merge, body)


TESTS = [value for name, value in sorted(globals().items()) if name.startswith("test_") and callable(value)]
for case in TESTS:
    case()
print(f"detect-stranded-merges={len(TESTS)} tests ok")
