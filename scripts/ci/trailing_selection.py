#!/usr/bin/env python3
"""Size and identify an affected trunk run: backlog limit, shard concurrency, run title.

An affected dispatch shares the org's hosted runners with PR Gate. A narrow
selection runs at the PR-safe width. A selection still above the backlog limit
after newest-merge narrowing runs at twice that width so it finishes before the
next twice-daily full run. Full and pull-request runs use the workflow's full
width and do not consult this module.

Reads the selected shard matrix (JSON array) on stdin and prints the width.
"""
import json
import sys

SHARD_BACKLOG_LIMIT = 30
# run-name of every trunk-affected.yml dispatch; full CI runs never carry it.
AFFECTED_RUN_TITLE_PREFIX = "CI affected "
AFFECTED_WIDTH = 6
BROAD_AFFECTED_WIDTH = 12


def affected_max_parallel(selected_shards: int) -> int:
    if selected_shards > SHARD_BACKLOG_LIMIT:
        return BROAD_AFFECTED_WIDTH
    return AFFECTED_WIDTH


def main() -> None:
    print(affected_max_parallel(len(json.load(sys.stdin))))


if __name__ == "__main__":
    main()
