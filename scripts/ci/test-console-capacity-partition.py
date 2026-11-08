#!/usr/bin/env python3
"""Conserve the Console and Alerts test surface across whole-class shards."""
import copy
import importlib.util
import json
from pathlib import Path

from shard_capacity_partition import PartitionSource, check_partition

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())
CONTRACT = json.loads((ROOT / "scripts/ci/fixtures/console-capacity-contract.json").read_text())
ORIGINAL = CONTRACT["originalShard"]
NAMES = ("Server Features Console Sessions and Access", ORIGINAL["name"])
CHILDREN = [next(shard for shard in CONFIG["shards"] if shard["name"] == name) for name in NAMES]
MODULE.TEST_PROJECT_DIRS = {
    root: project for root, project in MODULE.TEST_PROJECT_DIRS.items() if project == ORIGINAL["csproj"]
}
assert MODULE.TEST_PROJECT_DIRS
CLASSES = MODULE.enumerate_test_classes()


def verify_conservation(children):
    return check_partition(PartitionSource(MODULE, CLASSES), ORIGINAL, children)


selected = verify_conservation(CHILDREN)
isolated = {
    method for method in MODULE.selection_pool(CLASSES, ORIGINAL["csproj"])
    if method.rsplit(".", 1)[0] in CONTRACT["isolatedClasses"]
}
assert selected[0] == isolated, "The measured Console classes changed ownership."
parents = [parent for parent in CONFIG["shard_partitions"] if set(NAMES) & set(parent["children"])]
assert parents == [CONTRACT["parentPartition"]], "The legacy parent contract changed."
for field in ("artifact_suffix", "log_name", "shard_name"):
    assert len({shard[field] for shard in CONFIG["shards"]}) == len(CONFIG["shards"]), field

for mutation in ("duplicate", "missing", "foreign", "budget"):
    broken = copy.deepcopy(CHILDREN)
    if mutation == "duplicate":
        broken[1]["filter"] = ORIGINAL["filter"]
    elif mutation == "missing":
        broken[0]["filter"] = f"({broken[0]['filter']})&FullyQualifiedName!~ConsoleSessionEndpointTests"
    elif mutation == "foreign":
        broken[0]["filter"] += "|FullyQualifiedName~Honua.Server.Tests.Features.Admin."
    else:
        broken[0]["test_timeout_minutes"] += 1
    try:
        verify_conservation(broken)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"Accepted {mutation} partition.")

print(f"console-capacity-partition=ok original_methods={sum(map(len, selected))} "
      f"child_methods={list(map(len, selected))} mutation_controls=4 budgets=unchanged")
