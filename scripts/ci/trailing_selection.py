#!/usr/bin/env python3
"""Size an affected trunk run: the shard backlog limit and the shard concurrency.

An affected dispatch shares the org's hosted runners with PR Gate. A narrow
selection runs at the PR-safe width. A selection still above the backlog limit
after newest-merge narrowing runs at twice that width so it finishes before the
next twice-daily full run. Full and pull-request runs keep the full width.

Reads the selected shard matrix (JSON array) on stdin and prints the width.
"""
import argparse
import json
import sys

SHARD_BACKLOG_LIMIT = 30
FULL_RUN_WIDTH = 24
AFFECTED_WIDTH = 6
BROAD_AFFECTED_WIDTH = 12


def max_parallel(selected_shards: int, affected_dispatch: bool) -> int:
    if not affected_dispatch:
        return FULL_RUN_WIDTH
    if selected_shards > SHARD_BACKLOG_LIMIT:
        return BROAD_AFFECTED_WIDTH
    return AFFECTED_WIDTH


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--affected-dispatch", choices=("true", "false"), required=True)
    arguments = parser.parse_args()
    matrix = json.load(sys.stdin)
    print(max_parallel(len(matrix), arguments.affected_dispatch == "true"))


if __name__ == "__main__":
    main()
