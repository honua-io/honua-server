#!/usr/bin/env python3
"""Conserve timed-out parent selections, routing and execution policies."""
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
CONTRACTS = json.loads((ROOT / "scripts/ci/fixtures/live-output-capacity-contracts.json").read_text())
CLASSES = MODULE.enumerate_test_classes()


def verify(original, children):
    selected = check_method_partition(PartitionSource(MODULE, CLASSES), original, children)
    parent = MODULE._FilterParser(original["filter"]).parse()
    parsed = [MODULE._FilterParser(child["filter"]).parse() for child in children]
    for entry in CLASSES.values():
        fqn = entry["fqn"]
        owners = [MODULE._eval(expression, fqn) for expression in parsed]
        assert sum(owners) == int(MODULE._eval(parent, fqn)), fqn
        for method in entry["methods"] | {"FutureRegressionTest"}:
            name = fqn + "." + method
            actual = [MODULE._eval(expression, name) for expression in parsed]
            assert sum(actual) == int(MODULE._eval(parent, name)), name
            if MODULE._eval(parent, fqn):
                assert actual == owners, f"Class split: {name}"
    return selected


def reject_mutations(original, children):
    selected = verify(original, children)
    for mutation in ("lost", "duplicate", "foreign", "budget", "trigger"):
        broken = copy.deepcopy(children)
        if mutation == "lost":
            broken[0]["filter"] += "&FullyQualifiedName!=" + min(selected[0])
        elif mutation == "duplicate":
            broken[1]["filter"] = original["filter"]
        elif mutation == "foreign":
            broken[0]["filter"] += "|FullyQualifiedName~Honua.Server.Tests.Features.Admin."
        elif mutation == "budget":
            broken[0]["test_timeout_minutes"] += 1
        else:
            broken[0]["paths"] = broken[0]["paths"][1:]
        try:
            verify(original, broken)
        except AssertionError:
            continue
        raise AssertionError(f"Accepted {mutation} partition for {original['name']}")
    return list(map(len, selected))


for contract in CONTRACTS:
    original = contract["originalShard"]
    names = contract["children"]
    children = [next(shard for shard in CONFIG["shards"] if shard["name"] == name) for name in names]
    counts = reject_mutations(original, children)
    assert not any(shard["name"] == original["name"] for shard in CONFIG["shards"])
    for partition in CONFIG["shard_partitions"]:
        if set(names) & set(partition["children"]):
            assert set(names) <= set(partition["children"])
    for override in CONFIG["targeted_override_prefixes"]:
        assert original["name"] not in override["shards"]
        if set(names) & set(override["shards"]):
            assert set(names) <= set(override["shards"])
    print(f"live-output-capacity=ok parent={original['name']} methods={counts} negatives=5")

for field in ("artifact_suffix", "log_name", "shard_name"):
    assert len({shard[field] for shard in CONFIG["shards"]}) == len(CONFIG["shards"]), field
