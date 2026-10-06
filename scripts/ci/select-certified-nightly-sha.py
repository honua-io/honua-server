#!/usr/bin/env python3
"""Select the trunk sha the nightly container build images: the newest CERTIFIED one.

honua-io/honua-release#376 R18 pins a server candidate only when the sha is both imaged
(`nightly-<sha7>`, `nightly-aot-<sha7>`, `nightly-lambda-aot-<sha7>-<arch>`) and certified by
a completed full-matrix `ci.yml` run. The nightly used to image HEAD-at-cron, which rarely
had a completed full matrix, while the sha that did certify was never imaged: the two sets
stopped intersecting on 2026-09-16 and the resolver had no qualifying candidate for 18 days.

Certification is judged the way the resolver judges it
(honua-release `certification/check_build_test.py::classify_full_matrix`):

* the candidate runs are this repository's `ci.yml` runs on exactly that head sha whose event
  is `schedule` or `workflow_dispatch`;
* of those, only COMPLETED runs count, and the newest one (by `updated_at`, then run id)
  decides -- an older green full matrix does not survive a newer run that skipped lanes;
* every certification lane must have a check-run from THAT run whose latest attempt is
  `completed`/`success`, and the run's own conclusion must be `success`.

The lane list is not declared here. It is `ci.yml`'s workflow-level `CERTIFICATION_LANE_JOBS`
(job ids), resolved to the check-run names those jobs publish, so ci.yml and this selector
cannot drift apart.

Usage:
  select-certified-nightly-sha.py --repository honua-io/honua-server [--branch trunk]
      [--limit 100] [--workflow .github/workflows/ci.yml] [--sha <40-hex>]

`--sha` images one named trunk sha instead of walking, and still requires it to be certified.
Prints the chosen sha and its certifying run, and writes `sha`, `sha7`, `run_id` and `run_url`
to $GITHUB_OUTPUT when set. Exits 1 naming the missing lanes of the newest candidates when no
sha in the newest `--limit` trunk commits qualifies.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from collections.abc import Callable, Iterable, Iterator
from pathlib import Path

SHA = re.compile(r"[0-9a-f]{40}\Z")
RUN_URL = re.compile(r"/actions/runs/(\d+)(?:/|$)")
LANES_VARIABLE = "CERTIFICATION_LANE_JOBS"
# The events honua-release's full-matrix policy accepts for honua-server
# (certification/full-matrix-checks.yaml). merge_group and pull_request never certify.
CERTIFYING_EVENTS = frozenset({"schedule", "workflow_dispatch"})
REPORT_LIMIT = 10


class SelectionError(RuntimeError):
    """No certified candidate, or the evidence could not be read."""


def certification_lanes(workflow_text: str) -> list[str]:
    """The check-run names of ci.yml's certification lanes, in declaration order.

    Reads `CERTIFICATION_LANE_JOBS` from the workflow-level `env:` block and maps every job id
    to that job's `name:`. A job id that does not exist, or a name that is an expression (and
    therefore not a stable check-run name), refuses rather than silently dropping a lane.
    """
    lines = workflow_text.splitlines()
    declared = None
    in_env = False
    for line in lines:
        if re.match(r"^env:\s*$", line):
            in_env = True
            continue
        if in_env and re.match(r"^\S", line):
            in_env = False
        if in_env:
            match = re.match(rf"^  {LANES_VARIABLE}:\s*(['\"]?)(.*?)\1\s*$", line)
            if match:
                declared = match.group(2).split()
    if not declared:
        raise SelectionError(f"workflow declares no top-level env {LANES_VARIABLE}")

    names: dict[str, str] = {}
    in_jobs = False
    current = None
    for line in lines:
        if re.match(r"^jobs:\s*$", line):
            in_jobs = True
            continue
        if in_jobs and re.match(r"^\S", line):
            in_jobs = False
        if not in_jobs:
            continue
        job = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if job:
            current = job.group(1)
            continue
        name = re.match(r"^    name:\s*(.+?)\s*$", line)
        if current and name and current not in names:
            names[current] = name.group(1).strip("'\"")

    lanes = []
    for job_id in declared:
        if job_id not in names:
            raise SelectionError(f"{LANES_VARIABLE} names job '{job_id}', which has no `name:` in the workflow")
        if "${{" in names[job_id]:
            raise SelectionError(f"certification lane '{job_id}' has an expression name ({names[job_id]}); "
                                 "a lane needs one stable check-run name")
        lanes.append(names[job_id])
    if len(set(lanes)) != len(lanes):
        raise SelectionError(f"{LANES_VARIABLE} resolves to duplicate check-run names {lanes}")
    return lanes


def gh_json(path: str) -> object:
    """`gh api <path>` with backoff on transient failures; a persistent refusal raises."""
    env = dict(os.environ, NO_COLOR="1", GH_FORCE_TTY="0")
    delays = (10, 30, 60, 120)
    for attempt in range(len(delays) + 1):
        result = subprocess.run(["gh", "api", path], capture_output=True, text=True, env=env)
        if result.returncode == 0:
            try:
                return json.loads(result.stdout)
            except json.JSONDecodeError as exc:
                raise SelectionError(f"gh api {path} returned non-JSON ({exc})") from exc
        detail = " ".join((result.stderr or "").split())
        if attempt == len(delays) or "HTTP 404" in detail or "HTTP 422" in detail:
            raise SelectionError(f"gh api {path} failed: {detail}")
        print(f"::warning::gh api {path} failed ({detail}); retrying in {delays[attempt]}s", file=sys.stderr)
        time.sleep(delays[attempt])
    raise AssertionError("unreachable")


class GitHub:
    """The three reads the selection needs. Tests substitute a recorded `fetch`."""

    def __init__(self, repository: str, fetch: Callable[[str], object] = gh_json) -> None:
        self.repository = repository
        self.fetch = fetch

    def pages(self, path: str, key: str | None = None) -> Iterator[dict]:
        separator = "&" if "?" in path else "?"
        page, seen, total = 1, 0, None
        while True:
            result = self.fetch(f"{path}{separator}per_page=100&page={page}")
            if key:
                if not isinstance(result, dict) or not isinstance(result.get(key), list):
                    raise SelectionError(f"{path} page {page} has no {key} list")
                rows, count = result[key], result.get("total_count")
                if total is not None and count != total:
                    raise SelectionError(f"{path} total_count moved from {total} to {count} while paging")
                total = count
            else:
                rows = result
                if not isinstance(rows, list):
                    raise SelectionError(f"{path} page {page} is not a list")
            seen += len(rows)
            yield from rows
            if len(rows) < 100:
                break
            page += 1
        if isinstance(total, int) and seen != total:
            raise SelectionError(f"{path} returned {seen} of {total} {key}; refusing a truncated listing")

    def trunk(self, branch: str, limit: int) -> Iterator[str]:
        for index, row in enumerate(self.pages(f"repos/{self.repository}/commits?sha={branch}")):
            if index >= limit:
                return
            yield str(row.get("sha") or "")

    def contains(self, branch: str, sha: str) -> bool:
        result = self.fetch(f"repos/{self.repository}/compare/{sha}...{branch}")
        return isinstance(result, dict) and result.get("status") in {"ahead", "identical"}

    def runs(self, sha: str) -> list[dict]:
        return list(self.pages(f"repos/{self.repository}/actions/runs?head_sha={sha}", "workflow_runs"))

    def check_runs(self, sha: str) -> list[dict]:
        return list(self.pages(f"repos/{self.repository}/commits/{sha}/check-runs", "check_runs"))


def _run_marker(run: dict) -> tuple[str, int]:
    try:
        run_id = int(run.get("id") or 0)
    except (TypeError, ValueError):
        run_id = 0
    return str(run.get("updated_at") or run.get("created_at") or ""), run_id


def _attempt_marker(check: dict) -> tuple[int, str]:
    try:
        check_id = int(check.get("id") or 0)
    except (TypeError, ValueError):
        check_id = 0
    return check_id, str(check.get("started_at") or check.get("completed_at") or "")


def certify(github: GitHub, sha: str, workflow: str, lanes: Iterable[str]) -> tuple[bool, str, dict | None]:
    """(certified, why, deciding run) for one sha, by the resolver's full-matrix rule."""
    candidates = [
        run for run in github.runs(sha)
        if str(run.get("path", "")) == workflow and str(run.get("event", "")) in CERTIFYING_EVENTS
        and str(run.get("head_sha", "")) == sha
    ]
    completed = [run for run in candidates if run.get("status") == "completed"]
    if not completed:
        pending = len(candidates)
        return False, (f"no completed {workflow} schedule/workflow_dispatch run"
                       + (f" ({pending} still running)" if pending else "")), None
    run = max(completed, key=_run_marker)
    run_id = str(run.get("id") or "")

    latest: dict[str, dict] = {}
    for check in github.check_runs(sha):
        match = RUN_URL.search(str(check.get("details_url") or ""))
        if not match or match.group(1) != run_id:
            continue
        name = str(check.get("name", "")).strip()
        if name not in latest or _attempt_marker(check) > _attempt_marker(latest[name]):
            latest[name] = check

    required = list(lanes)
    missing = [lane for lane in required if lane not in latest]
    unsuccessful = [
        f"{lane}={latest[lane].get('conclusion') or latest[lane].get('status')}"
        for lane in required
        if lane in latest and (latest[lane].get("status") != "completed"
                               or latest[lane].get("conclusion") != "success")
    ]
    problems = []
    if missing:
        problems.append(f"missing lanes {missing}")
    if unsuccessful:
        problems.append(f"lanes not successful {unsuccessful}")
    if run.get("conclusion") != "success":
        problems.append(f"run conclusion={run.get('conclusion') or 'none'}")
    if problems:
        return False, f"{workflow} run {run_id} ({run.get('event')}) is not certifiable: " + "; ".join(problems), run
    return True, f"{workflow} run {run_id} ({run.get('event')}) completed with every certification lane successful", run


def select(github: GitHub, *, branch: str, limit: int, workflow: str, lanes: list[str],
           sha: str | None = None, log: Callable[[str], None] = print) -> tuple[str, dict]:
    """The newest certified trunk sha (or the named one) and its deciding run, or a refusal."""
    log(f"Certification lanes (from {workflow} {LANES_VARIABLE}): {', '.join(lanes)}")
    if sha:
        if not SHA.fullmatch(sha):
            raise SelectionError(f"--sha must be a full 40-hex commit sha, not {sha!r}")
        if not github.contains(branch, sha):
            raise SelectionError(f"{sha} is not on {branch}")
        certified, why, run = certify(github, sha, workflow, lanes)
        if not certified:
            raise SelectionError(f"requested {sha[:7]} is not certified: {why}")
        return sha, run

    skipped: list[str] = []
    for candidate in github.trunk(branch, limit):
        if not SHA.fullmatch(candidate):
            raise SelectionError(f"{branch} listing returned a non-immutable revision {candidate!r}")
        certified, why, run = certify(github, candidate, workflow, lanes)
        if certified:
            if skipped:
                log(f"Skipped {len(skipped)} newer {branch} commit(s) that are not certified:")
                for line in skipped[:REPORT_LIMIT]:
                    log(f"  {line}")
                if len(skipped) > REPORT_LIMIT:
                    log(f"  ... {len(skipped) - REPORT_LIMIT} older skipped commit(s) not listed")
            return candidate, run
        skipped.append(f"{candidate[:7]}: {why}")
    report = "\n".join(f"  {line}" for line in skipped[:REPORT_LIMIT])
    raise SelectionError(
        f"no {branch} commit in the newest {limit} is certified by a completed full-matrix {workflow} run "
        f"with every certification lane successful ({', '.join(lanes)}). Newest candidates:\n{report}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--repository", required=True)
    parser.add_argument("--branch", default="trunk")
    parser.add_argument("--limit", type=int, default=100)
    parser.add_argument("--workflow", default=".github/workflows/ci.yml",
                        help="repo-relative path of the certifying workflow; also read for its lane list")
    parser.add_argument("--sha", default="", help="image this certified trunk sha instead of the newest one")
    args = parser.parse_args(argv)

    try:
        lanes = certification_lanes(Path(args.workflow).read_text(encoding="utf-8"))
        sha, run = select(GitHub(args.repository), branch=args.branch, limit=args.limit,
                          workflow=args.workflow, lanes=lanes, sha=args.sha.strip() or None)
    except SelectionError as exc:
        for line in str(exc).splitlines():
            print(f"::error::{line}" if not line.startswith("  ") else line, file=sys.stderr)
        return 1

    run_id = str(run.get("id"))
    run_url = str(run.get("html_url") or f"https://github.com/{args.repository}/actions/runs/{run_id}")
    print(f"Selected {sha} (nightly-{sha[:7]}), certified by {args.workflow} run {run_id} "
          f"({run.get('event')}): {run_url}")
    outputs = {"sha": sha, "sha7": sha[:7], "run_id": run_id, "run_url": run_url}
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
            handle.writelines(f"{key}={value}\n" for key, value in outputs.items())
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write("### Nightly candidate\n\n"
                         f"Imaging `{sha}` (`nightly-{sha[:7]}`), "
                         f"{'the requested' if args.sha.strip() else 'the newest'} certified `{args.branch}` sha.\n"
                         f"Certifying run: [{run_id}]({run_url}) ({run.get('event')}).\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
