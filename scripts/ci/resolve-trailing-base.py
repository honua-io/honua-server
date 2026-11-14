#!/usr/bin/env python3
"""Refresh queued trailing verification and bound accumulated shard fanout."""

import json
import os
from pathlib import Path
import subprocess
import sys

from completed_ci_base import completed_base
from affected_admission import admission, record
from supersede_trailing_runs import gh_api, repository_endpoint


def execution_base(base, head):
    allowed, reason = admission(base, head)
    if not allowed:
        record(reason)
        # Cancel before changes publishes outputs, so no build/test fanout can
        # start and this head cannot become a successful completed CI baseline.
        gh_api(repository_endpoint(f"actions/runs/{os.environ['GITHUB_RUN_ID']}/cancel"), "POST")
        raise SystemExit(1)
    return base, reason


def main():
    dispatched, head = sys.argv[1:]
    try:
        base = completed_base(head, dispatched)
    except (subprocess.CalledProcessError, json.JSONDecodeError):
        print("::warning::Completed CI history unavailable; retaining the dispatched base.")
        base = dispatched
    base, reason = execution_base(base, head)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"base={base}\nrange={base}...{head}\ntrailing_selective=true\n")
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
        summary.write(f"\nDispatch base: `{dispatched}`. Execution range: `{base}...{head}`.\n{reason}\n")


if __name__ == "__main__":
    main()
