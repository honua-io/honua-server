#!/usr/bin/env python3
"""Select the latest successful trunk CI ancestor without regressing a queued base."""

import json
import os
import subprocess
import sys

GIT_SHA_LENGTH = 40


def ancestor(base, head):
    return subprocess.run(["git", "merge-base", "--is-ancestor", base, head],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def completed_base(head, lower_bound=""):
    endpoint = (f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/workflows/ci.yml/runs"
                "?branch=trunk&status=completed&per_page=100")
    payload = json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))
    runs = sorted(payload["workflow_runs"], key=lambda run: run["updated_at"], reverse=True)
    for run in runs:
        candidate = run["head_sha"]
        if (run["conclusion"] == "success" and len(candidate) == GIT_SHA_LENGTH
                and all(character in "0123456789abcdef" for character in candidate)
                and ancestor(candidate, head)
                and (not lower_bound or ancestor(lower_bound, candidate))):
            return candidate
    return lower_bound


if __name__ == "__main__":
    print(completed_base(sys.argv[1]))
