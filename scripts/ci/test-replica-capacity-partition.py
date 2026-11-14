#!/usr/bin/env python3
"""Conserve the FeatureServer Replica test surface across whole-class shards."""
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
CONTRACT = json.loads((ROOT / "scripts/ci/fixtures/replica-capacity-contract.json").read_text())
ORIGINAL = CONTRACT["originalShard"]
NAMES = ("FeatureServer Replica Management", "FeatureServer Replica Sync", ORIGINAL["name"])
CHILDREN = [next(shard for shard in CONFIG["shards"] if shard["name"] == name) for name in NAMES]
MODULE.TEST_PROJECT_DIRS = {
    root: project for root, project in MODULE.TEST_PROJECT_DIRS.items() if project == ORIGINAL["csproj"]
}
assert MODULE.TEST_PROJECT_DIRS
CLASSES = MODULE.enumerate_test_classes()


def verify_conservation(children):
    selected = check_method_partition(PartitionSource(MODULE, CLASSES), ORIGINAL, children)
    parent = MODULE._FilterParser(ORIGINAL["filter"]).parse()
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


selected = verify_conservation(CHILDREN)
parents = [parent for parent in CONFIG["shard_partitions"] if set(NAMES) & set(parent["children"])]
# Packing joins the legacy Tiles/Replica parent with GP/NAS. The packing
# contract verifies that complete union; this fixture still freezes Replica's
# original selection and checks its three children independently above.
PACKING = json.loads((ROOT / "scripts/ci/fixtures/soap-mvt-packing.json").read_text())
packed_name = "GeoServices SOAP and MVT"
member_names = {member["name"] for member in PACKING["members"]}
expected_parent = {
    "name": "GPServer NAServer Tiles and Replica capacity partition",
    "csproj": ORIGINAL["csproj"],
    "filter": "|".join("(" + parent["filter"] + ")" for parent in PACKING["partitions"]),
    "children": list(dict.fromkeys(
        packed_name if child in member_names else child
        for parent in PACKING["partitions"] for child in parent["children"])),
    "granularity": "method",
}
assert len(parents) == 1
assert {**parents[0], "children": set(parents[0]["children"])} == {
    **expected_parent, "children": set(expected_parent["children"])
}, "The combined parent contract changed."
for field in ("artifact_suffix", "log_name", "shard_name"):
    assert len({shard[field] for shard in CONFIG["shards"]}) == len(CONFIG["shards"]), field

for mutation in ("duplicate", "missing", "foreign", "budget"):
    broken = copy.deepcopy(CHILDREN)
    if mutation == "duplicate":
        broken[2]["filter"] = ORIGINAL["filter"]
    elif mutation == "missing":
        broken[0]["filter"] = f"({broken[0]['filter']})&FullyQualifiedName!~ReplicaConflictReviewEndpointTests"
    elif mutation == "foreign":
        broken[0]["filter"] += "|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.MvtTileEndpointTests"
    else:
        broken[0]["test_timeout_minutes"] += 1
    try:
        verify_conservation(broken)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"Accepted {mutation} partition.")

print(f"replica-capacity-partition=ok original_methods={sum(map(len, selected))} "
      f"child_methods={list(map(len, selected))} mutation_controls=4 budgets=unchanged")
