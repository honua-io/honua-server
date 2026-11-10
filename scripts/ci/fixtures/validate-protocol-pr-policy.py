#!/usr/bin/env python3
"""Prove nightly OGC and bounded GeoServices PR routing against live configuration."""
from __future__ import annotations

import copy
import importlib.util
from pathlib import Path
from typing import Any

spec = importlib.util.spec_from_file_location(
    "affected_shard_fixtures", Path(__file__).with_name("validate-affected-shards.py"))
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)
FixtureError = fixtures.FixtureError
selector = fixtures.selector
select_offline = fixtures.select_offline
router = fixtures.router
GEOSERVICES_PR_LIMIT = 2


def check_ogc_nightly_policy(config: dict[str, Any]) -> None:
    """OGC-only protocol changes execute no PR shards; mixed product edits still gate."""
    nightly = {s["name"] for s in config["shards"] if s.get("nightly_only")}
    if not nightly:
        raise FixtureError("OGC nightly policy has no shards")
    probes = [
        "tests/dotnet/Honua.Server.Tests/OgcFeaturesEndpointTests.cs",
        "src/Honua.Protocols/OgcClassic/Wfs20/Wfs20Endpoints.cs",
        "src/Honua.Protocols/OgcApi/Tiles/OgcTilesEndpoints.cs",
        "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Source/Wps20/Wps20EndpointsTests.cs",
        "tests/dotnet/Honua.Protocols.OgcApi.UnitTests/Source/Records/OgcRecordsItemsLinkTests.cs",
        "tests/dotnet/Honua.Protocols.OgcClassic.ComponentTests/WfsLegacyEndpointsTests.cs",
    ]
    for probe in probes:
        result = select_offline(config, [probe], descriptor=router(config, [probe]), cap=6)
        if result["shards"]:
            raise FixtureError(f"OGC-only change selected PR shards: {result['shards']}")
    probe = [probes[0], "src/Honua.Protocols/GeoServicesRest/FeatureServer/Probe.cs"]
    descriptor = {"run_all": True, "shards": [s["name"] for s in config["shards"]]}
    mixed = select_offline(config, probe, descriptor=descriptor, cap=len(config["shards"]))
    if nightly & set(mixed["shards"]) or not mixed["shards"]:
        raise FixtureError("mixed OGC/product changes must retain product checks and exclude nightly")
    mutation = copy.deepcopy(config)
    for shard in mutation["shards"]:
        shard.pop("nightly_only", None)
    control = select_offline(mutation, [probes[0]], descriptor=router(config, [probes[0]]), cap=6)
    if not control["shards"]:
        raise FixtureError("nightly exclusion control selected nothing even without policy")

def check_geoservices_policy(config: dict[str, Any]) -> None:
    """Direct changes get the two highest relevance slots; closure-only waits."""
    names = [s["name"] for s in config["shards"] if selector.geoservices_integration(s)]
    if len(names) <= GEOSERVICES_PR_LIMIT:
        raise FixtureError("GeoServices policy needs at least three integration shards")
    descriptor = {"run_all": True, "shards": names, "reason": "fixture"}
    indirect = select_offline(config, ["src/Honua.Core/Probe.cs"], descriptor=descriptor, cap=100)
    if indirect["shards"] or set(indirect["geoservices_deferred_to_trunk"]) != set(names):
        raise FixtureError("transitive-only GeoServices integration must defer to trunk")
    changed = ["src/Honua.Protocols/GeoServicesRest/FeatureServer/Probe.cs"]
    direct = select_offline(config, changed, descriptor=descriptor, cap=100)
    if len(direct["shards"]) != GEOSERVICES_PR_LIMIT:
        raise FixtureError("direct GeoServices integration must select at most two shards")
    preferred = names[-1]
    ranked = selector.select(config=config, changed_files=changed, descriptor=descriptor,
                             affected_projects=None, cap=100, test_class_hits={preferred: 1})
    if ranked["shards"][0] != preferred:
        raise FixtureError("changed test ownership must outrank unrelated GeoServices children")
    if set(ranked["shards"]) | set(ranked["geoservices_deferred_to_trunk"]) != set(names):
        raise FixtureError("deferred GeoServices integrations must remain in the receipt")
    complete = router(config, changed)
    if len(set(complete["shards"]) & set(names)) <= GEOSERVICES_PR_LIMIT:
        raise FixtureError("post-merge router must retain the complete affected GeoServices set")


def main() -> int:
    config = selector.load_config(fixtures.CONFIG_PATH)
    check_ogc_nightly_policy(config)
    check_geoservices_policy(config)
    print("protocol-pr-policy=ok (OGC nightly, direct and transitive GeoServices)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
