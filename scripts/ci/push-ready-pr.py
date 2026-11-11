#!/usr/bin/env python3
"""Publish a lane's completed review batch without superseding an active gate."""
import json
import subprocess
import sys


class Deferred(RuntimeError):
    """Publication must wait for another event or local fix."""


def command(*args):
    return subprocess.check_output(args, text=True, stdin=subprocess.DEVNULL).strip()


def api_lines(url, jq):
    return [json.loads(line) for line in
            command("gh", "api", "--paginate", url, "--jq", jq).splitlines()]


def inspect(repo, branch, previous=None):
    # Paginate: a repository can have more than 100 open pull requests.
    pulls = api_lines(f"repos/{repo}/pulls?state=open&per_page=100", ".[] | @json")
    matches = [p for p in pulls
               if p["head"]["ref"] == branch and (p["head"].get("repo") or {}).get("full_name") == repo]
    if len(matches) > 1:
        raise Deferred("Multiple open PRs use this branch; inspect them before publishing.")
    pr = matches[0] if matches else None
    if previous and (not pr or (pr["number"], pr["head"]["sha"]) != previous):
        raise Deferred("PR head or state changed during validation; pull and review before publishing.")
    if not pr:
        return None
    head = pr["head"]["sha"]
    runs = api_lines(f"repos/{repo}/actions/workflows/pr-gate.yml/runs?head_sha={head}&per_page=100",
                     ".workflow_runs[] | @json")
    active = [run for run in runs if run["status"] != "completed"]
    if active:
        run = active[0]
        raise Deferred(f"PR Gate {run['id']} is {run['status']}. Batch fixes locally; publish after it completes.")
    return pr["number"], head


def main():
    try:
        branch = command("git", "symbolic-ref", "--quiet", "--short", "HEAD")
        if branch == "trunk":
            raise Deferred("Publish a topic branch, not trunk.")
        if command("git", "status", "--porcelain"):
            raise Deferred("Commit the complete review batch before publishing.")
        repo = command("gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner")
        # Check the configured push destination, including pushurl overrides.
        remote = command("git", "remote", "get-url", "--push", "origin")
        if remote not in (f"git@github.com:{repo}.git", f"https://github.com/{repo}.git",
                          f"https://github.com/{repo}", f"ssh://git@github.com/{repo}.git"):
            raise Deferred("origin's push URL must match the selected GitHub repository.")
        previous = inspect(repo, branch)
        head = command("git", "rev-parse", "HEAD")
        subprocess.run(["pre-pr"], check=True, stdin=subprocess.DEVNULL)
        if command("git", "rev-parse", "HEAD") != head or command("git", "status", "--porcelain"):
            raise Deferred("Local source changed during validation; review and validate the final batch.")
        current = inspect(repo, branch, previous)
        if not previous and current:
            raise Deferred("A PR was opened during validation; inspect its head before publishing.")
        # Git's normal fast-forward check protects against concurrent remote pushes.
        subprocess.run(["git", "push", "origin", f"HEAD:refs/heads/{branch}"],
                       check=True, stdin=subprocess.DEVNULL)
        return 0
    except (Deferred, subprocess.CalledProcessError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"Publication deferred: {error}", file=sys.stderr)
        return 75


if __name__ == "__main__":
    sys.exit(main())
