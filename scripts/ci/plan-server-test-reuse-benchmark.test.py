#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("plan-server-test-reuse-benchmark.py")
SPEC = importlib.util.spec_from_file_location("reuse_plan", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

SERVER = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
GEO = "tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj"


def registry() -> dict:
    return {
        "projects": [
            {"csproj": SERVER, "artifact_suffix": "server", "proof_filter": "FullyQualifiedName~Server"},
            {"csproj": GEO, "artifact_suffix": "geo", "proof_filter": "FullyQualifiedName~Geo"},
        ]
    }


class PlanTests(unittest.TestCase):
    def test_core_reuses_only_repeated_projects(self) -> None:
        config = {
            "contract_version": 1,
            "shards": [
                {"name": "server-a", "project": SERVER},
                {"name": "server-b", "project": SERVER},
                {"name": "geo", "project": GEO},
            ],
            "profiles": [{"name": "all", "shards": ["server-a", "server-b", "geo"]}],
        }
        plan = MODULE.build_plan(config, {"shards": []}, registry(), "core")
        self.assertEqual(["server"], [item["identity"] for item in plan["producers"]])
        self.assertEqual(
            ["server-a", "server-b"], [item["identity"] for item in plan["reused_consumers"]]
        )
        self.assertEqual("FullyQualifiedName~Geo", plan["baseline"][2]["filter"])

    def test_observed_full_uses_real_filters_and_project_counts(self) -> None:
        shards = {
            "shards": [
                {"artifact_suffix": "one", "filter": "F=1"},
                {"artifact_suffix": "two", "filter": "F=2"},
                {"artifact_suffix": "geo", "csproj": GEO, "filter": "F=3"},
            ]
        }
        plan = MODULE.build_plan({"contract_version": 1}, shards, registry(), "observed-full")
        self.assertEqual(["server"], [item["identity"] for item in plan["producers"]])
        self.assertEqual("F=2", plan["baseline"][1]["filter"])
        self.assertEqual(["one", "two"], [item["identity"] for item in plan["reused_consumers"]])

    def test_rejects_unknown_project_and_duplicate_identity(self) -> None:
        base = {
            "contract_version": 1,
            "profiles": [{"name": "x", "shards": ["same"]}],
            "shards": [{"name": "same", "project": "missing.csproj"}],
        }
        with self.assertRaisesRegex(ValueError, "unregistered"):
            MODULE.build_plan(base, {"shards": []}, registry(), "core")
        base["shards"] = [
            {"name": "same", "project": SERVER},
            {"name": "same", "project": SERVER},
        ]
        with self.assertRaisesRegex(ValueError, "unique"):
            MODULE.build_plan(base, {"shards": []}, registry(), "core")


if __name__ == "__main__":
    unittest.main()
