#!/usr/bin/env python3
"""Dispatch the full ci.yml matrix on trunk inside the overnight window when the crons did not.

GitHub delivers this repository's `schedule` events hours late: in November 2026 the
21:23 UTC full-CI cron fired 3.7-4.6 h late and the 09:23 UTC one 6.5-8 h late, and the
half-hourly watchdog fired every 4-7 h. A late full run lands in the 07:00-22:00 Pacific
daytime window, where it is cancelled to free heavy runners, so trunk never certifies and
the nightly container build has no candidate to image.

`push` events are not throttled, so full-ci-dispatch.yml calls this script on every trunk
push (plus an hourly backup cron). Between 22:00 and 03:00 Pacific it dispatches one full run when
none is queued or running, the night has had fewer than `--max-per-night` full runs that
were not cancelled, and trunk head has no completed full result yet. Outside the window
it does nothing: a run dispatched by 02:59 Pacific needs about 2 h 50 min plus queueing and
finishes before 07:00 Pacific. The window is Pacific wall-clock time, so it holds in both PST
and PDT. The ci.yml crons stay; a scheduled run that does
arrive counts like a dispatched one. A night with no trunk push before 03:00 Pacific relies on
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
from dataclasses import dataclass
from typing import Callable
from zoneinfo import ZoneInfo

FULL_EVENTS = frozenset({"schedule", "workflow_dispatch"})
FULL_TITLE_PREFIX = "CI full"
PACIFIC = ZoneInfo("America/Los_Angeles")


class DispatchError(RuntimeError):
    pass


def parse_time(value: str) -> dt.datetime:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def is_full_run(run: dict, branch: str) -> bool:
    return (run.get("head_branch") == branch
            and run.get("event") in FULL_EVENTS
            and str(run.get("display_title", "")).startswith(FULL_TITLE_PREFIX))


@dataclass(frozen=True)
class Policy:
    """The overnight dispatch window, in `zone` local hours, and the nightly run limit."""
    branch: str = "trunk"
    window_start_hour: int = 22
    last_dispatch_hour: int = 3
    zone: dt.tzinfo = PACIFIC
    max_per_night: int = 2


@dataclass(frozen=True)
class Snapshot:
    now: dt.datetime
    runs: list[dict]
    trunk_head: str


def decide(snapshot: Snapshot, policy: Policy = Policy()) -> tuple[bool, str]:
    """(dispatch, reason) for one trunk push or backup tick."""
    local = snapshot.now.astimezone(policy.zone)
    if policy.last_dispatch_hour <= local.hour < policy.window_start_hour:
        return False, (f"outside the {policy.window_start_hour:02d}:00-{policy.last_dispatch_hour:02d}:00 "
                       f"{local:%Z} dispatch window ({local:%H:%M} {local:%Z})")
    full = [run for run in snapshot.runs if is_full_run(run, policy.branch)]
    active = [run for run in full if run.get("status") != "completed"]
    if active:
        return False, f"full run {active[0].get('id')} is {active[0].get('status')}"
    tonight = [run for run in full
               if parse_time(run["created_at"]) >= night_start(local, policy)
               and run.get("conclusion") != "cancelled"]
    if len(tonight) >= policy.max_per_night:
        return False, f"{len(tonight)} full runs already started tonight (limit {policy.max_per_night})"
    head = snapshot.trunk_head
    finished = sorted((run for run in full if run.get("conclusion") not in (None, "cancelled")),
                      key=lambda run: parse_time(run["created_at"]), reverse=True)
    if finished and finished[0].get("head_sha") == head:
        return False, (f"trunk head {head[:12]} already has full result {finished[0].get('id')} "
                       f"({finished[0].get('conclusion')})")
    return True, f"no full run is active and trunk head {head[:12]} has no full result"


def night_start(local: dt.datetime, policy: Policy) -> dt.datetime:
    day = local.date() if local.hour >= policy.window_start_hour else local.date() - dt.timedelta(days=1)
    return dt.datetime.combine(day, dt.time(policy.window_start_hour), tzinfo=policy.zone)


def gh_once(args: list[str], stdin: str | None = None) -> subprocess.CompletedProcess:
    env = dict(os.environ, NO_COLOR="1", GH_FORCE_TTY="0")
    return subprocess.run(["gh", "api", *args], input=stdin, capture_output=True, text=True, env=env)


def failure_detail(result: subprocess.CompletedProcess) -> str:
    return " ".join((result.stderr or "").split())


def gh_json(path: str) -> dict:
    """GET with backoff on transient failures; a persistent refusal raises."""
    delays = (10, 30, 60)
    for attempt in range(len(delays) + 1):
        result = gh_once([path])
        if result.returncode == 0:
            return json.loads(result.stdout)
        detail = failure_detail(result)
        if attempt == len(delays) or "HTTP 404" in detail or "HTTP 422" in detail:
            raise DispatchError(f"gh api {path} failed: {detail}")
        print(f"::warning::gh api {path} failed ({detail}); retrying in {delays[attempt]}s", file=sys.stderr)
        time.sleep(delays[attempt])
    raise AssertionError("unreachable")


@dataclass(frozen=True)
class Target:
    repository: str
    workflow: str
    branch: str


def list_runs(fetch: Callable[[str], dict], target: Target) -> list[dict]:
    path = f"repos/{target.repository}/actions/workflows/{target.workflow}/runs?branch={target.branch}&per_page=50"
    return list(fetch(path).get("workflow_runs", []))


def dispatch_once(target: Target) -> None:
    """POST the dispatch exactly once: a retry GitHub had already accepted would queue a second full run."""
    result = gh_once([f"repos/{target.repository}/actions/workflows/{target.workflow}/dispatches",
                      "--method", "POST", "--input", "-"], json.dumps({"ref": target.branch}))
    if result.returncode != 0:
        raise DispatchError(f"dispatch of {target.workflow} failed: {failure_detail(result)}")


def wait_for_new_run(target: Target, known: set) -> dict | None:
    """Wait for the run to appear so the next serialized dispatcher sees it as active."""
    for _ in range(36):
        time.sleep(5)
        new = [run for run in list_runs(gh_json, target)
               if run.get("id") not in known and is_full_run(run, target.branch)]
        if new:
            return new[0]
    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", "honua-io/honua-server"))
    parser.add_argument("--workflow", default="ci.yml")
    parser.add_argument("--branch", default="trunk")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    target = Target(args.repository, args.workflow, args.branch)
    head = gh_json(f"repos/{target.repository}/commits/{target.branch}")["sha"]
    runs = list_runs(gh_json, target)
    dispatch, reason = decide(Snapshot(dt.datetime.now(dt.timezone.utc), runs, head), Policy(branch=target.branch))
    if not dispatch:
        print(f"::notice::no dispatch: {reason}")
        return 0
    if args.dry_run:
        print(f"::notice::dry run, would dispatch full {target.workflow} on {target.branch}: {reason}")
        return 0
    dispatch_once(target)
    run = wait_for_new_run(target, {run.get("id") for run in runs})
    if run is None:
        # Red, so a possible duplicate dispatch by the next serialized run is visible.
        print(f"::error::dispatched full {target.workflow} on {target.branch} but the run is not listed after 180 s")
        return 1
    print(f"::notice::dispatched full {target.workflow} run {run.get('id')} on {target.branch}: {reason}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
