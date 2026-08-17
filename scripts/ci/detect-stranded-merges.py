#!/usr/bin/env python3
"""Detect merged PRs whose payload never reached the default branch, and open PRs
that are about to repeat the mistake (#3248, #3316, honua-sdk-js#1317).

A PR opened against a *stack base* rather than the default branch is merged into
that base. GitHub then reports the PR as MERGED, CI was green, and the linked
issue often gets closed -- but if the base branch itself is never merged, the
payload sits on a branch nobody is looking at. The failure is silent by
construction, which is why it needs a machine and not a periodic manual sweep.

This script answers two different questions.

Post-mortem: did a merged PR's *content* reach the default branch?
------------------------------------------------------------------
The cheap test -- ``git merge-base --is-ancestor <mergeCommit> origin/trunk`` --
is a **commit-identity** test, and identity is the wrong question. A squash
merge, a cherry-pick, or an independent re-implementation all put the content on
the default branch under a different SHA and all read as "stranded". The first
sweep for #3248 filed three such false positives out of four findings, including
a headline claim ("~3,800 insertions not in the product") that was later refuted
file by file. So the ancestor test is used only to pick *candidates*; every
candidate is then adjudicated by content.

Content adjudication is per path, three-way:

1. **Payload extraction.** The candidate's own commits are
   ``git rev-list <prHead> --not <mergeCommit>^1 <defaultRef> --no-merges`` --
   the commits on the PR head that are on neither the stack base nor the default
   branch. This matters: a PR raised against a *stale* stack base has its file
   list inflated with unrelated default-branch drift (#3113 reports 366 changed
   files for what is a 23-file change). Excluding commits the default branch
   already has removes that drift instead of adjudicating it.
2. **Blob comparison.** If the path's blob at the merge equals its blob on the
   default branch, the content landed byte-identically -- regardless of SHA.
3. **Added-line presence.** Otherwise the significant lines the PR *added* to
   that path are looked for in the default branch's current version of the file.
   All present => the change is there (possibly edited since). None present, and
   the default branch has not touched the path since the merge => the change is
   very probably lost. In between, or with later default-branch commits on that
   path => the branch moved past it; flagged as uncertain, not as loss.

The added-line test is a **heuristic**, deliberately biased toward
false-positives-that-say-so over silent misses:

* A line moved to a different file reads as missing.
* A line re-worded during a later re-land reads as missing.
* A pure-removal change (no added lines) cannot be judged at all and is reported
  as ``indeterminate`` rather than guessed at.
* Substring matching means a line that legitimately occurs elsewhere in the file
  reads as present.

Hence the split between ``stranded`` (files absent outright -- hard evidence) and
``edits-missing`` (files present but the PR's added lines are not -- strong, and
worth a human minute, but not proof).

Preventive: is an open PR stacked on a base that has already landed?
--------------------------------------------------------------------
By the time the merged sweep fires, the work is already stranded. An open PR
whose base branch is already an ancestor of the default branch -- or no longer
exists -- will strand its payload the moment it merges, and the remedy is one
command (``gh pr edit <N> --base trunk``). That turns the scheduled job from an
autopsy into a warning, which is #3248's last acceptance criterion.

Offline use
-----------
Every classifier is a pure function over resolved facts, and ``--fixture`` feeds
those facts from JSON, so the whole classification is testable with no ``gh``
call, no network, and no git repository. See ``fixtures/stranded-merges-*.json``.

Examples::

    # human-readable sweep of the last 250 merged PRs plus every open PR
    scripts/ci/detect-stranded-merges.py

    # CI use: machine-readable, non-zero exit when something needs a human
    scripts/ci/detect-stranded-merges.py --json --fail-on actionable

    # offline replay of recorded facts
    scripts/ci/detect-stranded-merges.py --fixture scripts/ci/fixtures/stranded-merges-3248.json
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from typing import Any, Iterable, Sequence

# Bases that are expected to be non-ancestors for a while by design. The merge
# train assembles a batch on train/batch/<sha>/<id>, lands the batch, and moves
# on; an escalated or abandoned batch leaves permanent non-ancestor merges that
# are noise, not findings (ADR-0055).
DEFAULT_IGNORED_BASE_PREFIXES = ("train/batch/",)

# Classifications for merged PRs, least to most actionable.
MERGED_ON_DEFAULT = "on-default-branch"
MERGED_LANDED = "landed"
MERGED_SUPERSEDED = "superseded"
MERGED_EDITS_MISSING = "edits-missing"
MERGED_STRANDED = "stranded"

# Classifications for open PRs.
OPEN_ON_DEFAULT = "based-on-default-branch"
OPEN_LIVE_BASE = "stacked-live-base"
OPEN_UNKNOWN_BASE = "unknown-base"
OPEN_NEEDS_RETARGET = "needs-retarget"

# Findings a human has to look at. Everything else is informational.
ACTIONABLE_CLASSIFICATIONS = frozenset(
    {MERGED_STRANDED, MERGED_EDITS_MISSING, OPEN_NEEDS_RETARGET}
)

# Per-path verdicts.
PATH_IDENTICAL = "identical"
PATH_PRESENT = "present"
PATH_PARTIAL = "partial"
PATH_MISSING = "missing"
PATH_ABSENT = "absent"
PATH_INDETERMINATE = "indeterminate"
PATH_DELETION_PENDING = "deletion-not-applied"

# A line has to carry some information before its absence means anything. Braces,
# `else`, and blank lines occur everywhere and would match by accident.
MIN_SIGNIFICANT_LINE_LENGTH = 8

# Per-path cap on how many distinct added lines are probed. Generated files can
# add tens of thousands of lines and the verdict never changes after a few hundred.
MAX_PROBED_LINES = 400


# --------------------------------------------------------------------------- #
# Shell plumbing
# --------------------------------------------------------------------------- #


def run(argv: Sequence[str], *, check: bool = True) -> str:
    """Run a command and return stdout, raising on failure when ``check``."""
    result = subprocess.run(argv, capture_output=True, text=True, check=False, errors="replace")
    if check and result.returncode != 0:
        raise RuntimeError(f"{' '.join(argv)} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout


def git(*args: str, check: bool = True) -> str:
    return run(["git", *args], check=check)


def git_ok(*args: str) -> bool:
    """True when the git command exits zero."""
    return subprocess.run(["git", *args], capture_output=True, text=True, check=False).returncode == 0


# --------------------------------------------------------------------------- #
# Pure classification
# --------------------------------------------------------------------------- #


def significant_added_lines(diff_text: str) -> dict[str, list[str]]:
    """Added lines per post-image path from a unified diff, noise filtered out.

    Reads ``+++ b/<path>`` headers, so renames are attributed to the new path and
    files the diff deletes (``+++ /dev/null``) contribute nothing.
    """
    per_path: dict[str, list[str]] = {}
    current: str | None = None
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            target = line[4:].strip()
            current = None if target == "/dev/null" else target[2:] if target.startswith("b/") else target
            if current is not None:
                per_path.setdefault(current, [])
            continue
        if line.startswith("---") or line.startswith("@@") or line.startswith("diff --git"):
            continue
        if current is not None and line.startswith("+"):
            stripped = line[1:].strip()
            if len(stripped) >= MIN_SIGNIFICANT_LINE_LENGTH:
                per_path[current].append(stripped)
    return per_path


def classify_path(
    *,
    path: str,
    head_blob: str | None,
    default_blob: str | None,
    added_lines: Sequence[str],
    default_text: str | None,
    touched_on_default_since_merge: bool,
) -> dict[str, Any]:
    """Adjudicate one path of a stranded candidate against the default branch.

    ``head_blob`` / ``default_blob`` are blob object ids, or ``None`` when the
    path does not exist on that side. ``default_text`` is the default branch's
    current content, only needed when the blobs differ.
    """
    verdict: str
    probed = list(dict.fromkeys(added_lines))[:MAX_PROBED_LINES]
    found = 0

    if head_blob is None:
        # The PR deleted the path. Absent downstream is the intended outcome;
        # still present means the deletion has not landed, which is not lost work.
        verdict = PATH_IDENTICAL if default_blob is None else PATH_DELETION_PENDING
    elif default_blob is None:
        verdict = PATH_ABSENT
    elif default_blob == head_blob:
        verdict = PATH_IDENTICAL
    elif not probed:
        # Nothing was added, so there is no textual evidence either way (a pure
        # removal, or a binary file). Say so rather than guess.
        verdict = PATH_INDETERMINATE
    else:
        haystack = default_text or ""
        found = sum(1 for line in probed if line in haystack)
        if found == len(probed):
            verdict = PATH_PRESENT
        elif found == 0:
            verdict = PATH_MISSING
        else:
            verdict = PATH_PARTIAL

    entry: dict[str, Any] = {"path": path, "verdict": verdict}
    if verdict in (PATH_PARTIAL, PATH_MISSING):
        entry["addedLinesProbed"] = len(probed)
        entry["addedLinesFound"] = found
        entry["supersededOnDefault"] = touched_on_default_since_merge
    return entry


def classify_merged_pr(
    pr: dict[str, Any],
    *,
    on_default_branch: bool,
    path_verdicts: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    """Classify one merged PR from resolved facts. Pure."""
    finding = {
        "number": pr["number"],
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "base": pr.get("baseRefName") or pr.get("base") or "",
        "mergeCommit": pr.get("mergeCommit") if isinstance(pr.get("mergeCommit"), str)
        else (pr.get("mergeCommit") or {}).get("oid", ""),
        "mergedAt": pr.get("mergedAt"),
    }

    if on_default_branch:
        finding["classification"] = MERGED_ON_DEFAULT
        return finding

    verdicts = list(path_verdicts)
    finding["paths"] = verdicts
    absent = [v["path"] for v in verdicts if v["verdict"] == PATH_ABSENT]
    lost = [
        v["path"]
        for v in verdicts
        if v["verdict"] in (PATH_MISSING, PATH_PARTIAL) and not v.get("supersededOnDefault")
    ]
    moved_on = [
        v["path"]
        for v in verdicts
        if v["verdict"] in (PATH_MISSING, PATH_PARTIAL, PATH_INDETERMINATE)
    ]

    finding["absentPaths"] = absent
    finding["unlandedEditPaths"] = lost

    if absent:
        finding["classification"] = MERGED_STRANDED
    elif lost:
        finding["classification"] = MERGED_EDITS_MISSING
    elif moved_on:
        finding["classification"] = MERGED_SUPERSEDED
    else:
        finding["classification"] = MERGED_LANDED
    return finding


def classify_open_pr(
    pr: dict[str, Any],
    *,
    default_branch: str,
    base_exists: bool,
    base_landed: bool | None,
    ignored_prefixes: Iterable[str] = DEFAULT_IGNORED_BASE_PREFIXES,
) -> dict[str, Any]:
    """Classify one open PR from resolved facts. Pure.

    ``base_landed`` is ``None`` when the base branch could not be resolved to a
    commit, which is reported rather than assumed either way.
    """
    base = pr.get("baseRefName") or pr.get("base") or ""
    finding = {
        "number": pr["number"],
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "base": base,
        "baseExists": base_exists,
        "baseLanded": base_landed,
    }

    if base == default_branch or ignored(base, ignored_prefixes):
        finding["classification"] = OPEN_ON_DEFAULT
        return finding

    if not base_exists:
        finding["classification"] = OPEN_NEEDS_RETARGET
        finding["reason"] = "base branch no longer exists"
    elif base_landed is True:
        finding["classification"] = OPEN_NEEDS_RETARGET
        finding["reason"] = f"base branch already merged into {default_branch}"
    elif base_landed is None:
        finding["classification"] = OPEN_UNKNOWN_BASE
        finding["reason"] = "base branch could not be resolved to a commit"
    else:
        finding["classification"] = OPEN_LIVE_BASE
        finding["reason"] = "base branch is still open; re-target once it lands"

    if finding["classification"] == OPEN_NEEDS_RETARGET:
        finding["remedy"] = f"gh pr edit {pr['number']} --base {default_branch}"
    return finding


def ignored(base: str, prefixes: Iterable[str]) -> bool:
    return any(base.startswith(prefix) for prefix in prefixes)


# --------------------------------------------------------------------------- #
# Fact resolution: live (gh + git) and recorded (fixture JSON)
# --------------------------------------------------------------------------- #


class LiveResolver:
    """Resolves the facts the classifiers need from ``gh`` and the local clone."""

    def __init__(self, *, repo: str | None, default_ref: str, remote: str) -> None:
        self.repo = repo
        self.default_ref = default_ref
        self.remote = remote
        self._remote_heads: dict[str, str] | None = None

    # -- PR listing -------------------------------------------------------- #

    def _gh_pr_list(self, state: str, limit: int, fields: str) -> list[dict[str, Any]]:
        argv = ["gh", "pr", "list", "--state", state, "--limit", str(limit), "--json", fields]
        if self.repo:
            argv += ["--repo", self.repo]
        return json.loads(run(argv) or "[]")

    def merged_prs(self, limit: int) -> list[dict[str, Any]]:
        return self._gh_pr_list("merged", limit, "number,title,baseRefName,mergeCommit,mergedAt,url")

    def open_prs(self, limit: int) -> list[dict[str, Any]]:
        return self._gh_pr_list("open", limit, "number,title,baseRefName,headRefName,isDraft,url")

    # -- merged-PR facts --------------------------------------------------- #

    def on_default_branch(self, merge_commit: str) -> bool:
        return git_ok("merge-base", "--is-ancestor", merge_commit, self.default_ref)

    def path_verdicts(self, pr: dict[str, Any], merge_commit: str) -> list[dict[str, Any]]:
        added = self._payload_added_lines(merge_commit)
        if not added:
            return []
        merged_at = pr.get("mergedAt")
        verdicts = []
        for path in sorted(added):
            head_blob = self._blob(merge_commit, path)
            default_blob = self._blob(self.default_ref, path)
            needs_text = head_blob is not None and default_blob is not None and head_blob != default_blob
            verdicts.append(
                classify_path(
                    path=path,
                    head_blob=head_blob,
                    default_blob=default_blob,
                    added_lines=added[path],
                    default_text=self._show(self.default_ref, path) if needs_text else None,
                    touched_on_default_since_merge=(
                        self._touched_since(path, merged_at) if needs_text and merged_at else False
                    ),
                )
            )
        return verdicts

    def _payload_commits(self, merge_commit: str) -> list[str]:
        """Commits carrying the PR's own work, excluding default-branch drift."""
        parents = git("rev-list", "--parents", "-n", "1", merge_commit, check=False).split()
        if len(parents) >= 3:
            # True merge commit: second parent is the PR head.
            argv = ["rev-list", f"{merge_commit}^2", "--not", f"{merge_commit}^1", self.default_ref, "--no-merges"]
        elif len(parents) == 2:
            # Squash or rebase merge: the commit itself is the payload.
            return [merge_commit]
        else:
            return []
        return git(*argv, check=False).split()

    def _payload_added_lines(self, merge_commit: str) -> dict[str, list[str]]:
        added: dict[str, list[str]] = {}
        for commit in self._payload_commits(merge_commit):
            diff = git("diff", "--no-color", "-U0", f"{commit}^", commit, check=False)
            for path, lines in significant_added_lines(diff).items():
                added.setdefault(path, []).extend(lines)
        return added

    def _blob(self, ref: str, path: str) -> str | None:
        result = subprocess.run(
            ["git", "rev-parse", f"{ref}:{path}"], capture_output=True, text=True, check=False
        )
        return result.stdout.strip() if result.returncode == 0 else None

    def _show(self, ref: str, path: str) -> str:
        return git("show", f"{ref}:{path}", check=False)

    def _touched_since(self, path: str, merged_at: str) -> bool:
        out = git("log", f"--since={merged_at}", "--format=%H", "-1", self.default_ref, "--", path, check=False)
        return bool(out.strip())

    # -- open-PR facts ----------------------------------------------------- #

    def base_state(self, base: str) -> tuple[bool, bool | None]:
        """(base branch still exists, base branch already on the default branch)."""
        local = f"{self.remote}/{base}"
        if git_ok("rev-parse", "--verify", "--quiet", f"{local}^{{commit}}"):
            return True, git_ok("merge-base", "--is-ancestor", local, self.default_ref)
        heads = self._heads()
        if base not in heads:
            return False, None
        sha = heads[base]
        if git_ok("cat-file", "-e", f"{sha}^{{commit}}"):
            return True, git_ok("merge-base", "--is-ancestor", sha, self.default_ref)
        return True, None

    def _heads(self) -> dict[str, str]:
        """Authoritative remote branch list; one network call, cached.

        A clone that fetched a single ref has no ``origin/<base>`` for most
        branches, and treating that as "branch deleted" would report every open
        stacked PR as needing a re-target.
        """
        if self._remote_heads is None:
            self._remote_heads = {}
            for line in git("ls-remote", "--heads", self.remote, check=False).splitlines():
                parts = line.split()
                if len(parts) == 2 and parts[1].startswith("refs/heads/"):
                    self._remote_heads[parts[1][len("refs/heads/") :]] = parts[0]
        return self._remote_heads


class FixtureResolver:
    """Replays recorded facts so the classification is testable offline."""

    def __init__(self, fixture: dict[str, Any]) -> None:
        self.fixture = fixture

    def merged_prs(self, limit: int) -> list[dict[str, Any]]:
        return list(self.fixture.get("merged") or [])[:limit]

    def open_prs(self, limit: int) -> list[dict[str, Any]]:
        return list(self.fixture.get("open") or [])[:limit]

    def on_default_branch(self, merge_commit: str) -> bool:
        for pr in self.fixture.get("merged") or []:
            if _merge_oid(pr) == merge_commit:
                return bool(pr.get("onDefaultBranch"))
        return False

    def path_verdicts(self, pr: dict[str, Any], merge_commit: str) -> list[dict[str, Any]]:
        return list(pr.get("paths") or [])

    def base_state(self, base: str) -> tuple[bool, bool | None]:
        state = (self.fixture.get("bases") or {}).get(base)
        if state is None:
            return True, False
        return bool(state.get("exists", True)), state.get("landed")


def _merge_oid(pr: dict[str, Any]) -> str:
    merge = pr.get("mergeCommit")
    if isinstance(merge, str):
        return merge
    return (merge or {}).get("oid", "") if merge else ""


# --------------------------------------------------------------------------- #
# Sweep
# --------------------------------------------------------------------------- #


def sweep(
    resolver: Any,
    *,
    default_branch: str,
    merged_limit: int,
    open_limit: int,
    ignored_prefixes: Iterable[str],
    include_open: bool = True,
) -> dict[str, list[dict[str, Any]]]:
    """Run both passes and return the findings worth showing a human."""
    merged_findings: list[dict[str, Any]] = []
    for pr in resolver.merged_prs(merged_limit):
        merge_commit = _merge_oid(pr)
        base = pr.get("baseRefName") or pr.get("base") or ""
        if not merge_commit or ignored(base, ignored_prefixes):
            continue
        if resolver.on_default_branch(merge_commit):
            continue
        finding = classify_merged_pr(
            pr,
            on_default_branch=False,
            path_verdicts=resolver.path_verdicts(pr, merge_commit),
        )
        merged_findings.append(finding)

    open_findings: list[dict[str, Any]] = []
    if include_open:
        for pr in resolver.open_prs(open_limit):
            base = pr.get("baseRefName") or pr.get("base") or ""
            if base == default_branch or ignored(base, ignored_prefixes):
                continue
            exists, landed = resolver.base_state(base)
            finding = classify_open_pr(
                pr,
                default_branch=default_branch,
                base_exists=exists,
                base_landed=landed,
                ignored_prefixes=ignored_prefixes,
            )
            if finding["classification"] != OPEN_ON_DEFAULT:
                open_findings.append(finding)

    return {"merged": merged_findings, "open": open_findings}


def actionable(result: dict[str, list[dict[str, Any]]]) -> list[dict[str, Any]]:
    return [
        f
        for group in result.values()
        for f in group
        if f["classification"] in ACTIONABLE_CLASSIFICATIONS
    ]


# --------------------------------------------------------------------------- #
# Reporting
# --------------------------------------------------------------------------- #


def _of(findings: Sequence[dict[str, Any]], classification: str) -> list[dict[str, Any]]:
    return [f for f in findings if f["classification"] == classification]


def render_markdown(
    result: dict[str, list[dict[str, Any]]],
    default_ref: str,
    merged_limit: int,
) -> str:
    merged = result.get("merged") or []
    opened = result.get("open") or []
    stranded = _of(merged, MERGED_STRANDED)
    edits = _of(merged, MERGED_EDITS_MISSING)
    superseded = _of(merged, MERGED_SUPERSEDED)
    landed = _of(merged, MERGED_LANDED)
    retarget = _of(opened, OPEN_NEEDS_RETARGET)
    live = _of(opened, OPEN_LIVE_BASE)
    unknown = _of(opened, OPEN_UNKNOWN_BASE)

    lines = [
        f"Swept the {merged_limit} most recent merged PRs and every open PR against `{default_ref}`.",
        "",
    ]

    if not (stranded or edits or retarget):
        lines.append("**Nothing actionable.** No merged PR is missing payload and no open PR needs a re-target.")
        lines.append("")

    if retarget:
        lines += [
            f"### Needs re-target ({len(retarget)}) -- open, preventable",
            "",
            "These are open against a base branch that has **already landed or been deleted**. Merging",
            "them as-is strands the payload the same way #3248 did. One command fixes each.",
            "",
            "| PR | base | why | remedy |",
            "|---|---|---|---|",
        ]
        for f in retarget:
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` | {f.get('reason', '')} "
                f"| `{f.get('remedy', '')}` |"
            )
        lines.append("")

    if stranded:
        lines += [
            f"### Stranded ({len(stranded)}) -- merged, files absent",
            "",
            "Merged into a branch that never reached the default branch, **and** files they added are",
            "still absent. Hard evidence: reviewed, tested work that is not in the product.",
            "",
            "| PR | base | merge commit | files absent |",
            "|---|---|---|---|",
        ]
        for f in stranded:
            paths = ", ".join(f"`{p}`" for p in f["absentPaths"][:5])
            if len(f["absentPaths"]) > 5:
                paths += f" (+{len(f['absentPaths']) - 5} more)"
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{f['mergeCommit'][:9]}` | {paths} |"
            )
        lines.append("")

    if edits:
        lines += [
            f"### Edits missing ({len(edits)}) -- merged, files present but changes are not",
            "",
            "Every file exists on the default branch, but for the paths listed **none of the lines this",
            "PR added are there**, and the default branch has not touched those paths since the merge.",
            "Heuristic, not proof -- a line that was re-worded or moved during a re-land reads as missing.",
            "",
            "| PR | base | merge commit | paths whose edits are absent |",
            "|---|---|---|---|",
        ]
        for f in edits:
            paths = ", ".join(f"`{p}`" for p in f["unlandedEditPaths"][:5])
            if len(f["unlandedEditPaths"]) > 5:
                paths += f" (+{len(f['unlandedEditPaths']) - 5} more)"
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{f['mergeCommit'][:9]}` | {paths} |"
            )
        lines.append("")

    if superseded or landed or live or unknown:
        lines += ["### Informational", ""]
        for f in landed:
            lines.append(
                f"- **landed** -- [#{f['number']}]({f['url']}) merged into `{f['base']}`, which never "
                "reached the default branch, but all of its content is there. Cost was duplicated effort, "
                "not a missing feature."
            )
        for f in superseded:
            lines.append(
                f"- **superseded** -- [#{f['number']}]({f['url']}) merged into `{f['base']}`; the default "
                "branch has changed those paths since, so the difference is the branch moving on rather "
                "than payload loss."
            )
        for f in live:
            lines.append(
                f"- **stacked on a live base** -- [#{f['number']}]({f['url']}) is open against `{f['base']}`, "
                "which is not merged yet. Re-target it the moment that base lands."
            )
        for f in unknown:
            lines.append(
                f"- **unresolved base** -- [#{f['number']}]({f['url']}) is open against `{f['base']}`, which "
                "could not be resolved to a commit in this clone. Not judged either way."
            )
        lines.append("")

    lines += [
        "_Method: candidates come from `git merge-base --is-ancestor`, then every candidate is adjudicated "
        "by content -- blob equality first, then presence of the PR's added lines on the default branch. "
        "Commit identity alone produced three false positives out of four on the first #3248 sweep._",
    ]
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", help="owner/name; defaults to the repository gh resolves in cwd")
    parser.add_argument("--default-branch", default="trunk", help="default branch name (default: trunk)")
    parser.add_argument("--remote", default="origin", help="remote holding the default branch (default: origin)")
    parser.add_argument("--limit", type=int, default=250, help="how many merged PRs to sweep (default: 250)")
    parser.add_argument("--open-limit", type=int, default=200, help="how many open PRs to sweep (default: 200)")
    parser.add_argument("--no-open", action="store_true", help="skip the open-PR re-target pass")
    parser.add_argument("--fixture", help="replay recorded facts from a JSON file instead of calling gh/git")
    parser.add_argument(
        "--ignore-base-prefix",
        action="append",
        default=None,
        help=f"base-branch prefix to skip; repeatable (default: {', '.join(DEFAULT_IGNORED_BASE_PREFIXES)})",
    )
    parser.add_argument("--json", action="store_true", help="emit findings as JSON instead of markdown")
    parser.add_argument(
        "--fail-on",
        choices=["never", "actionable", "stranded", "payload-missing", "any"],
        default="never",
        help="exit non-zero when findings of this severity exist (default: never). "
        "'payload-missing' is a deprecated alias for 'stranded'.",
    )
    args = parser.parse_args()

    prefixes = args.ignore_base_prefix if args.ignore_base_prefix is not None else list(DEFAULT_IGNORED_BASE_PREFIXES)
    default_ref = f"{args.remote}/{args.default_branch}"

    if args.fixture:
        with open(args.fixture, encoding="utf-8") as handle:
            fixture = json.load(handle)
        resolver: Any = FixtureResolver(fixture)
        default_branch = fixture.get("defaultBranch", args.default_branch)
        default_ref = fixture.get("defaultRef", default_ref)
    else:
        resolver = LiveResolver(repo=args.repo, default_ref=default_ref, remote=args.remote)
        default_branch = args.default_branch

    result = sweep(
        resolver,
        default_branch=default_branch,
        merged_limit=args.limit,
        open_limit=args.open_limit,
        ignored_prefixes=prefixes,
        include_open=not args.no_open,
    )

    if args.json:
        print(
            json.dumps(
                {
                    "defaultRef": default_ref,
                    "limit": args.limit,
                    "findings": result["merged"],
                    "openFindings": result["open"],
                    "actionable": len(actionable(result)),
                },
                indent=2,
            )
        )
    else:
        print(render_markdown(result, default_ref, args.limit))

    every = result["merged"] + result["open"]
    if args.fail_on == "any" and every:
        return 1
    if args.fail_on == "actionable" and actionable(result):
        return 1
    if args.fail_on in ("stranded", "payload-missing") and _of(every, MERGED_STRANDED):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
