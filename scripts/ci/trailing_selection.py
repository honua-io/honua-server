#!/usr/bin/env python3
"""Identify affected trunk runs and reserve heavy-pool capacity for PR Gate.

Admission bounds the job count before fanout. Full runs retain their normal
width; optional affected runs use at most two heavy shard runners.
"""
import json
import sys

# run-name of every trunk-affected.yml dispatch; full CI runs never carry it.
AFFECTED_RUN_TITLE_PREFIX = "CI affected "
AFFECTED_WIDTH = 2


def affected_max_parallel(selected_shards: int) -> int:
    return AFFECTED_WIDTH


def main() -> None:
    print(affected_max_parallel(len(json.load(sys.stdin))))


if __name__ == "__main__":
    main()
