#!/usr/bin/env python3
"""Bound optional trunk affected CI; deferred coverage remains in scheduled full CI."""

import json
import os
from pathlib import Path
import subprocess
import sys

JOB_LIMIT = 40
# Conservative bound: every side job, including all four foundation families,
# both MCP transports and all three Postgres versions. The fixture checks this
# against ci.yml so additions cannot silently expand admitted fanout.
SIDE_JOB_BUDGET = 34
# A transient router or history failure is retried by the next push instead of
# being recorded as a range owed to scheduled full CI.
UNAVAILABLE = "Selection is unavailable."


def selection(base, head):
    changed = subprocess.check_output(
        ["git", "diff", "--no-renames", "--name-only", f"{base}...{head}"], text=True)
    descriptor = subprocess.check_output(
        ["bash", "scripts/ci/honua-server-targeted-tests.sh", "--stdin"],
        input=changed + "\n", text=True)
    required = subprocess.check_output(
        ["jq", "-c", "--argjson", "selection", descriptor, "-f",
         "scripts/ci/required-shard-descriptor.jq", ".github/ci-shards.json"], text=True)
    return json.loads(required)


def decision(descriptor):
    if not isinstance(descriptor["shards"], list) or type(descriptor["run_all"]) is not bool:
        raise ValueError("Unknown routing descriptor")
    count = len(descriptor["shards"]) + SIDE_JOB_BUDGET
    if descriptor.get("run_all"):
        return False, "Routing requires full coverage."
    if count > JOB_LIMIT:
        return False, f"Conservative job bound {count} exceeds {JOB_LIMIT}."
    return True, f"Conservative job bound {count} is within {JOB_LIMIT}."


def admission(base, head):
    if not base:
        return False, "No completed trunk ancestor is available."
    try:
        return decision(selection(base, head))
    except (subprocess.CalledProcessError, ValueError, KeyError, TypeError):
        return False, UNAVAILABLE


def record(reason):
    message = f"{reason} Deferred coverage runs in the next scheduled full CI."
    print(message)
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
        summary.write(message + "\n")


def require_descriptor(raw):
    allowed, reason = decision(json.loads(raw))
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
        output.write(f"deferred={str(not allowed and reason != UNAVAILABLE).lower()}\n")


if __name__ == "__main__":
    main()
