#!/usr/bin/env python3
"""Select the latest successful trunk CI ancestor without regressing a queued base."""

import json
import os
import subprocess
import sys

GIT_SHA_LENGTH = 40
# Step that trunk-affected.yml runs only when it hands a range to scheduled full CI.
DEFERRED_STEP = "Record coverage deferred to scheduled full CI"
DEFERRAL_LOOKUPS = 20


def ancestor(base, head):
    return subprocess.run(["git", "merge-base", "--is-ancestor", base, head],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def valid_sha(candidate):
    return len(candidate) == GIT_SHA_LENGTH and all(character in "0123456789abcdef" for character in candidate)


def runs(workflow, status):
    endpoint = (f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/workflows/{workflow}/runs"
                f"?branch=trunk&status={status}&per_page=100")
    payload = json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))
    return sorted(payload["workflow_runs"], key=lambda run: run["updated_at"], reverse=True)


def completed_base(head, lower_bound=""):
    for run in runs("ci.yml", "completed"):
        candidate = run["head_sha"]
        if (run["conclusion"] == "success" and valid_sha(candidate)
                and ancestor(candidate, head)
                and (not lower_bound or ancestor(lower_bound, candidate))):
            return candidate
    return lower_bound


def deferred(run):
    endpoint = f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{run['id']}/jobs"
    payload = json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))
    steps = [step for job in payload["jobs"] for step in job.get("steps", [])]
    return any(step["name"] == DEFERRED_STEP and step["conclusion"] == "success" for step in steps)


def deferred_floor(base, head):
    """Return the newest trunk head whose range was already handed to scheduled full CI."""
    candidates = [run for run in runs("trunk-affected.yml", "success")
                  if valid_sha(run["head_sha"]) and run["head_sha"] != base
                  and ancestor(run["head_sha"], head) and (not base or ancestor(base, run["head_sha"]))]
    for run in candidates[:DEFERRAL_LOOKUPS]:
        if deferred(run):
            return run["head_sha"]
    return ""


def execution_base(head):
    base = completed_base(head)
    try:
        floor = deferred_floor(base, head)
    except (subprocess.CalledProcessError, json.JSONDecodeError, KeyError, TypeError):
        return base
    return completed_base(head, floor) if floor else base


if __name__ == "__main__":
    print(execution_base(sys.argv[1]))
