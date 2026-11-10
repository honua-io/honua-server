"""PR Gate time budget for the affected-shard selection.

A shard slower than `max_shard_minutes` would set the gate's wall clock on its own, and a selection
past `max_total_minutes` holds more runners than one pull request may. Both stay in the trunk
matrix, so they are deferred and named instead of silently dropped.
"""

from __future__ import annotations

from typing import Any, Sequence

TIME_BUDGET_KEY = "pr_time_budget"
DEFERRED_SKIP_REASON = "affected_shards_deferred_to_trunk"
BUDGET_FIELDS = ("max_shard_minutes", "max_total_minutes")


class TimeBudgetError(ValueError):
    """The shard map's time budget is missing or malformed."""


def load_time_budget(config: dict[str, Any]) -> dict[str, float]:
    policy = config.get(TIME_BUDGET_KEY)
    if not isinstance(policy, dict):
        raise TimeBudgetError(f"the shard map declares no {TIME_BUDGET_KEY}")
    budget = {}
    for field in BUDGET_FIELDS:
        value = policy.get(field)
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0:
            raise TimeBudgetError(f"{TIME_BUDGET_KEY}.{field} must be a positive number")
        budget[field] = float(value)
    return budget


def projected_minutes(selected: Sequence[dict[str, Any]]) -> dict[str, float]:
    """Runner-minute and wall-clock estimates for a parallel fan-out sized by `dispatch_rank`."""
    return {
        "projected_runner_minutes": round(sum(entry["dispatch_rank"] for entry in selected), 1),
        "projected_wall_clock_minutes": round(
            max((entry["dispatch_rank"] for entry in selected), default=0.0), 1
        ),
    }


def apply_time_budget(result: dict[str, Any], cap: int, budget: dict[str, float]) -> dict[str, Any]:
    """Re-spend an uncapped ranking against the cap and the budget, in ranking order."""
    selected: list[dict[str, Any]] = []
    dropped: list[str] = []
    deferred: list[dict[str, Any]] = []
    for entry in result["selected"]:
        verdict = placement(entry["dispatch_rank"], selected, cap, budget)
        if verdict == "selected":
            selected.append(entry)
        elif verdict == "dropped":
            dropped.append(entry["name"])
        else:
            deferred.append({"name": entry["name"], "minutes": entry["dispatch_rank"], "reason": verdict})
    spent = {
        **result,
        **projected_minutes(selected),
        "cap": cap,
        "selected": selected,
        "shards": [entry["name"] for entry in selected],
        "dropped": dropped + result["dropped"],
        "time_budget": budget,
        "deferred_to_trunk": deferred,
    }
    if not selected and deferred:
        spent["skip"] = True
        spent["reason"] = DEFERRED_SKIP_REASON
    return spent


def placement(minutes: float, selected: Sequence[dict[str, Any]], cap: int, budget: dict[str, float]) -> str:
    if minutes > budget["max_shard_minutes"]:
        return "over_shard_budget"
    if len(selected) >= cap:
        return "dropped"
    if sum(entry["dispatch_rank"] for entry in selected) + minutes > budget["max_total_minutes"]:
        return "over_total_budget"
    return "selected"


def deferred_summary(result: dict[str, Any]) -> list[str]:
    budget = result.get("time_budget") or {}
    lines = [
        f"Deferred to the next trunk run by the time budget (shard <= "
        f"`{budget.get('max_shard_minutes')}` min, selection <= `{budget.get('max_total_minutes')}` "
        "runner-minutes):",
        "",
        "| Shard | Measured minutes | Reason |",
        "|---|---:|---|",
    ]
    lines += [
        f"| {entry['name']} | {entry['minutes']} | `{entry['reason']}` |"
        for entry in result["deferred_to_trunk"]
    ]
    return lines
