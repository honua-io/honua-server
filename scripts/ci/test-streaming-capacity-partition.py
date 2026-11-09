#!/usr/bin/env python3
"""Conserve streaming methods and execution policy across transport/complement shards."""
import copy
import importlib.util
import json
from pathlib import Path

from shard_capacity_partition import PartitionSource, check_method_partition

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("coverage", REPOSITORY_ROOT / "scripts/ci/check-server-test-shard-coverage.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
config = json.loads((REPOSITORY_ROOT / ".github/ci-shards.json").read_text())
original = json.loads((REPOSITORY_ROOT / "scripts/ci/fixtures/streaming-capacity-contract.json").read_text())["originalShard"]
names = ["Server Features Streaming Transports", original["name"]]
children = [next(shard for shard in config["shards"] if shard["name"] == name) for name in names]
classes = MODULE.enumerate_test_classes()

def verify(shards):
    owners = check_method_partition(PartitionSource(MODULE, classes), original, shards)
    parsed = {shard["name"]: MODULE._FilterParser(shard["filter"]).parse() for shard in shards}
    endpoint = "Honua.Server.Tests.Features.Streaming.FeatureStreamEndpointsTests"
    for method, owner in (("Sse_FutureCase", names[0]), ("WebSocket_FutureCase", names[0]),
                          ("FutureCase_Sse_WebSocket", names[1])):
        assert [name for name in names if MODULE._eval(parsed[name], endpoint + "." + method)] == [owner]
    admission = "Honua.Server.Tests.Features.Streaming.FeatureStreamAdmissionTests.WebSocket_FutureCase"
    assert [name for name in names if MODULE._eval(parsed[name], admission)] == [names[1]]
    return owners

selected = verify(children)
partitions = [partition for partition in config["shard_partitions"] if set(names) & set(partition["children"])]
assert len(partitions) == 1
assert partitions[0]["name"] == "Server Features Misc"
assert partitions[0]["granularity"] == "method"
assert all(name in partitions[0]["children"] for name in names)
for field in ("artifact_suffix", "log_name", "shard_name"):
    assert len({shard[field] for shard in config["shards"]}) == len(config["shards"]), field
for mutation in ("missing", "duplicate", "foreign", "budget", "trigger"):
    broken = copy.deepcopy(children)
    if mutation == "missing":
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
        verify(broken)
    except AssertionError:
        pass
    else:
        raise AssertionError(f"Accepted {mutation} streaming partition.")
print(f"streaming-capacity-partition=ok methods={list(map(len, selected))} negatives=5")
