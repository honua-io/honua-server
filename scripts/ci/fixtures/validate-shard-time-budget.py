#!/usr/bin/env python3
"""Offline fixtures for the PR Gate shard time budget, against the real shard map.

Each check is paired with a failure injection, so a green run proves the check still rejects the
defect it is written for.
"""

from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location(
    "validate_affected_shards", REPO_ROOT / "scripts/ci/fixtures/validate-affected-shards.py"
)
shard_fixtures = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(shard_fixtures)
selector = shard_fixtures.selector
budget_module = selector.time_budget
FixtureError = shard_fixtures.FixtureError
TESTKIT_PROBE = ["tests/dotnet/Honua.TestKit/AffectedShardsFixtureProbe.cs"]


def select_within_budget(config: dict[str, Any], changed: list[str], descriptor: dict[str, Any]) -> dict[str, Any]:
    uncapped = shard_fixtures.select_offline(config, changed, descriptor=descriptor, cap=len(config["shards"]))
    return selector.apply_time_budget(uncapped, selector.DEFAULT_CAP, budget_module.load_time_budget(config))


def check_budget_bounds_the_selection(config: dict[str, Any]) -> None:
    """No selected shard is over the shard limit, the total fits, and every candidate is accounted for."""
    budget = budget_module.load_time_budget(config)
    result = select_within_budget(config, TESTKIT_PROBE, shard_fixtures.router(config, TESTKIT_PROBE))
    over_limit = [e["name"] for e in result["selected"] if e["dispatch_rank"] > budget["max_shard_minutes"]]
    if over_limit:
        raise FixtureError(f"the time budget selected shards over {budget['max_shard_minutes']} min: {over_limit}")
    if result["projected_runner_minutes"] > budget["max_total_minutes"]:
        raise FixtureError(f"the time budget selected {result['projected_runner_minutes']} runner-minutes")
    accounted = result["shards"] + result["dropped"] + [entry["name"] for entry in result["deferred_to_trunk"]]
    if len(accounted) != len(set(accounted)) or len(accounted) != result["candidate_count"]:
        raise FixtureError("selected, dropped and deferred shards must partition the candidates")


def check_total_budget_defers_the_overflow(config: dict[str, Any]) -> None:
    budget = budget_module.load_time_budget(config)
    near_limit = sorted(
        (s for s in config["shards"] if s["dispatch_rank"] <= budget["max_shard_minutes"]),
        key=lambda shard: -shard["dispatch_rank"],
    )[: selector.DEFAULT_CAP]
    if sum(s["dispatch_rank"] for s in near_limit) <= budget["max_total_minutes"]:
        raise FixtureError("the slowest in-budget shards fit the total budget; refresh this fixture")
    targeted = {"run_all": False, "reason": "targeted", "shards": [s["name"] for s in near_limit]}
    result = select_within_budget(config, [], targeted)
    if result["projected_runner_minutes"] > budget["max_total_minutes"]:
        raise FixtureError(f"the total budget let {result['projected_runner_minutes']} runner-minutes through")
    if not any(entry["reason"] == "over_total_budget" for entry in result["deferred_to_trunk"]):
        raise FixtureError("a selection past the total budget did not defer the overflow")


def slowest_uniquely_owned_shard(config: dict[str, Any]) -> dict[str, Any]:
    limit = budget_module.load_time_budget(config)["max_shard_minutes"]
    return next(
        shard
        for shard in sorted(config["shards"], key=lambda s: -s["dispatch_rank"])
        if shard["dispatch_rank"] > limit and shard_fixtures.unique_owned_prefix(config, shard)
    )


def check_all_deferred_selection_skips_offline(config: dict[str, Any]) -> None:
    slow = slowest_uniquely_owned_shard(config)
    probe = [f"{shard_fixtures.unique_owned_prefix(config, slow)}AffectedShardsFixtureProbe.cs"]
    result = select_within_budget(config, probe, shard_fixtures.router(config, probe))
    if result["shards"] or result["reason"] != budget_module.DEFERRED_SKIP_REASON:
        raise FixtureError(f"selection ran {result['shards']} for a diff owned only by {slow['name']!r}")


def check_all_deferred_selection_through_the_cli(config: dict[str, Any]) -> None:
    """The CLI receipt, step summary and `deferred_to_trunk` output all name the deferred shard."""
    slow = slowest_uniquely_owned_shard(config)
    probe = f"{shard_fixtures.unique_owned_prefix(config, slow)}AffectedShardsFixtureProbe.cs\n"
    with tempfile.TemporaryDirectory() as directory:
        output = Path(directory) / "github-output"
        completed = subprocess.run(
            [sys.executable, str(shard_fixtures.SELECTOR), "--changed-files", "-", "--time-budget", "--github-output"],
            input=probe, capture_output=True, text=True, cwd=str(REPO_ROOT),
            env={**os.environ, "GITHUB_OUTPUT": str(output)},
        )
        if completed.returncode != 0:
            raise FixtureError(f"selector exited {completed.returncode}: {completed.stderr[-800:]}")
        outputs = dict(line.split("=", 1) for line in output.read_text(encoding="utf-8").splitlines())
    result = json.loads(completed.stdout)
    deferred = [entry["name"] for entry in result["deferred_to_trunk"]]
    if not result["skip"] or result["reason"] != budget_module.DEFERRED_SKIP_REASON or slow["name"] not in deferred:
        raise FixtureError(f"the CLI did not defer {slow['name']!r}: {result['reason']} {deferred}")
    if json.loads(outputs.get("deferred_to_trunk", "[]")) != deferred:
        raise FixtureError("the deferred_to_trunk step output does not match the receipt")
    if slow["name"] not in completed.stderr:
        raise FixtureError("the step summary does not name the deferred shard")


def check_failure_injections(config: dict[str, Any]) -> None:
    original = selector.apply_time_budget

    def ignores_the_budget(result: dict[str, Any], cap: int, budget: dict[str, float]) -> dict[str, Any]:
        return original(result, cap, {field: float("inf") for field in budget})

    selector.apply_time_budget = ignores_the_budget
    try:
        for check in (check_budget_bounds_the_selection, check_total_budget_defers_the_overflow,
                      check_all_deferred_selection_skips_offline):
            try:
                check(config)
            except FixtureError:
                continue
            raise FixtureError(f"{check.__name__} accepted a selection that ignores the time budget")
    finally:
        selector.apply_time_budget = original

    for broken in (None, {"max_shard_minutes": 10}, {"max_shard_minutes": 0, "max_total_minutes": 45},
                   {"max_shard_minutes": True, "max_total_minutes": 45},
                   {"max_shard_minutes": "10", "max_total_minutes": 45}):
        mutated = {key: value for key, value in config.items() if key != budget_module.TIME_BUDGET_KEY}
        if broken is not None:
            mutated[budget_module.TIME_BUDGET_KEY] = broken
        try:
            budget_module.load_time_budget(mutated)
        except budget_module.TimeBudgetError:
            continue
        raise FixtureError(f"the time budget loader accepted {broken!r}")


def main() -> int:
    config = selector.load_config(shard_fixtures.CONFIG_PATH)
    check_budget_bounds_the_selection(config)
    check_total_budget_defers_the_overflow(config)
    check_all_deferred_selection_skips_offline(config)
    check_all_deferred_selection_through_the_cli(config)
    check_failure_injections(config)
    limit = budget_module.load_time_budget(config)["max_shard_minutes"]
    selectable = sum(shard["dispatch_rank"] <= limit for shard in config["shards"])
    print(f"shard-time-budget=ok (selects {selectable} and defers {len(config['shards']) - selectable} "
          f"of {len(config['shards'])} shards)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except FixtureError as error:
        print(f"::error::validate-shard-time-budget: {error}", file=sys.stderr)
        raise SystemExit(1)
