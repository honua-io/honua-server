#!/usr/bin/env python3
"""Detect merged PRs whose merge commit never reached the default branch (#3248).

A PR opened against a *stack base* rather than the default branch is merged into
that base. GitHub then reports the PR as MERGED, CI was green, and the linked
issue often gets closed -- but if the base branch itself is never merged, the
payload sits on a branch nobody is looking at. The failure is silent by
construction, which is why it needs a machine and not a periodic manual sweep.

The detection is exact and cheap::

    git merge-base --is-ancestor <mergeCommit> origin/<default>   # non-zero => stranded

**A stranded merge commit does not imply stranded content.** This is the lesson
from the first sweep (honua-server#3248, honua-sdk-js#1317): of four stranded
merges found across two repos, three had their payload re-land on the default
branch through a later PR. The cost was paid as duplicated re-implementation
effort rather than as a missing feature. A detector that files "payload lost" on
every non-ancestor merge would therefore have been wrong three times out of four.

So this script does two passes. The ancestor test finds *candidates*; a
content pass then asks the question that actually matters -- are any of the
PR's files absent from the default branch? -- and only candidates that fail
that second test are reported as ``payload-missing``. The rest are reported as
``content-present``: worth a glance, not worth an issue.

**Known limit: this detects missing FILES, not reverted EDITS.** The content
pass tests path existence, so a PR that only *modified* existing files -- and
whose modifications were later reverted or lost in a bad conflict resolution --
has every path present and classifies as ``content-present``. Silence. That is
the higher-risk case, because a lost modification is far harder to spot by eye
than a missing file.

Closing it is not a one-line change and is deliberately out of scope. A
stranded PR's diff is computed against its own base, so on a diverged stack
base the file list is inflated with unrelated drift (honua-server#3113 reports
100 files for what its issue describes as a 24-file change). Blob-comparing
those against the default branch yields dozens of "differences" that are mostly
just the default branch moving forward. Telling "the branch lacks this PR's
change" apart from "the branch moved past it" needs three-way logic against the
merge base -- a different and much larger tool.

Examples::

    # human-readable sweep of the last 250 merged PRs
    scripts/ci/detect-stranded-merges.py --limit 250

    # CI use: machine-readable, non-zero exit only when payload is actually missing
    scripts/ci/detect-stranded-merges.py --json --fail-on payload-missing
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

CLASSIFICATION_ON_TRUNK = "on-default-branch"
CLASSIFICATION_CONTENT_PRESENT = "content-present"
CLASSIFICATION_PAYLOAD_MISSING = "payload-missing"


def run(argv: Sequence[str], *, check: bool = True) -> str:
    """Run a command and return stdout, raising on failure when ``check``."""
    result = subprocess.run(argv, capture_output=True, text=True, check=False)
    if check and result.returncode != 0:
        raise RuntimeError(f"{' '.join(argv)} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout


def is_ancestor(commit: str, ref: str) -> bool:
    """True when ``commit`` is reachable from ``ref``."""
    result = subprocess.run(
        ["git", "merge-base", "--is-ancestor", commit, ref],
        capture_output=True,
        text=True,
        check=False,
    )
    return result.returncode == 0


def path_exists_on(ref: str, path: str) -> bool:
    """True when ``path`` exists in the tree at ``ref``."""
    result = subprocess.run(
        ["git", "cat-file", "-e", f"{ref}:{path}"],
        capture_output=True,
        text=True,
        check=False,
    )
    return result.returncode == 0


def fetch_merged_prs(repo: str | None, limit: int) -> list[dict[str, Any]]:
    """Merged PRs, newest first, with the fields the sweep needs."""
    argv = [
        "gh",
        "pr",
        "list",
        "--state",
        "merged",
        "--limit",
        str(limit),
        "--json",
        "number,title,baseRefName,mergeCommit,mergedAt,url",
    ]
    if repo:
        argv += ["--repo", repo]
    return json.loads(run(argv) or "[]")


def fetch_pr_paths(repo: str | None, number: int) -> list[str]:
    """Paths the PR touched, excluding ones it deleted.

    A deleted path is *supposed* to be absent from the default branch, so
    counting it as missing payload would report every cleanup PR as a loss.
    """
    argv = ["gh", "pr", "view", str(number), "--json", "files"]
    if repo:
        argv += ["--repo", repo]
    files = json.loads(run(argv) or "{}").get("files") or []
    return [f["path"] for f in files if not _is_pure_deletion(f)]


def _is_pure_deletion(entry: dict[str, Any]) -> bool:
    # gh reports additions/deletions per file; a pure deletion adds nothing.
    return int(entry.get("additions") or 0) == 0 and int(entry.get("deletions") or 0) > 0


def ignored(base: str, prefixes: Iterable[str]) -> bool:
    return any(base.startswith(prefix) for prefix in prefixes)


def classify(
    pr: dict[str, Any],
    default_ref: str,
    repo: str | None,
    *,
    ignored_prefixes: Iterable[str],
) -> dict[str, Any] | None:
    """Classify one merged PR, or None when it is not a candidate at all."""
    base = pr.get("baseRefName") or ""
    merge_commit = (pr.get("mergeCommit") or {}).get("oid")
    if not merge_commit or ignored(base, ignored_prefixes):
        return None

    finding = {
        "number": pr["number"],
        "title": pr.get("title", ""),
        "url": pr.get("url", ""),
        "base": base,
        "mergeCommit": merge_commit,
        "mergedAt": pr.get("mergedAt"),
    }

    if is_ancestor(merge_commit, default_ref):
        finding["classification"] = CLASSIFICATION_ON_TRUNK
        return finding

    # Candidate. Now the question that matters: is any of it actually missing?
    absent = [path for path in fetch_pr_paths(repo, pr["number"]) if not path_exists_on(default_ref, path)]
    finding["absentPaths"] = absent
    finding["classification"] = CLASSIFICATION_PAYLOAD_MISSING if absent else CLASSIFICATION_CONTENT_PRESENT
    return finding


def sweep(
    *,
    repo: str | None,
    default_ref: str,
    limit: int,
    ignored_prefixes: Iterable[str],
) -> list[dict[str, Any]]:
    findings = []
    for pr in fetch_merged_prs(repo, limit):
        finding = classify(pr, default_ref, repo, ignored_prefixes=ignored_prefixes)
        if finding and finding["classification"] != CLASSIFICATION_ON_TRUNK:
            findings.append(finding)
    return findings


def render_markdown(findings: Sequence[dict[str, Any]], default_ref: str, limit: int) -> str:
    missing = [f for f in findings if f["classification"] == CLASSIFICATION_PAYLOAD_MISSING]
    present = [f for f in findings if f["classification"] == CLASSIFICATION_CONTENT_PRESENT]

    lines = [
        f"Swept the {limit} most recent merged PRs against `{default_ref}`.",
        "",
        "_Scope: this detects missing **files**, not reverted **edits**. A PR that only modified "
        "existing files, whose changes were later reverted or lost in a conflict resolution, has "
        "every path present and will not appear here._",
        "",
    ]
    if not findings:
        lines.append("No merged PR has a merge commit outside the default branch. Nothing to do.")
        return "\n".join(lines)

    if missing:
        lines += [
            f"### Payload missing ({len(missing)})",
            "",
            "These merged into a branch that never reached the default branch, **and** files they",
            "added are still absent. This is reviewed, tested work that is not in the product.",
            "",
            "| PR | base | merge commit | files absent |",
            "|---|---|---|---:|",
        ]
        for f in missing:
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` "
                f"| `{f['mergeCommit'][:9]}` | {len(f['absentPaths'])} |"
            )
        lines.append("")

    if present:
        lines += [
            f"### Stranded merge, content present ({len(present)})",
            "",
            "The merge commit is not an ancestor of the default branch, but every file the PR",
            "added is present -- the work re-landed through another PR. Informational: the cost",
            "was duplicated effort, not a missing feature. No recovery needed.",
            "",
            "| PR | base | merge commit |",
            "|---|---|---|",
        ]
        for f in present:
            lines.append(
                f"| [#{f['number']}]({f['url']}) {f['title']} | `{f['base']}` | `{f['mergeCommit'][:9]}` |"
            )
        lines.append("")

    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", help="owner/name; defaults to the repository gh resolves in cwd")
    parser.add_argument("--default-branch", default="trunk", help="default branch name (default: trunk)")
    parser.add_argument("--remote", default="origin", help="remote holding the default branch (default: origin)")
    parser.add_argument("--limit", type=int, default=250, help="how many merged PRs to sweep (default: 250)")
    parser.add_argument(
        "--ignore-base-prefix",
        action="append",
        default=None,
        help=f"base-branch prefix to skip; repeatable (default: {', '.join(DEFAULT_IGNORED_BASE_PREFIXES)})",
    )
    parser.add_argument("--json", action="store_true", help="emit findings as JSON instead of markdown")
    parser.add_argument(
        "--fail-on",
        choices=["never", "payload-missing", "any"],
        default="never",
        help="exit non-zero when findings of this severity exist (default: never)",
    )
    args = parser.parse_args()

    prefixes = args.ignore_base_prefix if args.ignore_base_prefix is not None else list(DEFAULT_IGNORED_BASE_PREFIXES)
    default_ref = f"{args.remote}/{args.default_branch}"

    findings = sweep(repo=args.repo, default_ref=default_ref, limit=args.limit, ignored_prefixes=prefixes)

    if args.json:
        print(json.dumps({"defaultRef": default_ref, "limit": args.limit, "findings": findings}, indent=2))
    else:
        print(render_markdown(findings, default_ref, args.limit))

    if args.fail_on == "any" and findings:
        return 1
    if args.fail_on == "payload-missing" and any(
        f["classification"] == CLASSIFICATION_PAYLOAD_MISSING for f in findings
    ):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
