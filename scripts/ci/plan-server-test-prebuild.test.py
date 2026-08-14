#!/usr/bin/env python3
"""Executable fixtures for the bounded pre-review build planner."""

from __future__ import annotations

import importlib.util
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("plan-server-test-prebuild.py")
SPEC = importlib.util.spec_from_file_location("prebuild_plan", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

CONFIG = {"contract_version": 1, "max_projects_per_head": 2, "max_selected_shards": 10}
SHARDS = {
    "shards": [
        {"name": "server-a", "filter": "A"},
        {"name": "server-b", "filter": "B"},
        {"name": "server-c", "filter": "C"},
        {"name": "odata-a", "csproj": "tests/OData.csproj", "filter": "D"},
        {"name": "odata-b", "csproj": "tests/OData.csproj", "filter": "E"},
        {"name": "ogc-a", "csproj": "tests/Ogc.csproj", "filter": "F"},
        {"name": "ogc-b", "csproj": "tests/Ogc.csproj", "filter": "G"},
    ]
}
REGISTRY = {
    "projects": [
        {"csproj": MODULE.DEFAULT_PROJECT, "artifact_suffix": "server"},
        {"csproj": "tests/OData.csproj", "artifact_suffix": "odata"},
        {"csproj": "tests/Ogc.csproj", "artifact_suffix": "ogc"},
    ]
}


def plan(names: list[str]) -> dict:
    return MODULE.build_plan(CONFIG, SHARDS, REGISTRY, {"shards": names, "reason": "fixture"})


value = plan(["server-a", "server-b", "server-c", "odata-a", "odata-b", "ogc-a", "ogc-b"])
assert [item["identity"] for item in value["producers"]] == ["server", "odata"]
assert value["producers"][0]["selected_shard_count"] == 3
assert value["deferred_repeated_projects"] == ["tests/Ogc.csproj"]
assert len(value["consumers"]) == 5

value = plan(["server-a", "odata-a", "ogc-a"])
assert value["producers"] == [] and value["consumers"] == []

try:
    plan(["missing"])
except ValueError as error:
    assert "unknown names" in str(error)
else:
    raise AssertionError("unknown shard was accepted")

bad_registry = {"projects": REGISTRY["projects"][:-1]}
try:
    MODULE.build_plan(CONFIG, SHARDS, bad_registry, {"shards": ["ogc-a", "ogc-b"]})
except ValueError as error:
    assert "not registered" in str(error)
else:
    raise AssertionError("unregistered repeated project was accepted")

print("server-test-prebuild-plan=ok")
