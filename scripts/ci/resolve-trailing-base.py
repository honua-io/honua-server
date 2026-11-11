#!/usr/bin/env python3
"""Refresh queued trailing verification and bound accumulated shard fanout."""

import json
import os
from pathlib import Path
import subprocess
import sys


SHARD_BACKLOG_LIMIT = 30


def git(*arguments):
    return subprocess.check_output(["git", *arguments], text=True).strip()


def ancestor(base, head):
    return subprocess.run(["git", "merge-base", "--is-ancestor", base, head],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def completed_base(dispatched, head):
    endpoint = (f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/workflows/ci.yml/runs"
                "?branch=trunk&status=completed&per_page=100")
    try:
        payload = json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))
    except (subprocess.CalledProcessError, json.JSONDecodeError):
        print("::warning::Completed CI history unavailable; retaining the dispatched base.")
        return dispatched
    runs = sorted(payload["workflow_runs"], key=lambda run: run["updated_at"], reverse=True)
    for run in runs:
        candidate = run["head_sha"]
        if (run["conclusion"] in {"success", "failure"} and len(candidate) == 40
                and all(character in "0123456789abcdef" for character in candidate)
                and ancestor(dispatched, candidate) and ancestor(candidate, head)):
            return candidate
    return dispatched


def selected_count(base, head):
    changed = git("diff", "--no-renames", "--name-only", f"{base}...{head}")
    descriptor = subprocess.check_output(
        ["bash", "scripts/ci/honua-server-targeted-tests.sh", "--stdin"],
        input=changed + "\n", text=True)
    return len(json.loads(descriptor)["shards"])


def execution_base(base, head):
    count = selected_count(base, head)
    if count <= SHARD_BACKLOG_LIMIT:
        return base, f"Accumulated selection: {count} shards."
    newest_parent = git("rev-parse", f"{head}^1")
    newest_count = selected_count(newest_parent, head)
    message = (f"Accumulated selection ({count} shards) exceeds {SHARD_BACKLOG_LIMIT}; "
               f"verify newest merge only ({newest_count} shards). Earlier merges are deferred "
               "to scheduled full CI. Required PR gates are unchanged.")
    if newest_count > SHARD_BACKLOG_LIMIT:
        message += " The newest merge itself requires broad coverage; retain all of its shards."
    return newest_parent, message


def main():
    dispatched, head = sys.argv[1:]
    base = completed_base(dispatched, head)
    base, reason = execution_base(base, head)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"base={base}\nrange={base}...{head}\ntrailing_selective=true\n")
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
        summary.write(f"\nDispatch base: `{dispatched}`. Execution range: `{base}...{head}`.\n{reason}\n")


if __name__ == "__main__":
    main()
