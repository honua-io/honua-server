#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
from pathlib import Path

SCRIPT = Path(__file__).with_name("plan-server-test-prebuild-benchmark.py")
SPEC = importlib.util.spec_from_file_location("prebuild_benchmark_plan", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

CONFIG = {
    "contract_version": 1,
    "profiles": [
        {"name": "two-same-project", "shards": ["server-a", "server-b"]},
        {
            "name": "five-hybrid-project",
            "shards": ["server-a", "server-b", "geo", "odata", "ogc"],
        },
    ],
    "shards": [
        {"name": "server-a", "project": "server.csproj"},
        {"name": "server-b", "project": "server.csproj"},
        {"name": "geo", "project": "geo.csproj"},
        {"name": "odata", "project": "odata.csproj"},
        {"name": "ogc", "project": "ogc.csproj"},
    ],
}
REGISTRY = {
    "projects": [
        {"csproj": name, "artifact_suffix": name.removesuffix(".csproj"), "proof_filter": name}
        for name in ("server.csproj", "geo.csproj", "odata.csproj", "ogc.csproj")
    ]
}

two = MODULE.build_plan(CONFIG, REGISTRY, "two-same-project")
assert len(two["baseline"]) == 2
assert all(item["reuse_expected"] for item in two["candidates"])
assert two["reused_projects"] == ["server.csproj"]

five = MODULE.build_plan(CONFIG, REGISTRY, "five-hybrid-project")
assert len(five["candidates"]) == 5
assert [item["identity"] for item in five["candidates"] if item["reuse_expected"]] == [
    "server-a",
    "server-b",
]

try:
    MODULE.build_plan(CONFIG, {"projects": REGISTRY["projects"][:-1]}, "five-hybrid-project")
except ValueError as error:
    assert "unregistered" in str(error)
else:
    raise AssertionError("unregistered project was accepted")

print("server-test-prebuild-benchmark-plan=ok")
