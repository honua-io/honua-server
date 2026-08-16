#!/usr/bin/env python3
"""Guard (#3248): a MERGED pull request whose merge commit never became an
ancestor of `trunk` shipped nothing.

Failure mode this detects
-------------------------
A PR that targets a **stack base branch** instead of `trunk` is merged into that
base branch. GitHub marks it MERGED, the linked issue often auto-closes, and CI
was green — but if the base branch itself never lands, the payload is not in the
product and nothing signals it. This happened at least three times in five weeks
(honua-server #3116, #3113, #2835 — roughly 3,800 insertions of reviewed work).

The whole test is one command per PR:

    git merge-base --is-ancestor <mergeCommitSha> origin/trunk

...which is exactly what this script runs, over the last N merged PRs. It also
looks at OPEN PRs stacked on a non-trunk base and reports the ones whose base
has already landed (or disappeared), because those are the next stranded PRs
unless someone re-targets them at `trunk`.

Usage
-----
    python3 scripts/ci/audit-merged-pr-landing.py                    # audit + exit 1 on findings
    python3 scripts/ci/audit-merged-pr-landing.py --limit 250
    python3 scripts/ci/audit-merged-pr-landing.py --json report.json
    python3 scripts/ci/audit-merged-pr-landing.py --warn-only        # always exit 0
    python3 scripts/ci/audit-merged-pr-landing.py --fixture prs.json # offline / tests

`--fixture` feeds the PR list from a JSON file instead of `gh`, so the script is
runnable and testable without network access. Ancestry is still resolved with
git unless the fixture supplies `landed` / `baseLanded` booleans.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = "honua-io/honua-server"
DEFAULT_TRUNK_REF = "origin/trunk"
# `gh pr list --state all` counts open + closed + merged toward the limit, so the
# window must be comfortably wider than the merged-PR depth we want to sweep.
# 400 reaches back past 2026-07-15 (PR #2835, the oldest known stranded case).
DEFAULT_LIMIT = 400

# Base branches that are an ordinary part of landing and must not be reported as
# a stack: `trunk` itself and the merge train's synthetic batch branches, which
# are fast-forwarded into trunk by .github/workflows/merge-train.yml.
TRAIN_BASE_PREFIX = "train/batch/"


def is_stack_base(base_ref: str, trunk_branch: str) -> bool:
    """True when `base_ref` is neither trunk nor a merge-train batch branch."""
    if base_ref == trunk_branch:
        return False
    return not base_ref.startswith(TRAIN_BASE_PREFIX)


def select_merged_prs(prs: list[dict]) -> list[dict]:
    """Merged PRs that have a merge commit we can test ancestry for."""
    selected = []
    for pr in prs:
        if pr.get("state", "").upper() == "OPEN":
            continue
        if not pr.get("mergedAt"):
            continue
        if not merge_commit_sha(pr):
            continue
        selected.append(pr)
    return selected


def select_open_prs(prs: list[dict]) -> list[dict]:
    return [pr for pr in prs if pr.get("state", "").upper() == "OPEN"]


def merge_commit_sha(pr: dict) -> str:
    commit = pr.get("mergeCommit") or {}
    if isinstance(commit, dict):
        return commit.get("oid") or ""
    return str(commit or "")


def classify_merged_pr(pr: dict, landed: bool, trunk_branch: str) -> dict:
    """Return one audit record for a merged PR.

    `stranded` is the finding: MERGED, but its merge commit is not an ancestor
    of trunk. A PR merged into a stack base that later landed is fine — the
    ancestry test, not the base branch, is the authority.
    """
    base_ref = pr.get("baseRefName", "")
    return {
        "number": pr.get("number"),
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "mergedAt": pr.get("mergedAt"),
        "baseRefName": base_ref,
        "mergeCommit": merge_commit_sha(pr),
        "stacked": is_stack_base(base_ref, trunk_branch),
        "landed": landed,
        "stranded": not landed,
    }


def classify_open_stack_pr(pr: dict, base_landed: bool, base_exists: bool,
                           trunk_branch: str) -> dict | None:
    """Return a record for an OPEN PR stacked on a non-trunk base, else None.

    `needs_retarget` means the base branch has already landed on trunk or no
    longer exists, so merging this PR into that base would strand it exactly
    the way #3116/#3113/#2835 were stranded. Re-target it at trunk.
    """
    base_ref = pr.get("baseRefName", "")
    if not is_stack_base(base_ref, trunk_branch):
        return None
    return {
        "number": pr.get("number"),
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "baseRefName": base_ref,
        "baseLanded": base_landed,
        "baseExists": base_exists,
        "needs_retarget": base_landed or not base_exists,
    }


def _run(cmd: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, check=False)


def git_is_ancestor(sha: str, trunk_ref: str, repo_root: Path) -> bool:
    """The one command from #3248: is <sha> an ancestor of trunk?"""
    if not sha:
        return False
    return _run(["git", "merge-base", "--is-ancestor", sha, trunk_ref],
                cwd=repo_root).returncode == 0


def git_ref_exists(ref: str, repo_root: Path) -> bool:
    return _run(["git", "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"],
                cwd=repo_root).returncode == 0


def fetch_prs(repo: str, limit: int) -> list[dict]:
    """Recently updated PRs (merged and open) via the gh CLI."""
    result = _run([
        "gh", "pr", "list", "--repo", repo, "--state", "all",
        "--limit", str(limit), "--json",
        "number,title,url,state,mergedAt,mergeCommit,baseRefName,headRefName,isDraft",
    ])
    if result.returncode != 0:
        raise RuntimeError(f"gh pr list failed: {result.stderr.strip()}")
    return json.loads(result.stdout or "[]")


def audit(prs: list[dict], trunk_ref: str, repo_root: Path) -> dict:
    trunk_branch = trunk_ref.split("/", 1)[-1] if "/" in trunk_ref else trunk_ref

    merged_records = []
    for pr in select_merged_prs(prs):
        if "landed" in pr:
            landed = bool(pr["landed"])
        else:
            landed = git_is_ancestor(merge_commit_sha(pr), trunk_ref, repo_root)
        merged_records.append(classify_merged_pr(pr, landed, trunk_branch))

    open_records = []
    for pr in select_open_prs(prs):
        base_ref = pr.get("baseRefName", "")
        if not is_stack_base(base_ref, trunk_branch):
            continue
        if "baseLanded" in pr:
            base_landed = bool(pr["baseLanded"])
            base_exists = bool(pr.get("baseExists", True))
        else:
            remote_base = f"origin/{base_ref}"
            base_exists = git_ref_exists(remote_base, repo_root)
            base_landed = (git_is_ancestor(remote_base, trunk_ref, repo_root)
                           if base_exists else False)
        record = classify_open_stack_pr(pr, base_landed, base_exists, trunk_branch)
        if record is not None:
            open_records.append(record)

    return {
        "trunkRef": trunk_ref,
        "auditedMerged": len(merged_records),
        "stranded": [r for r in merged_records if r["stranded"]],
        "openStacked": open_records,
        "needsRetarget": [r for r in open_records if r["needs_retarget"]],
    }


def render_report(report: dict) -> str:
    lines: list[str] = []
    stranded = report["stranded"]
    retarget = report["needsRetarget"]
    lines.append(
        f"Audited {report['auditedMerged']} merged PR(s) against "
        f"{report['trunkRef']}."
    )
    if stranded:
        lines.append("")
        lines.append(f"STRANDED — {len(stranded)} merged PR(s) whose merge commit is "
                     f"NOT an ancestor of {report['trunkRef']}:")
        lines.append("")
        lines.append("| PR | merged | base branch | merge commit |")
        lines.append("|---|---|---|---|")
        for r in stranded:
            lines.append(f"| #{r['number']} | {r['mergedAt'] or '?'} | "
                         f"`{r['baseRefName']}` | `{r['mergeCommit'][:9]}` |")
        lines.append("")
        lines.append("Verify any single row with the authoritative test:")
        lines.append("")
        lines.append("```")
        for r in stranded:
            lines.append(f"git merge-base --is-ancestor {r['mergeCommit']} "
                         f"{report['trunkRef']}   # PR #{r['number']}")
        lines.append("```")
    else:
        lines.append("No stranded merged PRs: every merge commit is an ancestor of "
                     f"{report['trunkRef']}.")

    if retarget:
        lines.append("")
        lines.append(f"RE-TARGET — {len(retarget)} open PR(s) stacked on a base that "
                     "has already landed or no longer exists. Merging them as-is "
                     "would strand the payload; point them at trunk:")
        lines.append("")
        for r in retarget:
            lines.append(f"- #{r['number']} (base `{r['baseRefName']}`) — "
                         f"`gh pr edit {r['number']} --base trunk`")
    elif report["openStacked"]:
        lines.append("")
        lines.append(f"{len(report['openStacked'])} open PR(s) are stacked on a "
                     "not-yet-landed base; nothing to do until the base merges.")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", default=DEFAULT_REPO)
    parser.add_argument("--trunk-ref", default=DEFAULT_TRUNK_REF)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT,
                        help="how many recent PRs to sweep (default 250)")
    parser.add_argument("--repo-root", default=".",
                        help="git working tree used for the ancestry tests")
    parser.add_argument("--fixture",
                        help="read the PR list from this JSON file instead of gh")
    parser.add_argument("--json", dest="json_out",
                        help="write the machine-readable report here")
    parser.add_argument("--warn-only", action="store_true",
                        help="report findings but always exit 0")
    args = parser.parse_args(argv)

    if args.fixture:
        prs = json.loads(Path(args.fixture).read_text(encoding="utf-8"))
    else:
        prs = fetch_prs(args.repo, args.limit)

    report = audit(prs, args.trunk_ref, Path(args.repo_root))
    text = render_report(report)
    print(text)

    if args.json_out:
        Path(args.json_out).write_text(json.dumps(report, indent=2) + "\n",
                                       encoding="utf-8")

    findings = len(report["stranded"]) + len(report["needsRetarget"])
    if findings and not args.warn_only:
        print(f"::error::{len(report['stranded'])} stranded merged PR(s) and "
              f"{len(report['needsRetarget'])} open PR(s) needing re-target "
              "(#3248)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
