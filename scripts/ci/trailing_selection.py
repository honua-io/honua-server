#!/usr/bin/env python3
"""Identify affected trunk runs and cap their heavy-runner use beside PR Gate.

Admission bounds the job count before fanout. Full runs retain their normal
width; optional affected runs use at most two heavy shard runners. A selective
affected run does not schedule the router's no-path-match smoke shards, and it
skips the Honua.Core package proof when every changed file is docs, workflow,
CI script or test. The twice-daily full run keeps both.
"""
import json
import re
import sys

# run-name of every trunk-affected.yml dispatch; full CI runs never carry it.
AFFECTED_RUN_TITLE_PREFIX = "CI affected "
AFFECTED_WIDTH = 2
# Anything outside this set can change the packed Core package or the server
# build it checks. Unknown paths fail closed to running the job.
CORE_PACKAGE_SKIP = re.compile(
    r"^(docs/|README\.md$|\.github/workflows/[^/]+\.yml$|scripts/ci/|tests/|test/)"
)


def affected_max_parallel(selected_shards: int) -> int:
    return AFFECTED_WIDTH


def affected_run_schedules_shards(reason: str) -> bool:
    """Selective affected runs skip the no-path-match smoke shards.

    `no_path_match` means no watched source path matched, so the configured
    default shards do not own the diff. The scheduled full run keeps them.
    """
    return reason != "no_path_match"


def full_run_schedules_shards(reason: str) -> bool:
    """Pull requests and full runs keep every shard the router returned."""
    return isinstance(reason, str)


def core_package_required(paths: list[str]) -> bool:
    return any(path and CORE_PACKAGE_SKIP.match(path) is None for path in paths)


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
        scheduled = (
            affected_run_schedules_shards(reason)
            if selective
            else full_run_schedules_shards(reason))
        print("true" if scheduled else "false")
        return
    if argv:
        raise SystemExit(f"unknown argument {argv[0]}")
    print(affected_max_parallel(len(json.load(sys.stdin))))


if __name__ == "__main__":
    main()
