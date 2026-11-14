#!/usr/bin/env python3
"""Bound optional trunk affected CI; deferred coverage remains in scheduled full CI."""

import json
import os
from pathlib import Path
import subprocess
import sys

from supersede_trailing_runs import ACTIVE_STATUSES, gh_api, repository_endpoint

JOB_LIMIT = 40
# Conservative bound: every side job, including all four foundation families,
# both MCP transports and all three Postgres versions. The fixture checks this
# against ci.yml so additions cannot silently expand admitted fanout.
SIDE_JOB_BUDGET = 33


def active_pr_gate():
    for status in ACTIVE_STATUSES:
        runs = gh_api(repository_endpoint(
            f"actions/workflows/pr-gate.yml/runs?status={status}&per_page=100"))["workflow_runs"]
        if runs:
            return True
    return False


def selection(base, head):
    changed = subprocess.check_output(
        ["git", "diff", "--no-renames", "--name-only", f"{base}...{head}"], text=True)
    descriptor = subprocess.check_output(
        ["bash", "scripts/ci/honua-server-targeted-tests.sh", "--stdin"],
        input=changed + "\n", text=True)
    return json.loads(descriptor)


def decision(descriptor, pr_busy):
    if not isinstance(descriptor["shards"], list) or type(descriptor["run_all"]) is not bool:
        raise ValueError("Unknown routing descriptor")
    count = len(descriptor["shards"]) + SIDE_JOB_BUDGET
    if pr_busy:
        return False, "PR Gate is active; reserve the heavy pool for PRs."
    if descriptor.get("run_all"):
        return False, "Routing requires full coverage."
    if count > JOB_LIMIT:
        return False, f"Conservative job bound {count} exceeds {JOB_LIMIT}."
    return True, f"Conservative job bound {count} is within {JOB_LIMIT}."


def admission(base, head):
    if not base:
        return False, "No completed trunk ancestor is available."
    try:
        return decision(selection(base, head), active_pr_gate())
    except (subprocess.CalledProcessError, ValueError, KeyError, TypeError):
        return False, "Selection or PR queue history is unavailable."


def record(reason):
    message = f"{reason} Deferred coverage runs in the next scheduled full CI."
    print(message)
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
        summary.write(message + "\n")


def require_descriptor(raw):
    allowed, reason = decision(json.loads(raw), pr_busy=False)
    if not allowed:
        record(reason)
        raise SystemExit(1)


def main():
    if sys.argv[1] == "--descriptor":
        require_descriptor(sys.argv[2])
        return
    allowed, reason = admission(*sys.argv[1:])
    if not allowed:
        record(reason)
    else:
        print(reason)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"admitted={str(allowed).lower()}\n")


if __name__ == "__main__":
    main()
