#!/usr/bin/env python3
"""Preserve WMS selection, routing and budgets when its filters share a job."""
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
EVIDENCE = json.loads((ROOT / "docs/internal/ci/wms-shard-capacity-20261018.json").read_text())
ORIGINAL = EVIDENCE["originalShard"]
NAMES = ("OGC Classic Maps Integration",)
CHILDREN = [next(s for s in CONFIG["shards"] if s["name"] == name) for name in NAMES]
PROJECT = "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj"
CLASS = "Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms.OgcClassicWmsTests"
CLASSES = MODULE.enumerate_test_classes()
METHODS = MODULE.selection_pool(CLASSES, PROJECT)
PARENT = MODULE._FilterParser(ORIGINAL["filter"]).parse()
EXPECTED = {method for method in METHODS if MODULE._eval(PARENT, method)}
MAIN = {method for method in EXPECTED if method.startswith(CLASS + ".")}
assert MAIN and MAIN < EXPECTED, "Both actual source partitions must be nonempty."
assert EVIDENCE["trxPresent"] is False and EVIDENCE["qualification"] == "not-evaluated"


def check(children):
    selected = check_partition(PartitionSource(MODULE, CLASSES), ORIGINAL, children)
    assert selected[0] == EXPECTED, "The WMS method inventory changed."
    return list(map(len, selected))


counts = check(CHILDREN)
parents = [p for p in CONFIG["shard_partitions"] if set(NAMES) & set(p["children"])]
assert len(parents) == 1 and parents[0] == {
    "name": ORIGINAL["name"], "filter": ORIGINAL["filter"], "children": list(NAMES)
}, "The merged shard must preserve the original selection contract."
assert not any(s["name"] == ORIGINAL["name"] for s in CONFIG["shards"])
for override in CONFIG["targeted_override_prefixes"]:
    assert ORIGINAL["name"] not in override["shards"], "Stale parent override."
    if any(name in override["shards"] for name in NAMES):
        assert all(name in override["shards"] for name in NAMES), "The override lost its owner."
# Registered source/test routing remains part of the normal PR Gate router controls.
assert len(CONFIG["shards"]) == len({s["shard_name"] for s in CONFIG["shards"]})
first = sorted(MAIN)[0]
foreign = sorted(set(METHODS) - EXPECTED)[0]
for mutation in ("lost", "duplicated", "foreign", "trigger", "budget", "parallelism", "project", "narrowed"):
    changed = copy.deepcopy(CHILDREN)
    if mutation == "lost":
        changed[0]["filter"] = "(" + changed[0]["filter"] + ")&FullyQualifiedName!=" + first
    elif mutation == "duplicated":
        changed.append(copy.deepcopy(changed[0]))
    elif mutation == "foreign":
        changed[0]["filter"] += "|FullyQualifiedName=" + foreign
    elif mutation == "trigger":
        changed[0]["paths"] = changed[0]["paths"][1:]
    elif mutation == "budget":
        changed[0]["test_timeout_minutes"] += 1
    elif mutation == "parallelism":
        changed[0]["max_cpu_count"] = "2"
    elif mutation == "project":
        changed[0]["csproj"] = "tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj"
    else:
        changed[0]["filter"] = "FullyQualifiedName~" + CLASS
    try:
        check(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"{mutation} mutation was accepted")
print(f"wms-capacity-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=8 budgets=unchanged roster_shards={len(CONFIG['shards'])}")
