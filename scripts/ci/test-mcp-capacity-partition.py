#!/usr/bin/env python3
"""Preserve every MCP class while isolating the measured endpoint class."""
import copy
import importlib.util
import json
from pathlib import Path

from shard_capacity_partition import check_partition

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())
CONTRACT = json.loads((ROOT / "scripts/ci/fixtures/mcp-capacity-contract.json").read_text())
ORIGINAL = CONTRACT["originalShard"]
NAMES = ("MCP Endpoints", "MCP and Sessions")
CHILDREN = [next(s for s in CONFIG["shards"] if s["name"] == name) for name in NAMES]
MODULE.TEST_PROJECT_DIRS = {
    root: project for root, project in MODULE.TEST_PROJECT_DIRS.items() if project == ORIGINAL["csproj"]
}
assert MODULE.TEST_PROJECT_DIRS
CLASSES = MODULE.enumerate_test_classes()
METHODS = MODULE.selection_pool(CLASSES, ORIGINAL["csproj"])
PARENT = MODULE._FilterParser(ORIGINAL["filter"]).parse()
EXPECTED = {method for method in METHODS if MODULE._eval(PARENT, method)}
ENDPOINTS = {method for method in EXPECTED if method.startswith(CONTRACT["endpointClass"] + ".")}
assert ENDPOINTS and ENDPOINTS < EXPECTED


def check_registration(config):
    parents = [p for p in config["shard_partitions"] if set(NAMES) & set(p["children"])]
    assert len(parents) == 1, "Partition children must have exactly one parent."
    assert parents[0] == CONTRACT["parentPartition"], "The legacy parent contract changed."
    for override in config["targeted_override_prefixes"]:
        if ORIGINAL["name"] in override["shards"]:
            assert "MCP Endpoints" in override["shards"], "Only half the override survived."
    for field in ("artifact_suffix", "log_name", "shard_name"):
        assert len({s[field] for s in config["shards"]}) == len(config["shards"]), field


selected = check_partition(MODULE, CLASSES, ORIGINAL, CHILDREN)
assert selected[0] == ENDPOINTS, "The measured endpoint class changed ownership."
counts = list(map(len, selected))
check_registration(CONFIG)
duplicate = copy.deepcopy(CONFIG)
duplicate["shard_partitions"].append({"name": "duplicate", "filter": ORIGINAL["filter"], "children": list(NAMES)})
try:
    check_registration(duplicate)
except AssertionError:
    pass
else:
    raise AssertionError("Duplicate parent was accepted.")
print(f"mcp-capacity-partition=ok original_methods={len(EXPECTED)} child_methods={counts} "
      f"classes={sum(MODULE._eval(PARENT, entry['fqn']) for entry in CLASSES.values())} "
      f"duplicate_parent_rejected=true budgets=unchanged")
