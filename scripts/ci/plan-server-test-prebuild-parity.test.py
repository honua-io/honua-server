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


def observation(producers: list[dict], selected_shards: int = 3) -> dict:
    return {
        "contract": MODULE.OBSERVATION_CONTRACT,
        "selected_shard_count": selected_shards,
        "producers": producers,
    }


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
            "selected_shard_count": 3,
        }
    ],
    "candidates": [
        {
            "identity": "server",
            "project": PROJECT,
            "project_suffix": "server",
            "filter": "FullyQualifiedName~ServerProofTests",
            "reuse_expected": True,
            "selected_shard_count": 3,
        }
    ],
    "reused_projects": [PROJECT],
}
assert MODULE.build_plan(observation([]), REGISTRY)["candidates"] == []
assert MODULE.build_plan(observation([]), REGISTRY)["profile"] == "exact-head-shadow:none"
assert (
    MODULE.build_plan(
        observation([{**valid, "selected_shard_count": 2}], selected_shards=2), REGISTRY
    )["profile"]
    == "exact-head-shadow:two-shard"
)

for invalid in (
    {**valid, "project_suffix": "wrong"},
    {**valid, "selected_shard_count": 1},
    {**valid, "selected_shard_count": 4},
    {**valid, "selected_shard_count": True},
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

for invalid_total in (0, 101, True):
    try:
        MODULE.build_plan(observation([valid], selected_shards=invalid_total), REGISTRY)
        raise AssertionError("invalid observer shard total was accepted")
    except ValueError:
        pass

second_project = "tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj"
second_producer = {
    "identity": "geoservices",
    "project": second_project,
    "project_suffix": "geoservices",
    "selected_shard_count": 3,
}
try:
    MODULE.build_plan(
        observation([valid, second_producer], selected_shards=5),
        {
            "projects": [
                *REGISTRY["projects"],
                {
                    "csproj": second_project,
                    "artifact_suffix": "geoservices",
                    "proof_filter": "FullyQualifiedName~GeoServicesProofTests",
                },
            ]
        },
    )
    raise AssertionError("producer weights exceeding the observed shard total were accepted")
except ValueError as error:
    assert "exceed the selected shard set" in str(error)

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
