#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
from pathlib import Path

SCRIPT = Path(__file__).with_name("plan-server-test-prebuild-parity.py")
SPEC = importlib.util.spec_from_file_location("prebuild_parity", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

PROJECT = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
REGISTRY = {
    "projects": [
        {
            "csproj": PROJECT,
            "artifact_suffix": "server",
            "proof_filter": "FullyQualifiedName~ServerProofTests",
        }
    ]
}


def observation(producers: list[dict]) -> dict:
    return {"contract": MODULE.OBSERVATION_CONTRACT, "producers": producers}


valid = {
    "identity": "server",
    "project": PROJECT,
    "project_suffix": "server",
    "selected_shard_count": 3,
}
plan = MODULE.build_plan(observation([valid]), REGISTRY)
assert plan == {
    "contract": MODULE.BENCHMARK_CONTRACT,
    "profile": "exact-head-shadow:multi-shard",
    "baseline": [
        {
            "identity": "server",
            "project": PROJECT,
            "project_suffix": "server",
            "filter": "FullyQualifiedName~ServerProofTests",
            "reuse_expected": True,
        }
    ],
    "candidates": [
        {
            "identity": "server",
            "project": PROJECT,
            "project_suffix": "server",
            "filter": "FullyQualifiedName~ServerProofTests",
            "reuse_expected": True,
        }
    ],
    "reused_projects": [PROJECT],
}
assert MODULE.build_plan(observation([]), REGISTRY)["candidates"] == []
assert MODULE.build_plan(observation([]), REGISTRY)["profile"] == "exact-head-shadow:none"
assert (
    MODULE.build_plan(observation([{**valid, "selected_shard_count": 2}]), REGISTRY)["profile"]
    == "exact-head-shadow:two-shard"
)

for invalid in (
    {**valid, "project_suffix": "wrong"},
    {**valid, "selected_shard_count": 1},
    {**valid, "identity": "wrong"},
):
    try:
        MODULE.build_plan(observation([invalid]), REGISTRY)
        raise AssertionError("invalid observer producer was accepted")
    except ValueError:
        pass

try:
    MODULE.build_plan(observation([valid, valid]), REGISTRY)
    raise AssertionError("duplicate observer producer was accepted")
except ValueError:
    pass

for unsafe_project in ("../escape.csproj", "/tmp/escape.csproj", "tests/Bad'Path.csproj"):
    try:
        MODULE.build_plan(
            observation([{**valid, "project": unsafe_project}]),
            {
                "projects": [
                    {
                        "csproj": unsafe_project,
                        "artifact_suffix": "server",
                        "proof_filter": "FullyQualifiedName~ServerProofTests",
                    }
                ]
            },
        )
        raise AssertionError("unsafe project path was accepted")
    except ValueError:
        pass

print("server-test-prebuild-parity-plan=ok")
