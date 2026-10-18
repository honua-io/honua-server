#!/usr/bin/env python3
"""Preserve every runnable WMS method across the measured class/complement cut."""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())
EVIDENCE = json.loads((ROOT / "docs/internal/ci/wms-shard-capacity-20261018.json").read_text())
ORIGINAL = EVIDENCE["originalShard"]
NAMES = ("OGC Classic Maps Core", "OGC Classic Maps Supporting")
CHILDREN = [next(s for s in CONFIG["shards"] if s["name"] == name) for name in NAMES]
PROJECT = "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj"
CLASS = "Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms.OgcClassicWmsTests"
METHODS = MODULE.selection_pool(MODULE.enumerate_test_classes(), PROJECT)
PARENT = MODULE._FilterParser(ORIGINAL["filter"]).parse()
EXPECTED = {method for method in METHODS if MODULE._eval(PARENT, method)}
MAIN = {method for method in EXPECTED if method.startswith(CLASS + ".")}
assert MAIN and MAIN < EXPECTED, "Both actual source partitions must be nonempty."
assert EVIDENCE["trxPresent"] is False and EVIDENCE["qualification"] == "not-evaluated"


def check(children):
    selected = [{method for method in METHODS
                 if MODULE._eval(MODULE._FilterParser(child["filter"]).parse(), method)}
                for child in children]
    assert all(selected), "Empty child."
    assert set.union(*selected) == EXPECTED, "Lost original or leaked foreign methods."
    assert sum(map(len, selected)) == len(EXPECTED), "Duplicate method ownership."
    assert selected[0] == MAIN, "The measured main class changed ownership."
    for child in children:
        for field in ("paths", "csproj", "max_cpu_count", "test_timeout_minutes", "timeout_minutes",
                      "upload_operator_eval_report", "upload_odata_evidence"):
            assert child[field] == ORIGINAL[field], f"Original {field} changed."
    return list(map(len, selected))


counts = check(CHILDREN)
parents = [p for p in CONFIG["shard_partitions"] if set(NAMES) & set(p["children"])]
assert len(parents) == 1 and parents[0] == {
    "name": ORIGINAL["name"], "filter": ORIGINAL["filter"], "children": list(NAMES)
}, "Both children must have one unchanged legacy parent contract."
assert not any(s["name"] == ORIGINAL["name"] for s in CONFIG["shards"])
for override in CONFIG["targeted_override_prefixes"]:
    assert ORIGINAL["name"] not in override["shards"], "Stale parent override."
    if any(name in override["shards"] for name in NAMES):
        assert all(name in override["shards"] for name in NAMES), "Only half the override survived."
# Registered source/test routing remains part of the normal PR Gate router controls.
assert len(CONFIG["shards"]) == len({s["shard_name"] for s in CONFIG["shards"]})
first = sorted(MAIN)[0]
foreign = sorted(set(METHODS) - EXPECTED)[0]
for mutation in ("lost", "duplicated", "foreign", "trigger", "budget", "parallelism", "project", "swapped"):
    changed = copy.deepcopy(CHILDREN)
    if mutation == "lost":
        changed[0]["filter"] += "&FullyQualifiedName!=" + first
    elif mutation == "duplicated":
        changed[1]["filter"] += "|FullyQualifiedName=" + first
    elif mutation == "foreign":
        changed[1]["filter"] += "|FullyQualifiedName=" + foreign
    elif mutation == "trigger":
        changed[0]["paths"] = changed[0]["paths"][1:]
    elif mutation == "budget":
        changed[0]["test_timeout_minutes"] += 1
    elif mutation == "parallelism":
        changed[1]["max_cpu_count"] = "2"
    elif mutation == "project":
        changed[1]["csproj"] = "tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj"
    else:
        changed[0]["filter"], changed[1]["filter"] = changed[1]["filter"], changed[0]["filter"]
    try:
        check(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"{mutation} mutation was accepted")
print(f"wms-capacity-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=8 budgets=unchanged roster_shards={len(CONFIG['shards'])}")
