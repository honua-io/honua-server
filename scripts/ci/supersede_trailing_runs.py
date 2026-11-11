#!/usr/bin/env python3
"""Cancel trunk CI runs whose coverage a running full CI run already contains.

A full run covers every merge up to its head, so a selective ("CI affected")
run at that head or an earlier one only duplicates its jobs on the shared
runner pool. A starting full run cancels such selective runs; a starting
selective run cancels itself when a full run covering its head is in progress.
"""

import json
import os
import subprocess
import sys

from trailing_selection import AFFECTED_RUN_TITLE_PREFIX

ACTIVE_STATUSES = ("in_progress", "queued", "pending", "waiting", "requested")
# Runs held by a concurrency group report "pending"; started runs report either
# of these, since GitHub keeps a run "queued" while any of its jobs waits for a runner.
STARTED_STATUSES = {"in_progress", "queued"}


def gh_api(endpoint, method="GET"):
    output = subprocess.check_output(["gh", "api", "--method", method, endpoint], text=True)
    return json.loads(output) if output.strip() else {}


def repository_endpoint(path):
    return f"repos/{os.environ['GITHUB_REPOSITORY']}/{path}"


def active_trunk_runs():
    runs = {}
    for status in ACTIVE_STATUSES:
        payload = gh_api(repository_endpoint(
            f"actions/workflows/ci.yml/runs?branch=trunk&status={status}&per_page=100"))
        runs.update((run["id"], run) for run in payload["workflow_runs"])
    return list(runs.values())


def is_selective(run):
    return (run.get("display_title") or "").startswith(AFFECTED_RUN_TITLE_PREFIX)


def is_untitled_full_run(run):
    """Scheduled and plain manual runs carry no dispatch nonce; titled dispatches may be selective."""
    return (run.get("display_title") or "").strip() == "CI"


def contains(covering_head, head):
    if covering_head == head:
        return True
    comparison = gh_api(repository_endpoint(f"compare/{head}...{covering_head}"))
    return comparison["status"] in {"ahead", "identical"}


def covered_selective_runs(runs, own_id, own_head):
    return [run for run in runs
            if run["id"] != own_id and is_selective(run) and contains(own_head, run["head_sha"])]


def covering_full_run(runs, own_id, own_head):
    for run in runs:
        if (run["id"] != own_id and run["status"] in STARTED_STATUSES and is_untitled_full_run(run)
                and contains(run["head_sha"], own_head)):
            return run
    return None


def supersede_from_full_run(runs, own_id, own_head):
    covered = covered_selective_runs(runs, own_id, own_head)
    if not covered:
        return "No selective trunk run is covered by this full run.", []
    titles = ", ".join(f"`{run['display_title']}` ({run['id']})" for run in covered)
    return f"Cancelling selective runs this full run covers: {titles}.", [run["id"] for run in covered]


def supersede_own_selective_run(runs, own_id, own_head):
    covering = covering_full_run(runs, own_id, own_head)
    if covering is None:
        return "No running full CI run covers this head; selective verification continues.", []
    return (f"Full CI run {covering['id']} at `{covering['head_sha'][:12]}` covers this head; "
            "cancelling this selective run."), [own_id]


STRATEGIES = {"full": supersede_from_full_run, "selective": supersede_own_selective_run}


def cancel(run_id):
    try:
        gh_api(repository_endpoint(f"actions/runs/{run_id}/cancel"), method="POST")
    except subprocess.CalledProcessError as error:
        print(f"::warning::Run {run_id} could not be cancelled ({error}); it may have finished.")


def main():
    own_id = int(os.environ["GITHUB_RUN_ID"])
    own_head = os.environ["GITHUB_SHA"]
    strategy = STRATEGIES[sys.argv[1]]
    try:
        message, cancellations = strategy(active_trunk_runs(), own_id, own_head)
    except (subprocess.CalledProcessError, json.JSONDecodeError, KeyError) as error:
        message, cancellations = f"::warning::Trunk run history unavailable ({error}); no run cancelled.", []
    print(message)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
        summary.write(f"\n{message}\n")
    for run_id in cancellations:
        cancel(run_id)


if __name__ == "__main__":
    main()
