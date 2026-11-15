#!/usr/bin/env python3
"""Dispatch the full ci.yml matrix on trunk inside the overnight window when the crons did not.

GitHub delivers this repository's `schedule` events hours late: in November 2026 the
21:23 UTC full-CI cron fired 3.7-4.6 h late and the 09:23 UTC one 6.5-8 h late, and the
half-hourly watchdog fired every 4-7 h. A late full run lands in the 07:00-22:00 Pacific
daytime window, where it is cancelled to free heavy runners, so trunk never certifies and
the nightly container build has no candidate to image.

`push` events are not throttled, so full-ci-dispatch.yml calls this script on every trunk
push (plus an hourly backup cron). Between 06:00 and 11:00 UTC it dispatches one full run when
none is queued or running, the night has had fewer than `--max-per-night` full runs that
were not cancelled, and trunk head has no completed full result yet. Outside the window
it does nothing: a run dispatched by 10:59 UTC needs about 2 h 50 min plus queueing and
finishes before 15:00 UTC (07:00 PST). The ci.yml crons stay; a scheduled run that does
arrive counts like a dispatched one. A night with no trunk push before 11:00 UTC relies on
those crons, because the backup cron is delivered late too.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time
from typing import Callable

FULL_EVENTS = frozenset({"schedule", "workflow_dispatch"})
FULL_TITLE_PREFIX = "CI full"


class DispatchError(RuntimeError):
    pass


def parse_time(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def is_full_run(run: dict, branch: str) -> bool:
    return (run.get("head_branch") == branch
            and run.get("event") in FULL_EVENTS
            and str(run.get("display_title", "")).startswith(FULL_TITLE_PREFIX))


def decide(now: dt.datetime, runs: list[dict], trunk_head: str, *, branch: str = "trunk",
           window_start_hour: int = 6, last_dispatch_hour: int = 11,
           max_per_night: int = 2) -> tuple[bool, str]:
    """(dispatch, reason) for one trunk push or backup tick. `now` is UTC-aware."""
    if not window_start_hour <= now.hour < last_dispatch_hour:
        return False, (f"outside the {window_start_hour:02d}:00-{last_dispatch_hour:02d}:00 UTC "
                       f"dispatch window ({now:%H:%M} UTC)")
    full = [run for run in runs if is_full_run(run, branch)]
    active = [run for run in full if run.get("status") != "completed"]
    if active:
        run = active[0]
        return False, f"full run {run.get('id')} is {run.get('status')}"
    window_start = now.replace(hour=window_start_hour, minute=0, second=0, microsecond=0)
    tonight = [run for run in full
               if parse_time(run["created_at"]) >= window_start and run.get("conclusion") != "cancelled"]
    if len(tonight) >= max_per_night:
        return False, f"{len(tonight)} full runs already started tonight (limit {max_per_night})"
    finished = sorted((run for run in full if run.get("conclusion") not in (None, "cancelled")),
                      key=lambda run: parse_time(run["created_at"]), reverse=True)
    if finished and finished[0].get("head_sha") == trunk_head:
        run = finished[0]
        return False, f"trunk head {trunk_head[:12]} already has full result {run.get('id')} ({run.get('conclusion')})"
    return True, f"no full run is active and trunk head {trunk_head[:12]} has no full result"


def gh(args: list[str], stdin: str | None = None, *, retry: bool = True) -> str:
    env = dict(os.environ, NO_COLOR="1", GH_FORCE_TTY="0")
    # A retried dispatch POST that GitHub had already accepted would queue a second full run.
    delays = (10, 30, 60) if retry else ()
    for attempt in range(len(delays) + 1):
        result = subprocess.run(["gh", "api", *args], input=stdin, capture_output=True, text=True, env=env)
        if result.returncode == 0:
            return result.stdout
        detail = " ".join((result.stderr or "").split())
        if attempt == len(delays) or "HTTP 404" in detail or "HTTP 422" in detail:
            raise DispatchError(f"gh api {args[0]} failed: {detail}")
        print(f"::warning::gh api {args[0]} failed ({detail}); retrying in {delays[attempt]}s", file=sys.stderr)
        time.sleep(delays[attempt])
    raise AssertionError("unreachable")


def gh_json(path: str) -> dict:
    return json.loads(gh([path]))


def list_runs(fetch: Callable[[str], dict], repository: str, workflow: str, branch: str) -> list[dict]:
    path = f"repos/{repository}/actions/workflows/{workflow}/runs?branch={branch}&per_page=50"
    return list(fetch(path).get("workflow_runs", []))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", "honua-io/honua-server"))
    parser.add_argument("--workflow", default="ci.yml")
    parser.add_argument("--branch", default="trunk")
    parser.add_argument("--window-start-hour", type=int, default=6)
    parser.add_argument("--last-dispatch-hour", type=int, default=11)
    parser.add_argument("--max-per-night", type=int, default=2)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    now = dt.datetime.now(dt.timezone.utc)
    head = gh_json(f"repos/{args.repository}/commits/{args.branch}")["sha"]
    runs = list_runs(gh_json, args.repository, args.workflow, args.branch)
    dispatch, reason = decide(now, runs, head, branch=args.branch,
                              window_start_hour=args.window_start_hour,
                              last_dispatch_hour=args.last_dispatch_hour,
                              max_per_night=args.max_per_night)
    if not dispatch:
        print(f"::notice::no dispatch: {reason}")
        return 0
    if args.dry_run:
        print(f"::notice::dry run, would dispatch full {args.workflow} on {args.branch}: {reason}")
        return 0
    known = {run.get("id") for run in runs}
    gh([f"repos/{args.repository}/actions/workflows/{args.workflow}/dispatches", "--method", "POST",
        "--input", "-"], stdin=json.dumps({"ref": args.branch}), retry=False)
    # Wait for the run to appear so the next serialized dispatcher sees it as active; if it
    # never does, fail so the job is red rather than letting the next push dispatch again.
    for _ in range(36):
        time.sleep(5)
        new = [run for run in list_runs(gh_json, args.repository, args.workflow, args.branch)
               if run.get("id") not in known and is_full_run(run, args.branch)]
        if new:
            print(f"::notice::dispatched full {args.workflow} run {new[0].get('id')} on {args.branch}: {reason}")
            return 0
    print(f"::error::dispatched full {args.workflow} on {args.branch} but the run is not listed after 180 s")
    return 1


if __name__ == "__main__":
    sys.exit(main())
