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
sweep for #3248 filed three such false positives out of four, including a
headline claim ("~3,800 insertions not in the product") that was later refuted
file by file. So the ancestor test is used only to pick *candidates*; every
candidate is then adjudicated by content, in three descending strengths of
evidence:

1. **Patch identity.** ``git cherry`` / ``git patch-id --stable`` proves that an
   equivalent patch is already on the default branch. This is exact, survives
   squash and cherry-pick, and is checked first.
2. **Blob equality.** If the path's blob at the merge equals its blob on the
   default branch, the content landed byte-identically, whatever the SHA.
3. **Added-line presence.** Otherwise the significant lines the PR *added* to
   that path are looked for in the default branch's current version of the file.

Payload extraction matters as much as adjudication. A PR raised against a
*stale* stack base has its file list inflated with unrelated default-branch
drift (#3113 reports 366 changed files for what is a 23-file change), so the
payload is taken from the candidate's own commits -- for a merge commit,
``git rev-list <head> --not <mergeCommit>^1 <defaultRef> --no-merges``; for a
squash or rebase merge, the new commits ending at ``mergeCommit`` that belong to
this PR, bounded by the PR's own commit list. (GitHub reports only the *last*
rebased commit as ``mergeCommit``, so taking that one commit alone would miss
everything the earlier commits of a rebase merge added.)

The added-line test is a **heuristic**, deliberately biased toward
false-positives-that-say-so over silent misses:

* A line moved to a different file reads as missing.
* A line re-worded during a later re-land reads as missing.
* Substring matching means a line that legitimately occurs elsewhere in the file
  reads as present.

Hence the split between ``stranded`` (files absent outright -- hard evidence) and
``edits-missing`` (files present but the PR's added lines are not -- strong, and
worth a human minute, but not proof). ``superseded`` is reserved for the case
where the default branch demonstrably *has* part of the change and has edited the
path since; a path where **none** of the added lines are present is never
demoted to ``superseded`` just because the file is a hot one that the default
branch happens to touch every week.

Anything that cannot be adjudicated at all -- a merge commit that is not in the
clone because the stack base was deleted or force-pushed, a git failure, a
payload with no added lines to test -- is reported as ``indeterminate`` with the
reason, and counted as actionable. It is never silently reported as ``landed``.

Preventive: is an open PR stacked on a base that has already landed?
--------------------------------------------------------------------
By the time the merged sweep fires, the work is already stranded. An open PR
whose base branch has already been **merged**, or no longer exists, will strand
its payload the moment it merges, and the remedy is one command
(``gh pr edit <N> --base trunk``). That turns the scheduled job from an autopsy
into a warning, which is #3248's last acceptance criterion.

"Merged" is deliberately not "is an ancestor of the default branch": a freshly
created or freshly reset stack base points at a default-branch commit and is an
ancestor while being perfectly alive, and telling someone to detach a live stack
from it would be wrong. The test is whether a PR whose head is that branch has
been merged.

Offline use
-----------
Every classifier is a pure function over resolved facts, and ``--fixture`` feeds
those facts from JSON, so the whole classification is testable with no ``gh``
call, no network, and no git repository. See ``fixtures/stranded-merges-*.json``.

Recorded dispositions
---------------------
A stranded payload stays stranded even after someone decides not to recover it,
so the finding is true every week forever. ``stranded-merge-dispositions.json``
records the decision next to the code: an ``abandoned`` or ``landed-elsewhere``
entry drops the finding out of the actionable count into an *Adjudicated* table,
while ``recovering`` only annotates it. A decision covers exactly the paths it
lists, so a newly actionable path re-opens the finding rather than being absorbed
by an old waiver.

JSON output carries ``schemaVersion``. Version 2 renamed the classifications:
``payload-missing`` -> ``stranded`` and ``content-present`` -> ``landed``, and
added ``edits-missing`` / ``superseded`` / ``indeterminate`` plus the
``openFindings`` array. Version 3 added the per-finding ``disposition`` object
and the ``staleDispositions`` array, and ``actionable`` no longer counts findings
a disposition silenced. ``--fail-on payload-missing`` is still accepted as an
alias for ``--fail-on stranded``.

Examples::

    # human-readable sweep of the last 250 merged PRs plus every open PR
    scripts/ci/detect-stranded-merges.py

    # CI use: sweep once to JSON, render the report from it, never sweep twice
    scripts/ci/detect-stranded-merges.py --json --fail-on actionable > findings.json
    scripts/ci/detect-stranded-merges.py --from-json findings.json

    # offline replay of recorded facts
    scripts/ci/detect-stranded-merges.py --fixture scripts/ci/fixtures/stranded-merges-3248.json
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time
from typing import Any, Iterable, Sequence

SCHEMA_VERSION = 3

# Bases that are expected to be non-ancestors for a while by design. The merge
# train assembles a batch on train/batch/<sha>/<id>, lands the batch, and moves
# on; an escalated or abandoned batch leaves permanent non-ancestor merges that
# are noise, not findings (ADR-0055).
DEFAULT_IGNORED_BASE_PREFIXES = ("train/batch/",)

# Classifications for merged PRs.
MERGED_ON_DEFAULT = "on-default-branch"
MERGED_LANDED = "landed"
MERGED_SUPERSEDED = "superseded"
MERGED_EDITS_MISSING = "edits-missing"
MERGED_STRANDED = "stranded"
MERGED_INDETERMINATE = "indeterminate"

# Classifications for open PRs.
OPEN_ON_DEFAULT = "based-on-default-branch"
OPEN_LIVE_BASE = "stacked-live-base"
OPEN_UNKNOWN_BASE = "unknown-base"
OPEN_NEEDS_RETARGET = "needs-retarget"

# Findings a human has to look at. Everything else is informational.
ACTIONABLE_CLASSIFICATIONS = frozenset(
    {MERGED_STRANDED, MERGED_EDITS_MISSING, MERGED_INDETERMINATE, OPEN_NEEDS_RETARGET}
)

# Recorded dispositions (#3248). A merged stranded payload does not become
# un-stranded when someone decides not to recover it, so the finding is real
# forever and the detector will keep reporting it every Monday. Two of the three
# original #3248 findings are exactly that: #3116 was abandoned by name under the
# single-engine raster decision, and #3113's residual was adjudicated as
# no-action. Left alone, the weekly job files a tracking issue whose majority is
# permanently-decided rows -- which is how a detector teaches everyone to ignore
# it, the failure its own workflow comment warns about. It also invites the
# re-litigation that already happened twice on #3113, once wrongly.
#
# So a decision is recorded in the repository, next to the code, and silences the
# finding it was made about. Two properties keep the ledger from degrading into a
# mute button:
#
# * **Scope-bound.** A disposition records the exact paths that were adjudicated.
#   If the sweep later finds an actionable path the decision did not cover, the
#   finding stays actionable and names the uncovered paths. Deleting a file from
#   the default branch cannot be absorbed by a year-old waiver.
# * **Not for work in flight.** ``recovering`` annotates a finding with the
#   recovery PR and leaves it actionable. Only a *closed* decision silences, and
#   even then an optional ``reviewBy`` date makes the silence lapse.
DISPOSITION_ABANDONED = "abandoned"
DISPOSITION_LANDED_ELSEWHERE = "landed-elsewhere"
DISPOSITION_RECOVERING = "recovering"
DISPOSITION_STATUSES = frozenset(
    {DISPOSITION_ABANDONED, DISPOSITION_LANDED_ELSEWHERE, DISPOSITION_RECOVERING}
)
# Only these close the question. `recovering` deliberately does not.
SILENCING_DISPOSITION_STATUSES = frozenset({DISPOSITION_ABANDONED, DISPOSITION_LANDED_ELSEWHERE})

DEFAULT_DISPOSITIONS_PATH = "scripts/ci/stranded-merge-dispositions.json"

# Per-path verdicts.
PATH_IDENTICAL = "identical"
PATH_PATCH_LANDED = "patch-landed"
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

# Upper bound on how far back a squash/rebase payload walk will look, so a
# malformed commit count can never turn into a whole-branch scan.
MAX_PAYLOAD_WALK = 250

# Upper bound on default-branch commits whose patch id is computed while looking
# for a re-landed payload commit. The search is already narrowed to commits
# touching the same paths, so this only caps a pathological history.
MAX_PATCH_ID_CANDIDATES = 200


class CommandError(RuntimeError):
    """A subprocess the sweep depends on failed."""


# --------------------------------------------------------------------------- #
# Subprocess plumbing -- one place, so failures cannot be swallowed by accident
# --------------------------------------------------------------------------- #

# core.quotepath=off keeps non-ASCII paths literal in diff headers. With it on,
# `docs/héllo.md` arrives as `"docs/h\303\251llo.md"`, every blob lookup for it
# fails, and an absent file is classified as landed.
GIT = ("git", "-c", "core.quotepath=off")


def _run(argv: Sequence[str], *, stdin: str | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        list(argv), input=stdin, capture_output=True, text=True, errors="replace", check=False
    )


def sh(argv: Sequence[str], *, check: bool = True, stdin: str | None = None) -> str:
    """Run a command and return stdout, raising ``CommandError`` when ``check``."""
    result = _run(argv, stdin=stdin)
    if result.returncode != 0:
        if check:
            raise CommandError(f"{' '.join(argv)} failed ({result.returncode}): {result.stderr.strip()}")
        return ""
    return result.stdout


def git(*args: str, check: bool = True) -> str:
    return sh([*GIT, *args], check=check)


def git_ok(*args: str) -> bool:
    """True when the git command exits zero."""
    return _run([*GIT, *args]).returncode == 0


# --------------------------------------------------------------------------- #
# Pure classification
# --------------------------------------------------------------------------- #


def unquote_diff_path(raw: str) -> str:
    """Decode a git-quoted path from a diff header.

    ``core.quotepath=off`` stops git quoting non-ASCII, but a path containing a
    quote, a backslash or a control character is still C-quoted, so the decode
    has to exist regardless.
    """
    if not (len(raw) >= 2 and raw.startswith('"') and raw.endswith('"')):
        return raw
    body = raw[1:-1]
    out = bytearray()
    index = 0
    simple = {"n": 10, "t": 9, "r": 13, "b": 8, "f": 12, "a": 7, "v": 11, '"': 34, "\\": 92}
    while index < len(body):
        char = body[index]
        if char != "\\":
            out.extend(char.encode("utf-8"))
            index += 1
            continue
        if index + 1 >= len(body):
            break
        nxt = body[index + 1]
        if nxt in "01234567":
            out.append(int(body[index + 1 : index + 4], 8) & 0xFF)
            index += 4
        else:
            out.append(simple.get(nxt, ord(nxt)))
            index += 2
    return out.decode("utf-8", "replace")


def significant_added_lines(diff_text: str) -> dict[str, list[str]]:
    """Added lines per post-image path from a unified diff, noise filtered out.

    Reads ``+++ b/<path>`` headers, so renames are attributed to the new path and
    files the diff deletes (``+++ /dev/null``) contribute nothing.
    """
    per_path: dict[str, list[str]] = {}
    current: str | None = None
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            target = unquote_diff_path(line[4:].strip())
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
    patch_landed: bool = False,
) -> dict[str, Any]:
    """Adjudicate one path of a stranded candidate against the default branch.

    ``head_blob`` / ``default_blob`` are blob object ids, or ``None`` when the
    path does not exist on that side. ``patch_landed`` means every payload commit
    touching this path has an exact patch-id equivalent on the default branch --
    proof, and checked before any heuristic.
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
    elif patch_landed:
        verdict = PATH_PATCH_LANDED
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
        # "The default branch moved past this" is only credible when the default
        # branch demonstrably has *part* of the change. A hot path that trunk
        # rewrites weekly must not launder a wholly absent edit into a
        # non-actionable finding, which a bare `--since` test would do.
        entry["supersededOnDefault"] = bool(touched_on_default_since_merge and found > 0)
    return entry


def _merge_oid(pr: dict[str, Any]) -> str:
    merge = pr.get("mergeCommit")
    if isinstance(merge, str):
        return merge
    if isinstance(merge, dict):
        oid = merge.get("oid")
        return oid if isinstance(oid, str) else ""
    return ""


def _base_ref(pr: dict[str, Any]) -> str:
    return pr.get("baseRefName") or pr.get("base") or ""


def classify_merged_pr(
    pr: dict[str, Any],
    *,
    on_default_branch: bool,
    path_verdicts: Sequence[dict[str, Any]] = (),
    indeterminate_reason: str | None = None,
) -> dict[str, Any]:
    """Classify one merged PR from resolved facts. Pure."""
    finding = {
        "number": pr["number"],
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "base": _base_ref(pr),
        "mergeCommit": _merge_oid(pr),
        "mergedAt": pr.get("mergedAt"),
    }

    if on_default_branch:
        finding["classification"] = MERGED_ON_DEFAULT
        return finding

    if indeterminate_reason:
        # No adjudicable facts. This is emphatically not "all content landed":
        # the commonest cause is a stack base that was deleted or force-pushed,
        # which is exactly when payload goes missing.
        finding["classification"] = MERGED_INDETERMINATE
        finding["reason"] = indeterminate_reason
        finding["paths"] = list(path_verdicts)
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
    base_exists: bool | None,
    base_merged: bool | None,
    base_is_ancestor: bool | None = None,
    ignored_prefixes: Iterable[str] = DEFAULT_IGNORED_BASE_PREFIXES,
) -> dict[str, Any]:
    """Classify one open PR from resolved facts. Pure.

    ``None`` means "could not be established", which is reported as such rather
    than assumed either way: guessing "deleted" recommends detaching a live stack.
    """
    base = _base_ref(pr)
    finding = {
        "number": pr["number"],
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "base": base,
        "baseExists": base_exists,
        "baseMerged": base_merged,
    }

    if base == default_branch or ignored(base, ignored_prefixes):
        finding["classification"] = OPEN_ON_DEFAULT
        return finding

    if base_exists is None:
        finding["classification"] = OPEN_UNKNOWN_BASE
        finding["reason"] = "base branch could not be resolved"
    elif base_exists is False:
        finding["classification"] = OPEN_NEEDS_RETARGET
        finding["reason"] = "base branch no longer exists"
    elif base_merged is None:
        finding["classification"] = OPEN_UNKNOWN_BASE
        finding["reason"] = "could not establish whether the base branch has been merged"
    elif base_merged:
        finding["classification"] = OPEN_NEEDS_RETARGET
        finding["reason"] = f"base branch has already been merged into {default_branch}"
    else:
        finding["classification"] = OPEN_LIVE_BASE
        finding["reason"] = (
            f"base branch has no commits of its own beyond {default_branch} yet; re-target once it lands"
            if base_is_ancestor
            else "base branch is still open; re-target once it lands"
        )

    if finding["classification"] == OPEN_NEEDS_RETARGET:
        finding["remedy"] = f"gh pr edit {pr['number']} --base {default_branch}"
    return finding


def ignored(base: str, prefixes: Iterable[str]) -> bool:
    return any(base.startswith(prefix) for prefix in prefixes)


# --------------------------------------------------------------------------- #
# Recorded dispositions
# --------------------------------------------------------------------------- #


class DispositionError(ValueError):
    """The disposition ledger is malformed.

    Raised rather than skipped on purpose. A typo in a ``status`` would otherwise
    fail *open* -- the entry stops silencing and the finding quietly returns to
    the report, which is survivable -- or, worse, a typo in ``pr`` would silence
    nothing while looking like it had. A ledger that decides what CI ignores has
    to be loud when it is wrong.
    """


def load_dispositions(path: str, *, required: bool) -> dict[int, dict[str, Any]]:
    """Read the ledger into ``{pr number: disposition}``.

    ``required`` distinguishes an explicit ``--dispositions`` (a missing file is
    an error the caller asked for) from the default path (a repository that has
    never recorded a decision simply has no ledger, which is not a failure --
    this script is ``workflow_call``-able from repositories that have their own).
    """
    if not os.path.exists(path):
        if required:
            raise DispositionError(f"disposition ledger not found: {path}")
        return {}

    with open(path, encoding="utf-8") as handle:
        try:
            document = json.load(handle)
        except json.JSONDecodeError as error:
            raise DispositionError(f"{path} is not valid JSON: {error}") from error

    entries = document.get("dispositions")
    if not isinstance(entries, list):
        raise DispositionError(f"{path}: expected a top-level 'dispositions' array")

    by_pr: dict[int, dict[str, Any]] = {}
    for index, entry in enumerate(entries):
        where = f"{path}: dispositions[{index}]"
        if not isinstance(entry, dict):
            raise DispositionError(f"{where} is not an object")
        number = entry.get("pr")
        if not isinstance(number, int) or isinstance(number, bool):
            raise DispositionError(f"{where} needs an integer 'pr'")
        if number in by_pr:
            raise DispositionError(f"{where}: #{number} is recorded twice")
        status = entry.get("status")
        if status not in DISPOSITION_STATUSES:
            raise DispositionError(
                f"{where}: 'status' must be one of {sorted(DISPOSITION_STATUSES)}, got {status!r}"
            )
        if not (entry.get("reason") or "").strip():
            raise DispositionError(f"{where}: 'reason' is required -- a silent waiver explains nothing")
        paths = entry.get("paths", [])
        if not isinstance(paths, list) or any(not isinstance(p, str) for p in paths):
            raise DispositionError(f"{where}: 'paths' must be an array of strings")
        review_by = entry.get("reviewBy")
        if review_by is not None:
            if not isinstance(review_by, str) or not _parse_date(review_by):
                raise DispositionError(f"{where}: 'reviewBy' must be an ISO date (YYYY-MM-DD)")
        by_pr[number] = entry
    return by_pr


def _parse_date(value: str) -> dt.date | None:
    try:
        return dt.date.fromisoformat(value)
    except ValueError:
        return None


def actionable_paths(finding: dict[str, Any]) -> list[str]:
    """The paths a finding is actionable *for*, in report order.

    ``indeterminate`` has none by construction -- nothing could be adjudicated --
    so a disposition on it is trivially in scope. That is the right reading: the
    only way to close an unadjudicable finding is for a human to look, and the
    ledger is where they write down that they did.
    """
    return list(finding.get("absentPaths") or []) + list(finding.get("unlandedEditPaths") or [])


def apply_disposition(
    finding: dict[str, Any],
    disposition: dict[str, Any] | None,
    *,
    today: dt.date,
) -> dict[str, Any]:
    """Attach a recorded decision to a finding and decide whether it silences it.

    Pure, and mutates-then-returns the finding the way the classifiers do.
    """
    if not disposition or finding["classification"] not in ACTIONABLE_CLASSIFICATIONS:
        # Nothing to decide. A `landed` or `superseded` finding is already
        # informational, and filing it under "Adjudicated" would assert that its
        # payload never reached the default branch -- the opposite of what those
        # classifications mean. The sweep reports the entry as stale instead.
        return finding

    status = disposition["status"]
    uncovered = [p for p in actionable_paths(finding) if p not in set(disposition.get("paths") or [])]
    review_by = _parse_date(disposition["reviewBy"]) if disposition.get("reviewBy") else None
    lapsed = bool(review_by and today > review_by)

    record: dict[str, Any] = {
        "status": status,
        "reason": disposition["reason"],
        "silences": False,
    }
    for optional in ("decidedOn", "decidedBy", "evidence", "recovery", "reviewBy"):
        if disposition.get(optional):
            record[optional] = disposition[optional]

    if status not in SILENCING_DISPOSITION_STATUSES:
        # `recovering` is a note, not a decision: the payload is still off the
        # default branch, so the row stays in the actionable table with a pointer
        # to whoever is carrying it.
        record["why"] = "recovery is in flight; the finding stays actionable until it lands"
    elif uncovered:
        record["uncoveredPaths"] = uncovered
        record["why"] = (
            "the recorded decision does not cover every actionable path; "
            "re-adjudicate and extend 'paths'"
        )
    elif lapsed:
        record["why"] = f"the review date {disposition['reviewBy']} has passed; re-confirm the decision"
    else:
        record["silences"] = True

    finding["disposition"] = record
    return finding


# --------------------------------------------------------------------------- #
# Fact resolution: live (gh + git) and recorded (fixture JSON)
# --------------------------------------------------------------------------- #


class LiveResolver:
    """Resolves the facts the classifiers need from ``gh`` and the local clone."""

    def __init__(self, *, repo: str | None, default_ref: str, remote: str) -> None:
        self.repo = repo
        self.default_ref = default_ref
        self.remote = remote
        self._local_heads: dict[str, str] | None = None
        self._tree_cache: dict[str, dict[str, str]] = {}
        self._base_state_cache: dict[str, tuple[bool | None, bool | None, bool | None]] = {}
        self._patch_ids: dict[str, str] = {}

    # -- PR listing -------------------------------------------------------- #

    def _gh(self, *args: str, check: bool = True, attempts: int = 3) -> str:
        """Call gh, retrying a required call a couple of times.

        The GraphQL endpoint returns a transient 503 often enough that a weekly
        job would go red on it; a swallowed failure, on the other hand, is how
        the open-PR pass invents re-targets, so a required call still raises once
        the retries are spent.
        """
        argv = ["gh", *args]
        if self.repo and "--repo" not in args:
            argv += ["--repo", self.repo]
        for attempt in range(1, max(attempts, 1) + 1):
            try:
                return sh(argv, check=check)
            except CommandError:
                if attempt >= attempts:
                    raise
                time.sleep(min(2 ** attempt, 8))
        return ""

    def _gh_pr_list(self, state: str, limit: int, fields: str) -> list[dict[str, Any]]:
        return json.loads(self._gh("pr", "list", "--state", state, "--limit", str(limit), "--json", fields) or "[]")

    def merged_prs(self, limit: int) -> list[dict[str, Any]]:
        return self._gh_pr_list("merged", limit, "number,title,baseRefName,mergeCommit,mergedAt,url")

    def open_prs(self, limit: int) -> list[dict[str, Any]]:
        return self._gh_pr_list("open", limit, "number,title,baseRefName,headRefName,isDraft,url")

    # -- merged-PR facts --------------------------------------------------- #

    def on_default_branch(self, merge_commit: str) -> bool:
        return git_ok("merge-base", "--is-ancestor", merge_commit, self.default_ref)

    def adjudicate(self, pr: dict[str, Any], merge_commit: str) -> tuple[list[dict[str, Any]], str | None]:
        """(per-path verdicts, reason the PR could not be adjudicated)."""
        try:
            commits, reason = self._payload_commits(pr, merge_commit)
            if reason:
                return [], reason
            added, touched_by = self._payload_added_lines(commits)
            if not added:
                return [], (
                    f"the {len(commits)} payload commit(s) add no adjudicable lines "
                    "(pure removals, binary content, or an empty diff)"
                )
            landed_commits = self._patch_landed(commits)
            merge_tree = self._tree(merge_commit)
            default_tree = self._tree(self.default_ref)
            touched_since = self._paths_touched_since(sorted(added), pr.get("mergedAt"))

            verdicts = []
            for path in sorted(added):
                head_blob = merge_tree.get(path)
                default_blob = default_tree.get(path)
                differ = head_blob is not None and default_blob is not None and head_blob != default_blob
                owners = touched_by.get(path, set())
                verdicts.append(
                    classify_path(
                        path=path,
                        head_blob=head_blob,
                        default_blob=default_blob,
                        added_lines=added[path],
                        default_text=self._show(self.default_ref, path) if differ else None,
                        touched_on_default_since_merge=path in touched_since,
                        patch_landed=bool(owners) and owners.issubset(landed_commits),
                    )
                )
            return verdicts, None
        except CommandError as error:
            return [], f"git could not adjudicate this merge: {error}"

    def _payload_commits(self, pr: dict[str, Any], merge_commit: str) -> tuple[list[str], str | None]:
        """Commits carrying the PR's own work, excluding default-branch drift."""
        if not git_ok("cat-file", "-e", f"{merge_commit}^{{commit}}"):
            return [], (
                f"merge commit {merge_commit[:9]} is not in this clone -- the stack base was "
                "probably deleted or force-pushed, so the payload cannot be checked"
            )
        parents = git("rev-list", "--parents", "-n", "1", merge_commit).split()[1:]

        if len(parents) >= 2:
            base_parent, head_parent = parents[0], parents[1]
            if not git_ok("cat-file", "-e", f"{head_parent}^{{commit}}"):
                return [], f"the PR head parent {head_parent[:9]} of the merge commit is not in this clone"
            commits = git(
                "rev-list", head_parent, "--not", base_parent, self.default_ref, "--no-merges"
            ).split()
            if not commits:
                return [], "the merge commit brought in no commits of its own"
            return commits, None

        if not parents:
            return [], f"merge commit {merge_commit[:9]} is a root commit with no parent to diff against"

        # Squash or rebase merge. GitHub reports only the *last* rebased commit
        # as mergeCommit, so walking just that one silently drops everything the
        # earlier commits of a rebase merge added. Walk back over the new commits
        # on the base and keep the ones that belong to this PR.
        summaries = self._pr_commit_summaries(pr)
        window = min(max(len(summaries), 1), MAX_PAYLOAD_WALK)
        candidates = git(
            "rev-list", "--no-merges", "-n", str(window), merge_commit, "--not", self.default_ref
        ).split()
        marker = f"(#{pr['number']})"
        mine = [c for c in candidates if self._summary(c) in summaries or marker in self._summary(c)]
        return (mine or [merge_commit]), None

    def _pr_commit_summaries(self, pr: dict[str, Any]) -> set[str]:
        raw = self._gh("pr", "view", str(pr["number"]), "--json", "commits", check=False)
        try:
            commits = json.loads(raw or "{}").get("commits") or []
        except json.JSONDecodeError:
            return set()
        summaries = set()
        for commit in commits:
            headline = commit.get("messageHeadline")
            if headline:
                summaries.add(headline)
        return summaries

    def _summary(self, commit: str) -> str:
        return git("log", "-1", "--format=%s", commit, check=False).strip()

    def _payload_added_lines(self, commits: Sequence[str]) -> tuple[dict[str, list[str]], dict[str, set[str]]]:
        added: dict[str, list[str]] = {}
        touched_by: dict[str, set[str]] = {}
        for commit in commits:
            diff = git("diff", "--no-color", "-U0", f"{commit}^", commit)
            for path, lines in significant_added_lines(diff).items():
                added.setdefault(path, []).extend(lines)
                touched_by.setdefault(path, set()).add(commit)
        return {path: lines for path, lines in added.items() if lines}, touched_by

    def _patch_landed(self, commits: Sequence[str]) -> set[str]:
        """Payload commits that have an exact patch-id equivalent on the default branch.

        Patch identity is what ``git cherry`` is built on, and it is the only
        *proof* available here: it survives squash, rebase and cherry-pick, where
        the commit SHA does not. ``git cherry`` itself is not used, because it
        drops commits that are literally upstream from its output instead of
        marking them, so an exact re-land would read as "not found".

        The search is bounded by path: only default-branch commits touching the
        same files can possibly carry the same patch.
        """
        landed: set[str] = set()
        for commit in commits:
            if git_ok("merge-base", "--is-ancestor", commit, self.default_ref):
                landed.add(commit)
                continue
            wanted = self._patch_id(commit)
            if not wanted:
                continue
            paths = [
                line for line in git("diff-tree", "--no-commit-id", "--name-only", "-r", commit,
                                     check=False).splitlines() if line
            ]
            if not paths:
                continue
            candidates = git(
                "rev-list", "--no-merges", "-n", str(MAX_PATCH_ID_CANDIDATES), self.default_ref,
                "--not", f"{commit}^", "--", *paths, check=False
            ).split()
            if any(self._patch_id(candidate) == wanted for candidate in candidates):
                landed.add(commit)
        return landed

    def _patch_id(self, commit: str) -> str:
        if commit not in self._patch_ids:
            diff = git("diff", "--no-color", f"{commit}^", commit, check=False)
            out = sh(["git", "patch-id", "--stable"], stdin=diff, check=False).split() if diff else []
            self._patch_ids[commit] = out[0] if out else ""
        return self._patch_ids[commit]

    def _tree(self, ref: str) -> dict[str, str]:
        """path -> blob id for the whole tree at ``ref``; one git call, cached."""
        if ref not in self._tree_cache:
            entries: dict[str, str] = {}
            for line in git("ls-tree", "-r", ref).splitlines():
                meta, _, path = line.partition("\t")
                fields = meta.split()
                if len(fields) >= 3 and fields[1] == "blob":
                    entries[unquote_diff_path(path)] = fields[2]
            self._tree_cache[ref] = entries
        return self._tree_cache[ref]

    def _show(self, ref: str, path: str) -> str:
        return git("show", f"{ref}:{path}", check=False)

    def _paths_touched_since(self, paths: Sequence[str], merged_at: str | None) -> set[str]:
        """Payload paths the default branch has changed since the merge; one git call."""
        if not merged_at or not paths:
            return set()
        out = git(
            "log", f"--since={merged_at}", "--name-only", "--format=", self.default_ref, "--", *paths, check=False
        )
        return {unquote_diff_path(line.strip()) for line in out.splitlines() if line.strip()}

    # -- open-PR facts ----------------------------------------------------- #

    def base_state(self, base: str) -> tuple[bool | None, bool | None, bool | None]:
        """(base exists, base has been merged, base tip is an ancestor of default)."""
        if base not in self._base_state_cache:
            self._base_state_cache[base] = self._resolve_base_state(base)
        return self._base_state_cache[base]

    def _resolve_base_state(self, base: str) -> tuple[bool | None, bool | None, bool | None]:
        exists: bool | None
        ancestor: bool | None = None
        local = self._heads().get(base)
        if local:
            exists = True
            ancestor = git_ok("merge-base", "--is-ancestor", local, self.default_ref)
        else:
            # The checkout is credential-free, so `git ls-remote` would run
            # unauthenticated and 401 on a private repository. Ask the API with
            # the workflow token instead, and treat any failure as unknown.
            exists = self._branch_exists_via_api(base)
            if exists is None:
                return None, None, None
            if not exists:
                return False, None, None
        return exists, self._base_merged(base), ancestor

    def _heads(self) -> dict[str, str]:
        """Local remote-tracking branches. No network; fetch-depth: 0 populates these."""
        if self._local_heads is None:
            self._local_heads = {}
            prefix = f"refs/remotes/{self.remote}/"
            out = git("for-each-ref", "--format=%(refname) %(objectname)", prefix, check=False)
            for line in out.splitlines():
                name, _, oid = line.partition(" ")
                if name.startswith(prefix) and oid:
                    short = name[len(prefix) :]
                    if short != "HEAD":
                        self._local_heads[short] = oid.strip()
        return self._local_heads

    def _branch_exists_via_api(self, base: str) -> bool | None:
        slug = self.repo or "{owner}/{repo}"
        result = _run(["gh", "api", "-i", f"repos/{slug}/branches/{base}"])
        if result.returncode == 0:
            return True
        if "HTTP 404" in result.stdout or "HTTP 404" in result.stderr:
            return False
        return None

    def _base_merged(self, base: str) -> bool | None:
        """True when a PR whose head is ``base`` has been merged.

        Deliberately not "is an ancestor of the default branch": a stack base
        created or reset from the default branch is an ancestor while being alive.
        """
        raw = self._gh("pr", "list", "--head", base, "--state", "merged", "--limit", "1", "--json", "number",
                       check=False)
        if not raw:
            return None
        try:
            return bool(json.loads(raw))
        except json.JSONDecodeError:
            return None


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

    def adjudicate(self, pr: dict[str, Any], merge_commit: str) -> tuple[list[dict[str, Any]], str | None]:
        return list(pr.get("paths") or []), pr.get("indeterminateReason")

    def base_state(self, base: str) -> tuple[bool | None, bool | None, bool | None]:
        state = (self.fixture.get("bases") or {}).get(base)
        if state is None:
            # A base the fixture forgot to record is unknown, not healthy.
            return True, None, None
        return state.get("exists", True), state.get("merged"), state.get("ancestor")


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
    dispositions: dict[int, dict[str, Any]] | None = None,
    today: dt.date | None = None,
) -> dict[str, Any]:
    """Run both passes and return the findings worth showing a human."""
    dispositions = dispositions or {}
    today = today or dt.date.today()
    merged_prs = resolver.merged_prs(merged_limit)
    merged_findings: list[dict[str, Any]] = []
    for pr in merged_prs:
        merge_commit = _merge_oid(pr)
        if not merge_commit or ignored(_base_ref(pr), ignored_prefixes):
            # A PR whose merge commit GitHub no longer reports (garbage-collected,
            # or a null payload) cannot be adjudicated and is not evidence of loss.
            continue
        on_default = resolver.on_default_branch(merge_commit)
        verdicts, reason = ((), None) if on_default else resolver.adjudicate(pr, merge_commit)
        finding = classify_merged_pr(
            pr,
            on_default_branch=on_default,
            path_verdicts=verdicts,
            indeterminate_reason=reason,
        )
        if finding["classification"] != MERGED_ON_DEFAULT:
            merged_findings.append(apply_disposition(finding, dispositions.get(finding["number"]), today=today))

    open_prs: list[dict[str, Any]] = resolver.open_prs(open_limit) if include_open else []
    open_findings: list[dict[str, Any]] = []
    for pr in open_prs:
        base = _base_ref(pr)
        if base == default_branch or ignored(base, ignored_prefixes):
            continue
        exists, merged, ancestor = resolver.base_state(base)
        finding = classify_open_pr(
            pr,
            default_branch=default_branch,
            base_exists=exists,
            base_merged=merged,
            base_is_ancestor=ancestor,
            ignored_prefixes=ignored_prefixes,
        )
        if finding["classification"] != OPEN_ON_DEFAULT:
            open_findings.append(finding)

    # A decision about a PR the sweep no longer reports has outlived its subject:
    # the payload re-landed, or the PR aged past --limit. Either way the entry is
    # now silencing nothing, and saying so is what stops the ledger accumulating
    # waivers nobody can account for.
    reported = {f["number"] for f in merged_findings if f.get("disposition")}
    stale = [
        {"pr": number, "status": entry["status"], "reason": entry["reason"]}
        for number, entry in sorted(dispositions.items())
        if number not in reported
    ]

    return {
        "merged": merged_findings,
        "open": open_findings,
        "staleDispositions": stale,
        "scope": {
            "mergedExamined": len(merged_prs),
            "mergedLimit": merged_limit,
            "mergedTruncated": len(merged_prs) >= merged_limit,
            "openIncluded": include_open,
            "openExamined": len(open_prs),
            "openLimit": open_limit,
            "openTruncated": include_open and len(open_prs) >= open_limit,
        },
    }


def silenced(finding: dict[str, Any]) -> bool:
    return bool((finding.get("disposition") or {}).get("silences"))


def actionable(result: dict[str, Any]) -> list[dict[str, Any]]:
    return [
        f
        for key in ("merged", "open")
        for f in (result.get(key) or [])
        if f["classification"] in ACTIONABLE_CLASSIFICATIONS and not silenced(f)
    ]


# --------------------------------------------------------------------------- #
# Reporting
# --------------------------------------------------------------------------- #


def _of(findings: Sequence[dict[str, Any]], classification: str) -> list[dict[str, Any]]:
    return [f for f in findings if f["classification"] == classification]


def _paths_cell(paths: Sequence[str], limit: int = 5) -> str:
    cell = ", ".join(f"`{p}`" for p in paths[:limit])
    if len(paths) > limit:
        cell += f" (+{len(paths) - limit} more)"
    return cell or "—"


def _disposition_notes(findings: Sequence[dict[str, Any]]) -> list[str]:
    """Bullets for rows that carry a recorded decision which did *not* silence them.

    Printed under the table rather than as another column so the reason survives
    at full length -- "why this is still here despite a decision" is the sentence
    that stops the next person re-adjudicating it from scratch.
    """
    lines: list[str] = []
    for f in findings:
        record = f.get("disposition")
        if not record or record.get("silences"):
            continue
        note = f"- #{f['number']} has a recorded `{record['status']}` disposition that does not close it: {record.get('why', '')}"
        if record.get("uncoveredPaths"):
            note += f" ({_paths_cell(record['uncoveredPaths'], limit=3)})"
        note += "."
        if record.get("recovery"):
            note += f" Recovery: {record['recovery']}"
        lines.append(note)
    return [*lines, ""] if lines else []


def _scope_sentence(scope: dict[str, Any], default_ref: str) -> str:
    merged_examined = scope.get("mergedExamined", 0)
    merged_part = f"{merged_examined} merged PR{'s' if merged_examined != 1 else ''}"
    if scope.get("mergedTruncated"):
        merged_part += f" (the `--limit {scope.get('mergedLimit')}` cap was reached; older PRs were not examined)"
    if not scope.get("openIncluded"):
        open_part = "the open-PR pass was skipped"
    else:
        open_examined = scope.get("openExamined", 0)
        open_part = f"{open_examined} open PR{'s' if open_examined != 1 else ''}"
        if scope.get("openTruncated"):
            open_part += f" (the `--open-limit {scope.get('openLimit')}` cap was reached)"
    return f"Swept {merged_part} and {open_part} against `{default_ref}`."


def render_markdown(result: dict[str, Any], default_ref: str) -> str:
    merged = result.get("merged") or []
    opened = result.get("open") or []
    scope = result.get("scope") or {}
    adjudicated = [f for f in merged if silenced(f)]
    undecided = [f for f in merged if not silenced(f)]
    stale_dispositions = result.get("staleDispositions") or []
    stranded = _of(undecided, MERGED_STRANDED)
    edits = _of(undecided, MERGED_EDITS_MISSING)
    unknown_payload = _of(undecided, MERGED_INDETERMINATE)
    superseded = _of(undecided, MERGED_SUPERSEDED)
    landed = _of(undecided, MERGED_LANDED)
    retarget = _of(opened, OPEN_NEEDS_RETARGET)
    live = _of(opened, OPEN_LIVE_BASE)
    unknown_base = _of(opened, OPEN_UNKNOWN_BASE)

    lines = [_scope_sentence(scope, default_ref), ""]

    if not (stranded or edits or unknown_payload or retarget):
        lines += ["**Nothing actionable.** No merged PR is missing payload and no open PR needs a re-target.", ""]

    if retarget:
        lines += [
            f"### Needs re-target ({len(retarget)}) -- open, preventable",
            "",
            "These are open against a base branch that has **already been merged or deleted**. Merging",
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
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{f['mergeCommit'][:9]}` | {_paths_cell(f['absentPaths'])} |"
            )
        lines.append("")
        lines += _disposition_notes(stranded)

    if edits:
        lines += [
            f"### Edits missing ({len(edits)}) -- merged, files present but changes are not",
            "",
            "Every file exists on the default branch, no payload commit has a patch-id equivalent there,",
            "and for the paths listed **none of the lines this PR added are present**. Heuristic, not",
            "proof -- a line re-worded or moved during a re-land reads as missing.",
            "",
            "| PR | base | merge commit | paths whose edits are absent |",
            "|---|---|---|---|",
        ]
        for f in edits:
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{f['mergeCommit'][:9]}` | {_paths_cell(f['unlandedEditPaths'])} |"
            )
        lines.append("")
        lines += _disposition_notes(edits)

    if unknown_payload:
        lines += [
            f"### Could not be adjudicated ({len(unknown_payload)})",
            "",
            "The sweep could not establish whether the payload landed. This is **not** a clean bill of",
            "health -- the usual cause is a stack base that was deleted or force-pushed, which is exactly",
            "when work goes missing. Check these by hand.",
            "",
            "| PR | base | merge commit | why |",
            "|---|---|---|---|",
        ]
        for f in unknown_payload:
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{(f['mergeCommit'] or '?')[:9]}` | {f.get('reason', '')} |"
            )
        lines.append("")

    if adjudicated:
        lines += [
            f"### Adjudicated ({len(adjudicated)}) -- real findings, decision already recorded",
            "",
            "These payloads genuinely never reached the default branch, and they never will: someone",
            f"looked and wrote the decision down in `{DEFAULT_DISPOSITIONS_PATH}`. They are listed so the",
            "sweep stays honest about what is missing, and excluded from the actionable count so the",
            "weekly issue is not mostly settled history. A decision covers only the paths it names --",
            "a new actionable path puts the finding straight back in the tables above.",
            "",
            "| PR | decision | decided | why |",
            "|---|---|---|---|",
        ]
        for f in adjudicated:
            record = f["disposition"]
            decided = record.get("decidedOn") or "—"
            if record.get("decidedBy"):
                decided += f" ({record['decidedBy']})"
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{record['status']}` | {decided} "
                f"| {record['reason']} |"
            )
        lines.append("")

    if stale_dispositions:
        lines += [
            f"### Dispositions with nothing left to decide ({len(stale_dispositions)})",
            "",
            "Recorded against a PR this sweep does not report. Either the payload reached the default",
            "branch after all, or the PR aged past `--limit`. Prune the entry once you know which.",
            "",
        ]
        for entry in stale_dispositions:
            lines.append(f"- #{entry['pr']} — `{entry['status']}` — {entry['reason']}")
        lines.append("")

    if superseded or landed or live or unknown_base:
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
                "branch has part of the change and has edited those paths since, so the remaining "
                "difference is the branch moving on rather than payload loss."
            )
        for f in live:
            lines.append(
                f"- **stacked on a live base** -- [#{f['number']}]({f['url']}) is open against `{f['base']}`: "
                f"{f.get('reason', '')}."
            )
        for f in unknown_base:
            lines.append(
                f"- **unresolved base** -- [#{f['number']}]({f['url']}) is open against `{f['base']}`: "
                f"{f.get('reason', '')}. Not judged either way."
            )
        lines.append("")

    lines += [
        "_Method: candidates come from `git merge-base --is-ancestor`, then every candidate is adjudicated "
        "by content -- exact patch identity (`git patch-id --stable`) first, then blob equality, then presence of the "
        "PR's added lines on the default branch. Commit identity alone produced three false positives out "
        "of four on the first #3248 sweep._",
    ]
    return "\n".join(lines)


def to_json_document(result: dict[str, Any], default_ref: str) -> dict[str, Any]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "defaultRef": default_ref,
        "scope": result.get("scope") or {},
        "findings": result.get("merged") or [],
        "openFindings": result.get("open") or [],
        "staleDispositions": result.get("staleDispositions") or [],
        "actionable": len(actionable(result)),
    }


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
        "--from-json",
        help="render the markdown report from a previously produced --json document instead of sweeping again",
    )
    parser.add_argument(
        "--ignore-base-prefix",
        action="append",
        default=None,
        help=f"base-branch prefix to skip; repeatable (default: {', '.join(DEFAULT_IGNORED_BASE_PREFIXES)})",
    )
    parser.add_argument(
        "--dispositions",
        default=None,
        help=f"recorded decisions that silence settled findings (default: {DEFAULT_DISPOSITIONS_PATH} "
        "relative to the working directory, skipped when absent)",
    )
    parser.add_argument(
        "--no-dispositions",
        action="store_true",
        help="ignore the disposition ledger and report every finding",
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

    if args.from_json:
        with open(args.from_json, encoding="utf-8") as handle:
            document = json.load(handle)
        result = {
            "merged": document.get("findings") or [],
            "open": document.get("openFindings") or [],
            "staleDispositions": document.get("staleDispositions") or [],
            "scope": document.get("scope") or {},
        }
        print(render_markdown(result, document.get("defaultRef", default_ref)))
        return _exit_code(args.fail_on, result)

    if args.no_dispositions:
        dispositions: dict[int, dict[str, Any]] = {}
    else:
        # An explicit path that does not exist is the caller's mistake; the
        # default one missing just means this repository has recorded nothing.
        try:
            dispositions = load_dispositions(
                args.dispositions or DEFAULT_DISPOSITIONS_PATH, required=args.dispositions is not None
            )
        except DispositionError as error:
            print(f"error: {error}", file=sys.stderr)
            return 2

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
        dispositions=dispositions,
    )

    if args.json:
        print(json.dumps(to_json_document(result, default_ref), indent=2))
    else:
        print(render_markdown(result, default_ref))
    return _exit_code(args.fail_on, result)


def _exit_code(fail_on: str, result: dict[str, Any]) -> int:
    every = (result.get("merged") or []) + (result.get("open") or [])
    if fail_on == "any" and every:
        return 1
    if fail_on == "actionable" and actionable(result):
        return 1
    if fail_on in ("stranded", "payload-missing") and [
        f for f in _of(every, MERGED_STRANDED) if not silenced(f)
    ]:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
