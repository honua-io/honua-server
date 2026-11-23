#!/usr/bin/env python3
"""Select the reduced protocol profiles Trunk Sanity compiles for a trunk head.

Trunk Sanity queues the newest push behind the running one, so intermediate
pushes may never run. The range therefore starts at the newest successful
Trunk Sanity ancestor rather than at the push's own predecessor. Only runs
whose head contains this script count: older runs never compiled profiles.
Profiles are compiled only when that range changes compiled inputs; the
profile list comes from protocol-profile-selection.py. Without a successful
ancestor every profile is compiled.
"""

import os
from pathlib import Path
import re
import subprocess
import sys

import completed_ci_base

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = "scripts/ci/trunk_sanity_profiles.py"
SELECTOR = ROOT / "scripts/ci/protocol-profile-selection.py"
COMPILED_INPUT = re.compile(
    r"^(src/|eng/|Directory\.(Build|Packages)\.(props|targets)$|global\.json$|NuGet\.config$)",
    re.IGNORECASE)


def compiled_profiles(run):
    """True when the run's workflow version had the profile job."""
    return subprocess.run(["git", "cat-file", "-e", f"{run['head_sha']}:{SCRIPT}"],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def compiled_changes(base, head):
    changed = subprocess.check_output(["git", "diff", "--name-only", base, head], text=True)
    return [path for path in changed.splitlines() if COMPILED_INPUT.match(path)]


def select(base, head, selection):
    """Write the selector output and return (build, reason)."""
    if not base:
        scope, environment = ["--all"], os.environ
        reason = "No successful Trunk Sanity ancestor; compiling every protocol profile."
    elif not compiled_changes(base, head):
        return False, f"No compiled input changed since {base[:12]}; protocol profiles stay verified."
    else:
        scope = ["--changed-from-git"]
        environment = {**os.environ, "BASE_REF": base, "HEAD_REF": head}
        reason = f"Compiled inputs changed since {base[:12]}; compiling the selected protocol profiles."
    output = subprocess.check_output(
        [sys.executable, str(SELECTOR), "--dry-run", *scope], text=True, env=environment)
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
