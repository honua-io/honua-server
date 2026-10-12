#!/usr/bin/env python3
"""Fast offline OData unit routing checks: no inventory walk, .NET or Docker."""
from __future__ import annotations

import copy
import importlib.util
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("odata_selector", ROOT / "scripts/ci/compute-affected-shards.py")
selector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(selector)


def check_tier_sources() -> None:
    expected = {"UnitTest": "Fast", "UnitTheory": "Fast", "IntegrationTest": "Integration", "CloudTest": "Slow"}
    mapping = selector.tier_attributes(ROOT)
    assert all(mapping.get(name) == tier for name, tier in expected.items()), mapping
    # Regression injection: scanning only the original fixture project loses Fast.
    with mock.patch.object(selector, "TIER_ATTRIBUTE_DIRS", ("tests/dotnet/Honua.TestKit/Attributes",)):
        selector.tier_attributes.cache_clear()
        assert selector.tier_attributes(ROOT).get("UnitTest") is None
    selector.tier_attributes.cache_clear()


def check_shared_support() -> None:
    config = selector.load_config(ROOT / selector.DEFAULT_CONFIG)
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "shards.json"
        for prefix in ("tests/dotnet/Honua.TestKit.Abstractions/", "tests/dotnet/Honua.TestKit.Postgres/"):
            probe = [prefix + "RoutingProbe.cs"]
            path.write_text(json.dumps(config))
            result = selector.run_router(ROOT, selector.DEFAULT_ROUTER, path, probe)
            assert result["run_all"] and result["reason"] == "infrastructure_change", result
            broken = copy.deepcopy(config)
            broken["infrastructure_paths"].remove(prefix)
            path.write_text(json.dumps(broken))
            assert not selector.run_router(ROOT, selector.DEFAULT_ROUTER, path, probe)["run_all"]


def check_pre_pr_full_support() -> None:
    text = (ROOT / "scripts/ci/pre-pr-check.sh").read_text()
    block = text.split('CROSS_CUTTING_FULL_TRIGGERS=', 1)[1].split('cross_cutting_hit=', 1)[0]
    prefixes = re.findall(r"'([^']+)'", block)
    for prefix in ("tests/dotnet/Honua.TestKit.Abstractions/", "tests/dotnet/Honua.TestKit.Postgres/"):
        assert prefix in prefixes, f"pre-PR FULL escalation missing shared support {prefix}"


def check_ranking_tiers() -> None:
    # The moved suite has 14 Fast cases and 66 untyped cases. A Slow case remains
    # excluded under both policies; other integration shards retain their filter.
    methods = [f"Fast{i}" for i in range(14)] + [f"Normal{i}" for i in range(66)] + ["Slow"]
    tiers = {name: {"Fast"} for name in methods if name.startswith("Fast")}
    tiers["Slow"] = {"Slow"}
    entry = {"csproj": selector.ODATA_UNIT_PROJECT, "src": ["Probe.cs"], "methods": methods}
    with mock.patch.object(selector, "scan_tiers", return_value=({}, tiers)):
        assert len(selector.runnable_methods(ROOT, "Probe", entry)) == 80
        old = {**entry, "csproj": "tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj"}
        assert len(selector.runnable_methods(ROOT, "Probe", old)) == 66


def check_runner_filters() -> None:
    with tempfile.TemporaryDirectory() as directory:
        temporary = Path(directory)
        stub = temporary / "dotnet"
        stub.write_text("#!" + sys.executable + "\nimport json, os, sys\n"
                        "open(os.environ['HONUA_FIXTURE_CAPTURE'], 'w').write(json.dumps(sys.argv[1:]))\n"
                        "print('Passed! - Failed: 0, Passed: 80')\n")
        stub.chmod(0o755)
        for index, (project, expected_fast) in enumerate([
            (selector.ODATA_UNIT_PROJECT, False),
            (str(ROOT / selector.ODATA_UNIT_PROJECT), False),
            ("tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj", True),
            (selector.DEFAULT_TEST_PROJECT, True),
        ]):
            capture = temporary / f"args-{index}.json"
            environment = {**os.environ, "PATH": str(temporary) + os.pathsep + os.environ["PATH"],
                           "HONUA_FIXTURE_CAPTURE": str(capture), "HONUA_SERVER_TEST_SHARD_NAME": "fixture",
                           "HONUA_SERVER_TEST_FILTER": "FullyQualifiedName~OData", "HONUA_SERVER_TEST_LOG_NAME": f"fixture-{index}",
                           "HONUA_SERVER_TEST_CSPROJ": project, "HONUA_SERVER_TEST_EXCLUDE_FAST": "true",
                           "HONUA_SERVER_TEST_EXCLUDE_SLOW": "true", "HONUA_SERVER_TEST_POLL_SECONDS": "0.01",
                           "HONUA_SERVER_TEST_RESULTS_DIR": str(temporary / "results"), "HONUA_SERVER_TEST_TIMEOUT_MINUTES": ""}
            subprocess.run(["bash", str(ROOT / "scripts/ci/run-server-test-shard.sh")], env=environment,
                           check=True, capture_output=True, text=True, timeout=15)
            args = json.loads(capture.read_text())
            expression = args[args.index("--filter") + 1]
            assert ("Tier!=Fast" in expression) == expected_fast, (project, expression)
            assert "Tier!=Slow" in expression
            # Equivalent policy over the moved suite: all80 vs integration's66.
            selected = 66 + (0 if "Tier!=Fast" in expression else 14)
            assert selected == (66 if expected_fast else 80)


def main() -> None:
    check_tier_sources()
    check_shared_support()
    check_pre_pr_full_support()
    check_ranking_tiers()
    check_runner_filters()
    print("odata-unit-routing=ok (split traits, shared support escalation, all80 unit policy, existing filters preserved)")


if __name__ == "__main__":
    main()
