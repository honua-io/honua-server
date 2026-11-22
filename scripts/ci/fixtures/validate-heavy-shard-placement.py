#!/usr/bin/env python3
"""Offline fixtures for PR Gate heavy-pool shard placement, against the real shard map.

The heavy pool runs the same shard slower than a hosted runner. Placement keeps
the longest shard of each producer lane hosted and gives the pool only shards
whose slowed estimate still fits under it. Each check has a failure injection.
"""

from __future__ import annotations

import copy
import importlib.util
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "validate_affected_shards", REPO_ROOT / "scripts/ci/fixtures/validate-affected-shards.py"
)
shard_fixtures = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(shard_fixtures)
selector = shard_fixtures.selector
budget_module = selector.time_budget
SERVER = selector.DEFAULT_TEST_PROJECT
PROBE = "tests/dotnet/Honua.TestKit/AffectedShardsFixtureProbe.cs"
OTHER = "tests/dotnet/Honua.Protocols.OgcApi.Tests/Honua.Protocols.OgcApi.Tests.csproj"


def lane(ranks: list[float], csproj: str = SERVER, prefix: str = "s") -> list[dict]:
    return [{"shard_name": f"{prefix}{index}", "csproj": csproj, "dispatch_rank": rank}
            for index, rank in enumerate(ranks)]


def heavy_names(entries: list[dict]) -> list[str]:
    return [entry["shard_name"] for entry in entries if entry["heavy"]]


class HeavyPlacementTests(unittest.TestCase):
    def test_only_shards_that_fit_under_the_longest_go_heavy(self):
        placed = budget_module.place_heavy(lane([8.0, 5.0, 4.0, 3.0, 2.0]), 1.83)
        self.assertEqual(heavy_names(placed), ["s2", "s3", "s4"])

    def test_the_longest_shard_and_a_lone_shard_stay_hosted(self):
        self.assertEqual(heavy_names(budget_module.place_heavy(lane([5.0]), 1.83)), [])
        self.assertEqual(heavy_names(budget_module.place_heavy(lane([6.0, 6.0]), 1.01)), [])
        self.assertEqual(heavy_names(budget_module.place_heavy(lane([8.0, 6.0, 5.0]), 1.83)), [])

    def test_at_most_half_the_lane_goes_heavy_shortest_first(self):
        placed = budget_module.place_heavy(lane([9.0, 1.0, 1.5, 2.0, 2.5, 3.0]), 1.83)
        self.assertEqual(heavy_names(placed), ["s1", "s2", "s3"])

    def test_unmeasured_shards_stay_hosted(self):
        placed = budget_module.place_heavy(lane([8.0, None, 0.0, 2.0]), 1.83)
        self.assertEqual(heavy_names(placed), ["s3"])

    def test_duplicate_names_keep_their_own_marks(self):
        include = lane([9.0, 2.0]) + lane([2.0], OTHER, "o")
        include[1]["shard_name"] = include[2]["shard_name"] = "same"
        self.assertEqual([entry["heavy"] for entry in selector.place_heavy_shards(include, 1.83)], [False, True, False])

    def test_each_producer_lane_is_placed_on_its_own(self):
        include = lane([9.0, 2.0]) + lane([4.0, 2.0], OTHER, "o")
        placed = selector.place_heavy_shards(include, 1.83)
        self.assertEqual([entry["shard_name"] for entry in placed], [entry["shard_name"] for entry in include])
        self.assertEqual(heavy_names(placed), ["s1", "o1"])

    def test_real_map_keeps_every_lane_longest_shard_hosted(self):
        config = selector.load_config(shard_fixtures.CONFIG_PATH)
        slowdown = budget_module.load_heavy_slowdown(config)
        everything = {"run_all": True, "reason": "infrastructure_change",
                      "shards": [shard["name"] for shard in config["shards"]]}
        result = shard_fixtures.select_offline(config, [PROBE], descriptor=everything, cap=len(config["shards"]))
        placed = selector.place_heavy_shards(selector.matrix_include(result, config), slowdown)
        for group in selector.partition_producer_matrices(placed):
            if not group:
                continue
            longest = max(entry["dispatch_rank"] or 0.0 for entry in group)
            for entry in group:
                if entry["heavy"]:
                    self.assertLessEqual(entry["dispatch_rank"] * slowdown, longest, entry["shard_name"])
            self.assertLessEqual(len(heavy_names(group)), (len(group) + 1) // 2)

    def test_slowdown_loader_rejects_a_missing_or_invalid_policy(self):
        config = selector.load_config(shard_fixtures.CONFIG_PATH)
        self.assertGreater(budget_module.load_heavy_slowdown(config), 1.0)
        for broken in (None, {}, {"heavy_slowdown": 0.5}, {"heavy_slowdown": 1}, {"heavy_slowdown": True}, {"heavy_slowdown": "1.8"}):
            mutated = copy.deepcopy(config)
            mutated.pop(budget_module.HEAVY_PLACEMENT_KEY)
            if broken is not None:
                mutated[budget_module.HEAVY_PLACEMENT_KEY] = broken
            with self.subTest(broken=broken), self.assertRaises(budget_module.TimeBudgetError):
                budget_module.load_heavy_slowdown(mutated)


class FailureInjectionTests(unittest.TestCase):
    def test_even_index_placement_is_rejected(self):
        original = budget_module.place_heavy

        def even_index(group, _slowdown):
            return [{**entry, "heavy": index % 2 == 0} for index, entry in enumerate(group)]

        budget_module.place_heavy = even_index
        try:
            result = unittest.TestResult()
            HeavyPlacementTests("test_only_shards_that_fit_under_the_longest_go_heavy").run(result)
            HeavyPlacementTests("test_the_longest_shard_and_a_lone_shard_stay_hosted").run(result)
            self.assertEqual(len(result.failures), 2)
        finally:
            budget_module.place_heavy = original


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
