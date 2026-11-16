#!/usr/bin/env python3
"""Fail when a workflow disabled on GitHub is still tracked in .github/workflows.

A disabled workflow never runs on its own triggers, so its file only misleads readers.
A disabled file is allowed while an active workflow calls it with `uses:`, because
workflow_call ignores the disabled state, or while it has an unexpired entry below.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ".github/workflows"
CALL = re.compile(r"^\s*(?:-\s*)?uses:\s*['\"]?\./(\.github/workflows/[\w.-]+\.ya?ml)", re.MULTILINE)

# Disabled workflows that stay until their owning ticket decides them.
ALLOWED = {
    ".github/workflows/native-image-impact-observe.yml": ("#7880", "2026-12-31"),
    ".github/workflows/server-test-prebuild-observe.yml": ("#7880", "2026-12-31"),
    ".github/workflows/lambda-preview-certification.yml": ("#7881", "2026-12-31"),
    ".github/workflows/real-aws-certification.yml": ("#7881", "2026-12-31"),
}


def tracked_workflows(root: Path) -> dict[str, str]:
    return {f"{WORKFLOWS}/{path.name}": path.read_text(encoding="utf-8")
            for path in sorted((root / WORKFLOWS).glob("*.y*ml"))}


def reachable_from_active(workflows: dict[str, str], disabled: set[str]) -> set[str]:
    reached = set(workflows) - disabled
    pending = list(reached)
    while pending:
        for target in CALL.findall(workflows.get(pending.pop(), "")):
            if target not in reached:
                reached.add(target)
                pending.append(target)
    return reached


def violations(workflows: dict[str, str], states: dict[str, str],
               allowed: dict[str, tuple[str, str]], today: dt.date) -> list[str]:
    disabled = {path for path, state in states.items()
                if state.startswith("disabled") and path in workflows}
    dead = disabled - reachable_from_active(workflows, disabled)
    problems = []
    for path in sorted(dead):
        entry = allowed.get(path)
        if entry is None:
            problems.append(f"{path} is disabled on GitHub; delete it or re-enable it")
        elif dt.date.fromisoformat(entry[1]) < today:
            problems.append(f"{path} allowance for {entry[0]} expired on {entry[1]}")
    for path in sorted(set(allowed) - dead):
        problems.append(f"{path} is allowed but is not a disabled workflow nothing calls; drop the entry")
    return problems


def github_states(repo: str) -> dict[str, str]:
    result = subprocess.run(
        ["gh", "api", "--paginate", f"repos/{repo}/actions/workflows",
         "--jq", ".workflows[] | [.path, .state] | @tsv"],
        check=False, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"::error::cannot list workflows for {repo}: {result.stderr.strip()}")
    return dict(line.split("\t", 1) for line in result.stdout.splitlines() if line)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="honua-io/honua-server")
    parser.add_argument("--states", type=Path, help="JSON object of workflow path to state, instead of the API")
    args = parser.parse_args()
    states = json.loads(args.states.read_text()) if args.states else github_states(args.repo)
    problems = violations(tracked_workflows(ROOT), states, ALLOWED, dt.date.today())
    for problem in problems:
        print(f"::error::{problem}")
    print(f"disabled-workflow check: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
