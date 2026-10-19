#!/usr/bin/env python3
"""Keep the complete FeatureServer parent through a whole-class capacity cut."""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / ".github/ci-shards.json").read_text())
EVIDENCE = json.loads((ROOT / "docs/internal/ci/featureserver-shard-capacity-20261018.json").read_text())
ORIGINAL = EVIDENCE["originalShard"]
NAMES = ("FeatureServer Editing and Relationships", "FeatureServer Query Services and Replication")
CHILDREN = [next(s for s in CONFIG["shards"] if s["name"] == name) for name in NAMES]
# The parent can execute only this assembly. Keep every registered source root
# for it; walking other assemblies cannot add candidates to selection_pool.
# The independent full-repository coverage guard still runs in the CI router.
MODULE.TEST_PROJECT_DIRS = {
    root: project for root, project in MODULE.TEST_PROJECT_DIRS.items()
    if project == ORIGINAL["csproj"]
}
assert MODULE.TEST_PROJECT_DIRS, "The original test assembly has no registered source roots."
CLASSES = MODULE.enumerate_test_classes()
METHODS = MODULE.selection_pool(CLASSES, ORIGINAL["csproj"])
PARENT = MODULE._FilterParser(ORIGINAL["filter"]).parse()
EXPECTED = {method for method in METHODS if MODULE._eval(PARENT, method)}
PREFIXES = tuple(EVIDENCE["editingClassPrefixes"])
EDITING = {method for method in EXPECTED if method.startswith(PREFIXES)}
assert EDITING and EDITING < EXPECTED, "Both actual source partitions must be nonempty."
assert EVIDENCE["qualification"] == "not-evaluated" and EVIDENCE["trxPresent"] is False
assert EVIDENCE["historicalSizing"]["sourceDirty"] is True, "Do not relabel historical sizing as clean proof."


def check(children):
    parsed = [MODULE._FilterParser(child["filter"]).parse() for child in children]
    selected = [{method for method in METHODS if MODULE._eval(expr, method)} for expr in parsed]
    assert all(selected), "Empty child."
    assert set.union(*selected) == EXPECTED, "Lost original or leaked foreign methods."
    assert sum(map(len, selected)) == len(EXPECTED), "Duplicate method ownership."
    assert selected[0] == EDITING, "The measured editing class families changed ownership."
    for entry in CLASSES.values():
        if entry["csproj"] != ORIGINAL["csproj"]:
            continue
        fqn = entry["fqn"]
        assert sum(MODULE._eval(expr, fqn) for expr in parsed) == int(MODULE._eval(PARENT, fqn)), \
            f"Class ownership changed: {fqn}"
    for child in children:
        for field in ("paths", "csproj", "max_cpu_count", "test_timeout_minutes", "timeout_minutes",
                      "upload_operator_eval_report", "upload_odata_evidence"):
            assert child[field] == ORIGINAL[field], f"Original {field} changed."
    # A future method name cannot move an unrelated whole class into the cut.
    probes = {
        "Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.FutureEndpointTests.FeatureServerApplyEdits": 1,
        "Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.FeatureServerApplyEditsFutureTests.Check": 0,
    }
    for method, owner in probes.items():
        assert MODULE._eval(PARENT, method)
        assert [i for i, expr in enumerate(parsed) if MODULE._eval(expr, method)] == [owner]
    return list(map(len, selected))


def check_registration(config):
    partitions = [p for p in config["shard_partitions"] if set(NAMES) & set(p["children"])]
    assert len(partitions) == 1 and partitions[0] == {
        "name": ORIGINAL["name"], "csproj": ORIGINAL["csproj"],
        "filter": ORIGINAL["filter"], "children": list(NAMES)
    }, "Both children need the unchanged legacy parent contract."
    assert not any(s["name"] == ORIGINAL["name"] for s in config["shards"])
    for override in config["targeted_override_prefixes"]:
        assert ORIGINAL["name"] not in override["shards"], "Stale parent override."
        if any(name in override["shards"] for name in NAMES):
            assert all(name in override["shards"] for name in NAMES), "Only half the override survived."
    assert len(config["shards"]) == len({s["shard_name"] for s in config["shards"]})


counts = check(CHILDREN)
check_registration(CONFIG)
first = sorted(EDITING)[0]
foreign = sorted(set(METHODS) - EXPECTED)[0]
for mutation in ("lost", "duplicated", "foreign", "trigger", "budget", "job-budget", "parallelism", "project", "swapped"):
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
    elif mutation == "job-budget":
        changed[1]["timeout_minutes"] += 1
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
for mutation in ("missing-child", "legacy-filter", "override"):
    changed = copy.deepcopy(CONFIG)
    if mutation == "override":
        override = next(x for x in changed["targeted_override_prefixes"] if NAMES[0] in x["shards"])
        override["shards"].remove(NAMES[1])
    else:
        parent = next(p for p in changed["shard_partitions"] if p["name"] == ORIGINAL["name"])
        if mutation == "missing-child":
            parent["children"].pop()
        else:
            parent["filter"] += "&FullyQualifiedName!=" + first
    try:
        check_registration(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"{mutation} mutation was accepted")
print(f"featureserver-capacity-partition=ok original_methods={len(EXPECTED)} "
      f"child_methods={counts} negatives=12 budgets=unchanged roster_shards={len(CONFIG['shards'])}")
