#!/usr/bin/env python3
"""Conserve selections and execution policies for full-run capacity cuts."""
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
CONTRACTS = json.loads((ROOT / "scripts/ci/fixtures/full-run-capacity-contracts.json").read_text())
CLASSES = MODULE.enumerate_test_classes()


def verify(original, children):
    original = {**original, "csproj": original.get("csproj", "")}
    children = [{**child, "csproj": child.get("csproj", "")} for child in children]
    selected = check_method_partition(PartitionSource(MODULE, CLASSES), original, children)
    parent = MODULE._FilterParser(original["filter"]).parse()
    parsed = [MODULE._FilterParser(child["filter"]).parse() for child in children]
    project = original["csproj"] or MODULE.DEFAULT_CSPROJ
    for entry in CLASSES.values():
        if entry["csproj"] != project:
            continue
        for method in entry["methods"] | {"FutureRegressionTest"}:
            name = entry["fqn"] + "." + method
            assert sum(MODULE._eval(expression, name) for expression in parsed) == int(MODULE._eval(parent, name)), name
    for child in children:
        assert child["dispatch_rank"] == original["dispatch_rank"], "Unmeasured dispatch rank changed."
    return selected


for contract in CONTRACTS:
    original = contract["originalShard"]
    names = contract["children"]
    children = [next(shard for shard in CONFIG["shards"] if shard["name"] == name) for name in names]
    selected = verify(original, children)
    for mutation in ("lost", "duplicate", "foreign", "budget", "trigger", "report"):
        broken = copy.deepcopy(children)
        if mutation == "lost":
            broken[0]["filter"] += "&FullyQualifiedName!=" + min(selected[0])
        elif mutation == "duplicate":
            broken[1]["filter"] = original["filter"]
        elif mutation == "foreign":
            broken[0]["filter"] += "|FullyQualifiedName~Honua.Server.Tests."
        elif mutation == "budget":
            broken[0]["test_timeout_minutes"] += 1
        elif mutation == "trigger":
            broken[0]["paths"] = broken[0]["paths"][1:]
        else:
            for child in broken:
                child["upload_operator_eval_report"] = True
        try:
            verify(original, broken)
        except AssertionError:
            continue
        raise AssertionError(f"Accepted {mutation}: {original['name']}")
    assert not any(shard["name"] == original["name"] for shard in CONFIG["shards"])
    for partition in CONFIG["shard_partitions"]:
        assert original["name"] not in partition["children"]
        if set(names) & set(partition["children"]):
            assert set(names) <= set(partition["children"])
    for override in CONFIG["targeted_override_prefixes"]:
        assert original["name"] not in override["shards"]
        if set(names) & set(override["shards"]):
            assert set(names) <= set(override["shards"])
    print(f"full-run-capacity=ok parent={original['name']} methods={list(map(len, selected))} negatives=6")

for field in ("artifact_suffix", "log_name", "shard_name"):
    assert len({shard[field] for shard in CONFIG["shards"]}) == len(CONFIG["shards"]), field

# The fixed-name operator-eval-report upload fails without eval-report.json, so exactly one
# shard uploads it and that shard runs the harness test that writes the report.
REPORT_TEST = "Honua.Server.Tests.Features.Eval.EvalHarnessTests"
uploaders = [shard for shard in CONFIG["shards"] if shard["upload_operator_eval_report"]]
assert len(uploaders) == 1, [shard["name"] for shard in uploaders]
report_filter = MODULE._FilterParser(uploaders[0]["filter"]).parse()
report_methods = [entry["fqn"] + "." + method for entry in CLASSES.values()
                  if entry["fqn"] == REPORT_TEST for method in entry["methods"]]
assert report_methods and all(MODULE._eval(report_filter, name) for name in report_methods), uploaders[0]["name"]
print(f"operator-eval-report=ok shard={uploaders[0]['name']}")
