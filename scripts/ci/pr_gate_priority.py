#!/usr/bin/env python3
"""Release optional affected CI capacity when PR Gate requests runners."""

import os
from pathlib import Path

from supersede_trailing_runs import active_trunk_runs, cancel
from trailing_selection import AFFECTED_RUN_TITLE_PREFIX


def preemptible(run, repository):
    return (run.get("head_branch") == "trunk"
            and run.get("event") == "workflow_dispatch"
            and (run.get("head_repository") or {}).get("full_name") == repository
            and (run.get("display_title") or "").startswith(AFFECTED_RUN_TITLE_PREFIX))


def main():
    repository = os.environ["GITHUB_REPOSITORY"]
    for run in active_trunk_runs():
        if not preemptible(run, repository):
            continue
        message = (f"PR Gate takes priority over affected CI run {run['id']}; "
                   "deferred coverage remains in scheduled full CI.")
        print(message)
        with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
            summary.write(message + "\n")
        cancel(run["id"])


if __name__ == "__main__":
    main()
