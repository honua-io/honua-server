#!/usr/bin/env python3
"""Select the reduced protocol profiles Trunk Sanity compiles for a trunk head.

Trunk Sanity queues the newest push behind the running one, so intermediate
pushes may never run. The range therefore starts at the newest successful
Trunk Sanity ancestor rather than at the push's own predecessor. Only runs
whose head contains this script count: older runs never compiled profiles.
When that range changes anything but workflows or prose, or no such run
exists, every
profile is compiled. The selector's module narrowing is not used: it maps
some server files that still compile in reduced profiles (gRPC hosting, for
example) to `all` only.
"""

import os
from pathlib import Path
import subprocess
import sys

import completed_ci_base

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = "scripts/ci/trunk_sanity_profiles.py"
SELECTOR = ROOT / "scripts/ci/protocol-profile-selection.py"


def compiled_profiles(run):
    """True when the run's workflow version had the profile job."""
    return subprocess.run(["git", "cat-file", "-e", f"{run['head_sha']}:{SCRIPT}"],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def compiled(path):
    """False only for workflow files and Markdown under docs/ or at the root.

    Analyzer settings, banned symbols and embedded resources (skills/, docs/,
    tests/fixtures/, docker/) live outside src/ and eng/, so only paths that
    never reach a compilation are exempt.
    """
    prose = path.lower().endswith(".md") and (path.startswith("docs/") or "/" not in path)
    return not (path.startswith(".github/") or prose)


def compiled_changes(base, head):
    changed = subprocess.check_output(["git", "diff", "--name-only", base, head], text=True)
    return [path for path in changed.splitlines() if compiled(path)]


def select(base, head, selection):
    """Write the selector output and return (build, reason)."""
    if not base:
        reason = "No successful Trunk Sanity ancestor; compiling every protocol profile."
    elif compiled_changes(base, head):
        reason = f"Compiled inputs changed since {base[:12]}; compiling every protocol profile."
    else:
        return False, f"No compiled input changed since {base[:12]}; protocol profiles stay verified."
    output = subprocess.check_output([sys.executable, str(SELECTOR), "--dry-run", "--all"], text=True)
    Path(selection).write_text(output, encoding="utf-8")
    return True, reason


def main():
    head, selection = sys.argv[1:3]
    sanity_runs = [run for run in completed_ci_base.runs("trunk-sanity.yml", "success") if compiled_profiles(run)]
    build, reason = select(completed_ci_base.completed_base(head, completed=sanity_runs), head, selection)
    print(reason)
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
        summary.write(reason + "\n")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        output.write(f"build={str(build).lower()}\n")


if __name__ == "__main__":
    main()
