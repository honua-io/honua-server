#!/usr/bin/env python3
"""Dispatch the full ci.yml matrix on trunk inside the overnight window when the crons did not.

GitHub delivers this repository's `schedule` events hours late. A late full run lands in the 07:00-22:00 Pacific
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
import sys
import time
from dataclasses import dataclass
from typing import Callable
from urllib.parse import urlencode
from zoneinfo import ZoneInfo

import gh_api

FULL_EVENTS = frozenset({"schedule", "workflow_dispatch"})
FULL_TITLE_PREFIX = "CI full"
# Lifecycle order: a run that advances between two listings is still caught by a later one.
ACTIVE_STATUSES = ("requested", "waiting", "pending", "queued", "in_progress")
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
    if not in_window(local, policy):
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


def in_window(local: dt.datetime, policy: Policy) -> bool:
    return not policy.last_dispatch_hour <= local.hour < policy.window_start_hour


def night_start(local: dt.datetime, policy: Policy) -> dt.datetime:
    day = local.date() if local.hour >= policy.window_start_hour else local.date() - dt.timedelta(days=1)
    return dt.datetime.combine(day, dt.time(policy.window_start_hour), tzinfo=policy.zone)


def gh_json(path: str) -> dict:
    """GET with backoff on transient failures; a persistent refusal raises."""
    return gh_api.get_json(path, DispatchError)


@dataclass(frozen=True)
class Target:
    repository: str
    workflow: str
    branch: str


class RunHistory:
    """Filtered, fully paged ci.yml run listings on the target branch."""

    def __init__(self, fetch: Callable[[str], dict], target: Target) -> None:
        self.listing = gh_api.Listing(fetch, DispatchError)
        self.target = target

    def runs(self, **filters: str) -> list[dict]:
        query = urlencode({"branch": self.target.branch, **filters})
        path = f"repos/{self.target.repository}/actions/workflows/{self.target.workflow}/runs?{query}"
        return list(self.listing.pages(path, "workflow_runs"))

    def active(self) -> list[dict]:
        return [run for status in ACTIVE_STATUSES for run in self.runs(status=status)]

    def collect(self, head: str, since: dt.datetime) -> list[dict]:
        """The runs decide() reads: active ones, tonight's and those at trunk head.

        Each comes from its own filtered, fully paged listing, so affected runs dispatched by trunk
        pushes cannot push an active or counted full run off an unfiltered first page.
        """
        created = f">={since.astimezone(dt.timezone.utc):%Y-%m-%dT%H:%M:%SZ}"
        runs = self.active() + self.runs(created=created) + self.runs(head_sha=head)
        return list({run.get("id"): run for run in runs}.values())


def dispatch_once(target: Target) -> None:
    """POST the dispatch exactly once: a retry GitHub had already accepted would queue a second full run."""
    result = gh_api.gh_api([f"repos/{target.repository}/actions/workflows/{target.workflow}/dispatches",
                      "--method", "POST", "--input", "-"], json.dumps({"ref": target.branch}))
    if result.returncode != 0:
        raise DispatchError(f"dispatch of {target.workflow} failed: {gh_api.failure_detail(result)}")


def gh_json_once(path: str) -> dict:
    """GET without backoff, so polling after the dispatch stays inside the job timeout."""
    return gh_api.get_json(path, DispatchError, delays=())


def wait_for_new_run(target: Target, known: set) -> dict | None:
    """Wait for the run to appear so the next serialized dispatcher sees it as active."""
    history = RunHistory(gh_json_once, target)
    for _ in range(36):
        time.sleep(5)
        try:
            new = [run for run in history.active()
                   if run.get("id") not in known and is_full_run(run, target.branch)]
        except DispatchError as exc:
            print(f"::warning::listing after dispatch failed ({exc}); polling again", file=sys.stderr)
            continue
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
    policy = Policy(branch=target.branch)
    now = dt.datetime.now(dt.timezone.utc)
    local = now.astimezone(policy.zone)
    head, runs = "", []
    if in_window(local, policy):
        head = gh_json(f"repos/{target.repository}/commits/{target.branch}")["sha"]
        runs = RunHistory(gh_json, target).collect(head, night_start(local, policy))
    dispatch, reason = decide(Snapshot(now, runs, head), policy)
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
