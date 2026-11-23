#!/usr/bin/env python3
"""Identify affected trunk runs and cap their heavy-runner use beside PR Gate.

Admission bounds the job count before fanout. Full runs retain their normal
width; optional affected runs use at most two heavy shard runners. A selective
affected run does not schedule the router's no-path-match smoke shards, and it
runs the Honua.Core package proof only when a changed file is one that packing
Honua.Core reads. The twice-daily full run keeps both.
"""
import json
import re
import sys

# run-name of every trunk-affected.yml dispatch; full CI runs never carry it.
AFFECTED_RUN_TITLE_PREFIX = "CI affected "
AFFECTED_WIDTH = 2
# Repository metadata that no build, test or package reads. The ci.yml
# change filter keeps the same pattern in REPO_METADATA.
REPO_METADATA = (
    r"\.github/(codeql/|ISSUE_TEMPLATE/|CODEOWNERS$|actionlint\.yaml$"
    r"|dependabot\.yml$|hygiene\.json$|pull_request_template\.md$)"
)
# Files that packing Honua.Core and Honua.Core.Abstractions reads: their
# sources, MSBuild/SDK/NuGet/analyzer configuration from the directories above
# them (every root-level file counts, including the packed LICENSE, README and
# notices), embedded eng/ resources, the analyzer project and baseline that
# Directory.Build.props adds to every compile, and the job's .NET setup.
# The job's server build compiles project references that Build & Format
# Check already compiles in the same run.
CORE_PACKAGE_INPUTS = re.compile(
    r"^(src/Honua\.(Core|Analyzers)/|src/[^/]+$|[^/]+$|eng/|scripts/ci/analyzer-baseline/"
    r"|\.github/actions/setup-dotnet-ci/)"
)


def affected_max_parallel(selected_shards: int) -> int:
    return AFFECTED_WIDTH


def affected_run_schedules_shards(reason: str) -> bool:
    """Selective affected runs skip the no-path-match smoke shards.

    `no_path_match` means no watched source path matched, so the configured
    default shards do not own the diff. The scheduled full run keeps them.
    """
    return reason != "no_path_match"


def core_package_required(paths: list[str]) -> bool:
    """Run the Core package proof when any path is a Core package input.

    An empty list is not evidence that the package is unchanged, so it runs.
    """
    usable = [path for path in paths if path]
    if not usable:
        return True
    return any(CORE_PACKAGE_INPUTS.match(path) for path in usable)


def main(argv: list[str] | None = None) -> None:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ["--core-package"]:
        paths = [line.strip() for line in sys.stdin if line.strip()]
        print("true" if core_package_required(paths) else "false")
        return
    if argv[:1] == ["--schedule-shards"]:
        reason = ""
        selective = False
        tokens = iter(argv[1:])
        for token in tokens:
            if token == "--reason":
                reason = next(tokens)
            elif token == "--selective":
                selective = next(tokens) == "true"
            else:
                raise SystemExit(f"unknown argument {token}")
        # Non-selective and full runs keep the router result, smoke shards included.
        scheduled = affected_run_schedules_shards(reason) if selective else True
        print("true" if scheduled else "false")
        return
    if argv:
        raise SystemExit(f"unknown argument {argv[0]}")
    print(affected_max_parallel(len(json.load(sys.stdin))))


if __name__ == "__main__":
    main()
