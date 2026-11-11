#!/usr/bin/env python3
"""Preserve governance methods, paths and budgets across its capacity split."""
import copy
import importlib.util
import json
from pathlib import Path

from shard_capacity_partition import PartitionSource, check_method_partition

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())
CONTRACT = json.loads((ROOT / "scripts/ci/fixtures/governance-capacity-contract.json").read_text())
ORIGINAL = CONTRACT["originalShard"]
NAMES = CONTRACT["childNames"]
CHILDREN = [next(s for s in CONFIG["shards"] if s["name"] == name) for name in NAMES]
PROJECT = ORIGINAL["csproj"] or MODULE.DEFAULT_CSPROJ
MODULE.TEST_PROJECT_DIRS = {root: project for root, project in MODULE.TEST_PROJECT_DIRS.items() if project == PROJECT}
assert MODULE.TEST_PROJECT_DIRS
SOURCE = PartitionSource(MODULE, MODULE.enumerate_test_classes())
SELECTED = check_method_partition(SOURCE, ORIGINAL, CHILDREN)
ANCESTOR = next(p for p in CONFIG["shard_partitions"] if p["name"] == "Server Features Admin Platform and Governance")
assert all(name in ANCESTOR["children"] for name in NAMES)
assert ORIGINAL["name"] not in ANCESTOR["children"]
for override in CONFIG["targeted_override_prefixes"]:
    assert ORIGINAL["name"] not in override["shards"]

for mutation in ("hole", "duplicate", "budget", "paths"):
    changed = copy.deepcopy(CHILDREN)
    if mutation == "hole":
        changed[0]["filter"] = "FullyQualifiedName=Missing"
    elif mutation == "duplicate":
        changed[1]["filter"] = changed[0]["filter"]
    elif mutation == "budget":
        changed[0]["test_timeout_minutes"] += 1
    else:
        changed[0]["paths"] = []
    try:
        check_method_partition(SOURCE, ORIGINAL, changed)
    except AssertionError:
        continue
    raise AssertionError(f"Accepted {mutation} mutation")
print(f"governance-capacity-partition=ok child_methods={list(map(len, SELECTED))} "
      "mutation_controls=4 budgets=unchanged")
