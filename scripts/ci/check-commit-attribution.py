#!/usr/bin/env python3
"""Fail when commits or a PR body carry AI/agent attribution.

Fleet rule 1 (AGENTS.md "Fleet rules"; source: honua-flow agents/fleet-rules.md):
commits are authored by the repo owner and carry no `Co-Authored-By:` trailer naming
an AI tool and no "Generated with Claude Code"/"Generated with Codex" line. Agent
harnesses add these by default, so the prose rule is also enforced here.

    check-commit-attribution.py --pr N [--repo owner/name]   PR commits via `gh api`,
                                                              plus $PR_BODY when set
    check-commit-attribution.py --range BASE..HEAD           local commits
    check-commit-attribution.py --message-file PATH          one message (commit-msg hook)
    check-commit-attribution.py --self-test
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

AI_NAMES = re.compile(
    r"\b(claude|codex|copilot|cursor|gemini|grok|devin|aider|chatgpt|gpt-?\d[\w.-]*|openai|anthropic)\b"
    r"|@(?:[\w.-]+\.)?(?:anthropic|openai)\.com\b",
    re.IGNORECASE,
)
CO_AUTHOR = re.compile(r"^\s*co-authored-by:\s*(?P<who>.+?)\s*$", re.IGNORECASE | re.MULTILINE)
GENERATED = re.compile(r"generated\s+(?:with|by)\s+\[?\s*(?:claude(?:\s+code)?|codex)\b", re.IGNORECASE)


def findings(text: str) -> list[str]:
    """Return one human-readable finding per offending line."""
    out = []
    for match in CO_AUTHOR.finditer(text or ""):
        if AI_NAMES.search(match.group("who")):
            out.append(f"AI co-author trailer: {match.group(0).strip()}")
    for line in (text or "").splitlines():
        if GENERATED.search(line):
            out.append(f"generated-with line: {line.strip()}")
    return out


def pr_messages(pr: str, repo: str | None) -> list[tuple[str, str]]:
    repo = repo or os.environ.get("GITHUB_REPOSITORY") or "{owner}/{repo}"
    raw = subprocess.run(
        ["gh", "api", "--paginate", f"repos/{repo}/pulls/{pr}/commits?per_page=100",
         "--jq", ".[] | {sha: .sha, message: .commit.message}"],
        check=True, capture_output=True, text=True,
    ).stdout
    return [(c["sha"][:12], c["message"]) for c in map(json.loads, filter(None, raw.splitlines()))]


def range_messages(rev_range: str) -> list[tuple[str, str]]:
    raw = subprocess.run(
        ["git", "log", "--format=%H%x00%B%x1e", rev_range], check=True, capture_output=True, text=True
    ).stdout
    commits = []
    for record in filter(str.strip, raw.split("\x1e")):
        sha, _, message = record.strip("\n").partition("\x00")
        commits.append((sha[:12], message))
    return commits


def self_test() -> int:
    bad = [
        "fix: x\n\nCo-Authored-By: Claude <noreply@anthropic.com>",
        "fix: x\n\nCo-authored-by: Claude Opus 4 <noreply@anthropic.com>",
        "fix: x\n\nco-authored-by: Codex <codex@openai.com>",
        "fix: x\n\nCo-Authored-By: GitHub Copilot <copilot@github.com>",
        "fix: x\n\n\U0001F916 Generated with [Claude Code](https://claude.com/claude-code)",
        "fix: x\n\nGenerated with Codex",
        "## Summary\n\nGenerated with Claude Code",
    ]
    good = [
        "fix: x\n\nCo-Authored-By: Jane Doe <jane@example.com>",
        "fix: x\n\nCo-authored-by: Sam <12345+sam@users.noreply.github.com>",
        "docs: explain the claude-review workflow and Codex lanes",
        "docs: the attribution check rejects 'Generated with' lines from agents",
        "fix(ci): codegen generated with protoc",
    ]
    failed = [m for m in bad if not findings(m)] + [m for m in good if findings(m)]
    for message in failed:
        print(f"self-test FAILED on: {message!r}", file=sys.stderr)
    print(f"self-test: {len(bad) + len(good) - len(failed)}/{len(bad) + len(good)} cases pass")
    return 1 if failed else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--pr", help="pull request number; also checks $PR_BODY when set")
    source.add_argument("--range", dest="rev_range", help="git revision range, e.g. origin/trunk..HEAD")
    source.add_argument("--message-file", help="a single commit message file")
    source.add_argument("--self-test", action="store_true")
    parser.add_argument("--repo", help="owner/name for --pr (default: $GITHUB_REPOSITORY or the current repo)")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()
    if args.pr:
        items = pr_messages(args.pr, args.repo)
        body = os.environ.get("PR_BODY")
        if body:
            items.append(("PR body", body))
    elif args.rev_range:
        items = range_messages(args.rev_range)
    else:
        with open(args.message_file, encoding="utf-8") as handle:
            items = [("message", handle.read())]

    problems = [(where, f) for where, text in items for f in findings(text)]
    for where, finding in problems:
        print(f"::error::{where}: {finding}")
    if problems:
        print(
            "Commits and PR bodies must carry no AI/agent attribution (AGENTS.md, Fleet rules, rule 1). "
            "Reword the commits (git rebase, then drop the lines) and edit the PR body.",
            file=sys.stderr,
        )
        return 1
    print(f"No AI/agent attribution in {len(items)} commit message(s)/body.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
